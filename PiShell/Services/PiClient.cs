using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiShell.Services;

/// <summary>
/// Background service that manages the pi subprocess.
/// Automatically starts on application start and stops on shutdown.
/// </summary>
public class PiClient(
    ILogger<PiClient> logger,
    IHostApplicationLifetime lifetime,
    WorkingDirectoryOptions workingDirOptions) : BackgroundService
{
    private readonly ILogger<PiClient> _logger = logger;
    private readonly IHostApplicationLifetime _lifetime = lifetime;
    private readonly string _workingDirectory = workingDirOptions.Directory;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pendingRequests = [];
    private readonly Lock _pendingLock = new();

    public event Func<PiEvent, Task>? OnEvent;
    public event Func<string, Task>? OnLog;

    // Specialized handler for heartbeat responses (to avoid conflict with regular message handling)
    private Func<PiEvent, Task>? _heartbeatHandler;

    public void SetHeartbeatHandler(Func<PiEvent, Task>? handler)
    {
        _heartbeatHandler = handler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var piPath = FindPiExecutable();

            // Debug: log PATH contents
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            _logger.LogDebug("PATH: {Path}", pathEnv);

            if (piPath == null)
            {
                _logger.LogError("pi executable not found. Make sure pi is installed.");
                _logger.LogInformation("Searching in: {NpmPath}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
                _lifetime.StopApplication();
                return;
            }

            _logger.LogInformation("Starting pi at {Path}", piPath);
            _logger.LogInformation("Working directory: {WorkingDir}", _workingDirectory);

            // Validate and resolve working directory
            var workingDir = _workingDirectory;
            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            {
                _logger.LogWarning("Working directory '{Dir}' is invalid, using current directory", workingDir);
                workingDir = AppContext.BaseDirectory;
            }
            else
            {
                workingDir = Path.GetFullPath(workingDir);
            }

            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // Convert Windows path to Unix-style for pi environment variable
            var piHomeDir = homeDir.Replace("\\", "/");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {  
                    FileName = piPath,
                    Arguments = "--mode rpc",
                    WorkingDirectory = workingDir,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    OnLog?.Invoke($"[ERROR] {args.Data}");
                    _logger.LogError("pi error: {Data}", args.Data);
                }
            };

            _process.Start();
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;

            // Do not call BeginOutputReadLine() here. ListenForEventsAsync reads from
            // StandardOutput using ReadLineAsync. Calling BeginOutputReadLine would
            // mix asynchronous/synchronous operations on the same stream and throw
            // "Cannot mix synchronous and asynchronous operation on process stream."
            _process.BeginErrorReadLine();

            Task.Run(ListenForEventsAsync);

            // Give pi a moment to fully initialize
            await Task.Delay(500, stoppingToken);

            // Build and send the system prompt from markdown files
           // await SendSystemPromptAsync(stoppingToken);

            _logger.LogInformation("pi started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start pi");
            _lifetime.StopApplication();
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping pi...");

        _cts.Cancel();

        try
        {
            _stdin?.Close();

            if (_process != null && !_process.HasExited)
            {
                // Give the process a chance to exit gracefully
                await Task.WhenAny(
                    Task.Delay(2000, cancellationToken),
                    Task.Run(() => _process!.WaitForExit(2000))
                );

                if (!_process.HasExited)
                {
                    _process.Kill();
                    _logger.LogInformation("pi process killed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping pi process");
        }

        _process?.Dispose();
        _stdin?.Dispose();
        _stdout?.Dispose();
        _cts.Dispose();

        await base.StopAsync(cancellationToken);
    }

    private async Task ListenForEventsAsync()
    {
        try
        {
            string? line;
            while ((line = await _stdout!.ReadLineAsync(_cts.Token)) != null)
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node == null) continue;

                    var eventType = node["type"]?.GetValue<string>();
                    if (eventType == "response")
                    {
                        // Handle request/response correlation
                        var id = node["id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id))
                        {
                            lock (_pendingLock)
                            {
                                if (_pendingRequests.TryGetValue(id, out var tcs))
                                {
                                    _pendingRequests.Remove(id);
                                    tcs.TrySetResult(node);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Fire and forget event
                        var evt = ParseEvent(node);

                        // If there's a heartbeat handler, route events to it
                        // This prevents heartbeat responses from being handled by regular message handlers
                        if (_heartbeatHandler != null)
                        {
                            await _heartbeatHandler(evt);

                            // Clear heartbeat handler after agent_end
                            if (evt.Type == "agent_end")
                            {
                                _heartbeatHandler = null;
                            }
                        }
                        else
                        {
                            await (OnEvent?.Invoke(evt) ?? Task.CompletedTask);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse pi event: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in pi event listener");
        }
    }

    private static PiEvent ParseEvent(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>() ?? "unknown";
        return new PiEvent(type, node);
    }

    public Task SendPromptAsync(string message, List<PiImage>? images = null)
    {
        return SendCommandAsync(new
        {
            type = "prompt",
            message,
            images = images?.Select(i => new { type = "image", data = i.Data, mimeType = i.MimeType }).ToArray()
        });
    }

    public Task SendSteerAsync(string message)
    {
        return SendCommandAsync(new { type = "steer", message });
    }

    public Task SendFollowUpAsync(string message)
    {
        return SendCommandAsync(new { type = "follow_up", message });
    }

    public Task AbortAsync()
    {
        return SendCommandAsync(new { type = "abort" });
    }

    public Task<JsonNode> GetStateAsync()
    {
        return SendCommandWithResponseAsync(new { type = "get_state" });
    }

    public Task<JsonNode> GetMessagesAsync()
    {
        return SendCommandWithResponseAsync(new { type = "get_messages" });
    }

    public Task CompactAsync(string? customInstructions = null)
    {
        object cmd;
        if (!string.IsNullOrEmpty(customInstructions))
        {
            cmd = new { type = "compact", customInstructions };
        }
        else
        {
            cmd = new { type = "compact" };
        }
        return SendCommandAsync(cmd);
    }

    private Task SendCommandAsync(object command)
    {
        if (_stdin == null)
        {
            _logger.LogError("pi stdin not available");
            return Task.CompletedTask;
        }

        var json = JsonSerializer.Serialize(command);
        return _stdin.WriteLineAsync(json).ContinueWith((_,_) => _stdin.FlushAsync(), null, TaskContinuationOptions.OnlyOnRanToCompletion).Unwrap();
    }

    private async Task<JsonNode> SendCommandWithResponseAsync(object command)
    {
        if (_stdin == null)
        {
            throw new InvalidOperationException("pi stdin not available");
        }

        var id = Guid.NewGuid().ToString();

        var tcs = new TaskCompletionSource<JsonNode>();
        lock (_pendingLock)
        {
            _pendingRequests[id] = tcs;
        }

        var json = JsonSerializer.Serialize(new { id, type = command.GetType().GetProperty("type")?.GetValue(command)?.ToString(), command });
        await _stdin.WriteLineAsync(json);
        await _stdin.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Request timed out");
            throw;
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingRequests.Remove(id, out _);
            }
        }
    }

    private static bool ValidExecutable(string path)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
        }
        catch
        {
            return false;
        }

        return true;
        
    }

    private static string? FindPiExecutable()
    {
        // First, try to find pi via PATH using 'where' or 'which'
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = "pi",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            var output = p!.StandardOutput.ReadToEnd();
            var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            foreach (var path in paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath) && !Directory.Exists(fullPath) && ValidExecutable(fullPath))
                {
                    return fullPath;
                }
            }
        }
        catch { }

        // Check npm global path first (common for Windows)
        var npmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        if (Directory.Exists(npmPath))
        {
            var piCmd = Path.Combine(npmPath, "pi.cmd");
            if (File.Exists(piCmd))
                return piCmd;

            var piExe = Path.Combine(npmPath, "pi.exe");
            if (File.Exists(piExe))
                return piExe;

            var piPs1 = Path.Combine(npmPath, "pi.ps1");
            if (File.Exists(piPs1))
                return piPs1;
        }

        // Check common locations
        var candidates = new[]
        {
            "pi.cmd",
            "pi.exe",
            "pi.ps1",
            "pi",
            "/usr/local/bin/pi",
            "/usr/bin/pi",
            Path.Combine(AppContext.BaseDirectory, "pi.cmd"),
            Path.Combine(AppContext.BaseDirectory, "pi.exe"),
            Path.Combine(AppContext.BaseDirectory, "pi"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "bin", "pi.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "bin", "pi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "bin", "pi"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch { }
        }

        // Try searching in PATH directories
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            try
            {
                var piCmd = Path.Combine(dir, "pi.cmd");
                if (File.Exists(piCmd) && !Directory.Exists(piCmd))
                    return piCmd;

                var piExe = Path.Combine(dir, "pi.exe");
                if (File.Exists(piExe) && !Directory.Exists(piExe))
                    return piExe;

                var piPath = Path.Combine(dir, "pi");
                if (File.Exists(piPath) && !Directory.Exists(piPath))
                    return piPath;
            }
            catch { }
        }

        return null;
    }
}

public record PiEvent(string Type, JsonNode Data, string? RequestId = null);

public record PiImage(string Data, string MimeType);
