using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiCrust.Models;
using QRCoder;

namespace PiCrust.Services;

/// <summary>
/// Rabbit R1 Gateway service that handles WebSocket connections from Rabbit R1 devices.
/// Implements a simplified version of the OpenClaw gateway protocol for R1 connectivity.
/// </summary>
public class RabbitGatewayService : BackgroundService
{
    private readonly ILogger<RabbitGatewayService> _logger;
    private readonly Configuration _config;
    private readonly PiService _piService;
    
    private HttpListener? _listener;
    private readonly ConcurrentDictionary<string, RabbitDevice> _connectedDevices = new();
    private readonly ConcurrentDictionary<string, RabbitDevice> _pendingDevices = new();
    private readonly ConcurrentQueue<(RabbitDevice device, string message, string? id)> _messageQueue = new();
    private readonly SemaphoreSlim _messageProcessingSemaphore = new(1, 1);
    private string _authToken = string.Empty;
    private CancellationTokenSource? _listenerCts;

    public RabbitGatewayService(
        ILogger<RabbitGatewayService> logger,
        Configuration config,
        PiService piService)
    {
        _logger = logger;
        _config = config;
        _piService = piService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.RabbitGatewayEnabled)
        {
            _logger.LogInformation("Rabbit Gateway is disabled (RABBIT_GATEWAY_ENABLED=false)");
            return;
        }

        // Enable debug logging for troubleshooting
        _logger.LogInformation("========================================");
        _logger.LogInformation("Rabbit R1 Gateway Starting");
        _logger.LogInformation("========================================");

        // Generate auth token if not provided
        _authToken = string.IsNullOrEmpty(_config.RabbitGatewayToken)
            ? GenerateToken()
            : _config.RabbitGatewayToken;

        _logger.LogInformation("Starting Rabbit R1 Gateway on port {Port}", _config.RabbitGatewayPort);
        _logger.LogInformation("Auth token: {Token} (first 8 chars: {Prefix}...)", 
            _authToken, _authToken[..Math.Min(8, _authToken.Length)]);

        // Start WebSocket server
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        
        try
        {
            await StartWebSocketServerAsync(_listenerCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rabbit Gateway server error");
        }
    }

    private async Task StartWebSocketServerAsync(CancellationToken stoppingToken)
    {
        _listener = new HttpListener();
        
        // Different binding approaches for different platforms
        // Docker/Linux: use 0.0.0.0 (this should work in containers)
        // Windows: use + or * (requires admin or URL reservation)
        
        // Try Linux/Docker approach first
        try
        {
            _listener.Prefixes.Add($"http://0.0.0.0:{_config.RabbitGatewayPort}/");
            _listener.Start();
            _logger.LogInformation("Rabbit Gateway started on http://0.0.0.0:{Port}/", _config.RabbitGatewayPort);
        }
        catch (HttpListenerException)
        {
            // Try Windows wildcard approach
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_config.RabbitGatewayPort}/");
                _listener.Start();
                _logger.LogInformation("Rabbit Gateway started on http://+:{Port}/ (Windows mode)", _config.RabbitGatewayPort);
            }
            catch (HttpListenerException ex2)
            {
                _logger.LogError(ex2, "Failed to start Rabbit Gateway on port {Port}. " +
                    "On Windows, either run as Administrator or register URL: " +
                    "netsh http add urlacl url=http://+:{Port}/ user=Everyone", 
                    _config.RabbitGatewayPort);
                return;
            }
        }

        _logger.LogInformation("Rabbit Gateway listening on http://*:{Port}/", _config.RabbitGatewayPort);
        _logger.LogInformation("QR code available at http://<host-ip>:{Port}/qr", _config.RabbitGatewayPort);

        // Generate and display QR code
        await DisplayQrCodeAsync();

        // Handle incoming connections
        _logger.LogInformation("Starting request handling loop...");
        
        if (_listener == null)
        {
            _logger.LogError("Listener is null, cannot accept connections");
            return;
        }
        
        while (!stoppingToken.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                _logger.LogDebug("Waiting for incoming connection...");
                var context = await _listener.GetContextAsync();
                _logger.LogInformation("=== INCOMING REQUEST ===");
                _logger.LogInformation("Method: {Method}, URL: {Url}, IsWebSocket: {IsWs}", 
                    context.Request.HttpMethod, 
                    context.Request.Url,
                    context.Request.IsWebSocketRequest);
                
                if (context.Request.IsWebSocketRequest)
                {
                    _logger.LogDebug("WebSocket request detected, handling...");
                    _ = Task.Run(() => HandleWebSocketConnectionAsync(context, stoppingToken), stoppingToken);
                }
                else
                {
                    _logger.LogDebug("HTTP request detected, handling...");
                    // Handle HTTP requests (for QR code endpoint)
                    await HandleHttpRequestAsync(context);
                }
            }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                _logger.LogError(ex, "HttpListener error in main loop");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting Rabbit Gateway connection");
            }
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context)
    {
        var response = context.Response;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        
        _logger.LogDebug("HTTP request received: {Path}", path);
        
        try
        {
            if (path == "/qr" || path == "/qr.png")
            {
                // Return QR code as PNG
                var qrPayload = GetQrPayload();
                using var qrGenerator = new QRCodeGenerator();
                using var qrCode = qrGenerator.CreateQrCode(qrPayload, QRCodeGenerator.ECCLevel.M);
                var qrBitmap = new PngByteQRCode(qrCode).GetGraphic(10);
                
                response.ContentType = "image/png";
                response.StatusCode = 200;
                response.ContentLength64 = qrBitmap.Length;
                await response.OutputStream.WriteAsync(qrBitmap);
                await response.OutputStream.FlushAsync();
            }
            else if (path == "/" || path == "/status")
            {
                // Return connection info as JSON
                var info = new
                {
                    type = "rabbit-gateway",
                    version = 1,
                    status = "running",
                    port = _config.RabbitGatewayPort,
                    connectedDevices = _connectedDevices.Count,
                    pendingDevices = _pendingDevices.Count,
                    qrUrl = $"/qr"
                };
                
                response.ContentType = "application/json";
                response.StatusCode = 200;
                var json = JsonSerializer.Serialize(info);
                var buffer = Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer);
                await response.OutputStream.FlushAsync();
            }
            else
            {
                response.StatusCode = 404;
                var buffer = Encoding.UTF8.GetBytes("Not Found");
                await response.OutputStream.WriteAsync(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTTP request for {Path}", path);
            response.StatusCode = 500;
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private async Task HandleWebSocketConnectionAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        WebSocket? ws = null;
        var deviceId = string.Empty;
        
        try
        {
            _logger.LogInformation("Attempting WebSocket upgrade...");
            
            var wsContext = await context.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            
            _logger.LogInformation("WebSocket upgrade successful! New WebSocket connection to Rabbit Gateway");
            
            // Handle the connection
            deviceId = await HandleDeviceHandshakeAsync(ws, stoppingToken);
            
            if (!string.IsNullOrEmpty(deviceId))
            {
                _logger.LogInformation("Device {DeviceId} connected successfully", deviceId);
                
                // Add to connected devices
                var device = new RabbitDevice
                {
                    DeviceId = deviceId,
                    WebSocket = ws,
                    ConnectedAt = DateTime.UtcNow
                };
                _connectedDevices[deviceId] = device;
                
                // Handle messages from device
                await HandleDeviceMessagesAsync(device, stoppingToken);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket connection error for device");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket connection");
        }
        finally
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                _connectedDevices.TryRemove(deviceId, out _);
                _pendingDevices.TryRemove(deviceId, out _);
                _logger.LogInformation("Device {DeviceId} disconnected", deviceId);
            }
            
            ws?.Dispose();
        }
    }

    private async Task<string> HandleDeviceHandshakeAsync(WebSocket ws, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting device handshake...");
        
        // Per OpenClaw protocol, send a connect.challenge first
        var nonce = Guid.NewGuid().ToString();
        var challengePayload = new
        {
            type = "event",
            @event = "connect.challenge",
            payload = new
            {
                nonce = nonce,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
        
        var challengeJson = JsonSerializer.Serialize(challengePayload);
        var challengeBytes = Encoding.UTF8.GetBytes(challengeJson);
        await ws.SendAsync(new ArraySegment<byte>(challengeBytes), WebSocketMessageType.Text, true, stoppingToken);
        _logger.LogDebug("Sent connect.challenge to device");
        
        var buffer = new byte[4096];
        var segment = new ArraySegment<byte>(buffer);
        
        // Wait for connect request
        _logger.LogDebug("Waiting for connect request...");
        var result = await ws.ReceiveAsync(segment, stoppingToken);
        
        _logger.LogDebug("Received WebSocket message: {MessageType}, {Count} bytes", result.MessageType, result.Count);
        
        if (result.MessageType != WebSocketMessageType.Text)
        {
            _logger.LogWarning("Unexpected message type: {MessageType}", result.MessageType);
            return string.Empty;
        }
        
        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
        _logger.LogDebug("Received message: {Message}", message);
        
        try
        {
            var json = JsonNode.Parse(message);
            if (json == null) return string.Empty;
            
            var method = json["method"]?.GetValue<string>();
            _logger.LogDebug("Method: {Method}", method);
            
            if (method == "connect")
            {
                var auth = json["params"]?["auth"]?["token"]?.GetValue<string>();
                var clientId = json["params"]?["client"]?["id"]?.GetValue<string>();
                var role = json["params"]?["role"]?.GetValue<string>();
                var deviceId = json["params"]?["device"]?["id"]?.GetValue<string>() ?? clientId ?? Guid.NewGuid().ToString();
                
                _logger.LogInformation("Connect request from device: {DeviceId}, client: {ClientId}, role: {Role}", 
                    deviceId, clientId, role);
                
                // Validate token
                if (auth != _authToken)
                {
                    _logger.LogWarning("Invalid token attempt from device {DeviceId}", deviceId);
                    await SendErrorAsync(ws, "connect", "Invalid authentication token");
                    return string.Empty;
                }
                
                // Check if device is pending approval
                if (!_config.RabbitAutoApprove && !_pendingDevices.ContainsKey(deviceId))
                {
                    // Add to pending for manual approval
                    var pendingDevice = new RabbitDevice
                    {
                        DeviceId = deviceId,
                        WebSocket = ws,
                        ConnectedAt = DateTime.UtcNow,
                        Role = role ?? "unknown"
                    };
                    _pendingDevices[deviceId] = pendingDevice;
                    
                    // Send pending response
                    await SendResponseAsync(ws, json["id"]?.GetValue<string>(), new
                    {
                        type = "res",
                        ok = false,
                        error = new { code = "pending_approval", message = "Device pending approval. Use 'rabbit approve <deviceId>' or enable RABBIT_AUTO_APPROVE." }
                    });
                    
                    _logger.LogInformation("Device {DeviceId} pending approval", deviceId);
                    
                    // Wait for approval
                    var approved = await WaitForApprovalAsync(deviceId, stoppingToken);
                    
                    if (!approved)
                    {
                        return string.Empty;
                    }
                }
                
                // Send hello-ok response
                await SendResponseAsync(ws, json["id"]?.GetValue<string>(), new
                {
                    type = "hello-ok",
                    protocol = 1,
                    policy = new { tickIntervalMs = 15000 },
                    auth = new
                    {
                        deviceToken = GenerateToken(),
                        role = role ?? "operator",
                        scopes = new[] { "operator.read", "operator.write" }
                    }
                });
                
                return deviceId;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse device handshake");
        }
        
        return string.Empty;
    }

    private async Task<bool> WaitForApprovalAsync(string deviceId, CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var start = DateTime.UtcNow;
        
        while (DateTime.UtcNow - start < timeout && !stoppingToken.IsCancellationRequested)
        {
            if (_connectedDevices.ContainsKey(deviceId))
            {
                return true;
            }
            
            await Task.Delay(1000, stoppingToken);
        }
        
        return false;
    }

    private async Task HandleDeviceMessagesAsync(RabbitDevice device, CancellationToken stoppingToken)
    {
        var buffer = new byte[8192];
        var segment = new ArraySegment<byte>(buffer);
        
        while (device.WebSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await device.WebSocket.ReceiveAsync(segment, stoppingToken);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await device.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", stoppingToken);
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleDeviceMessageAsync(device, message, stoppingToken);
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving message from device {DeviceId}", device.DeviceId);
                break;
            }
        }
    }

    private async Task HandleDeviceMessageAsync(RabbitDevice device, string message, CancellationToken stoppingToken)
    {
        try
        {
            var json = JsonNode.Parse(message);
            if (json == null) return;
            
            var method = json["method"]?.GetValue<string>();
            var id = json["id"]?.GetValue<string>();
            
            _logger.LogDebug("Device {DeviceId} sent method: {Method}", device.DeviceId, method);
            
            switch (method)
            {
                case "agent.prompt":
                case "agent.start":
                    await HandleAgentPromptAsync(device, json, id, stoppingToken);
                    break;
                    
                case "agent.stop":
                    await _piService.AbortAsync();
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { result = "aborted" } });
                    break;
                    
                case "ping":
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { pong = true } });
                    break;
                    
                case "device.list":
                    await SendResponseAsync(device.WebSocket, id, new
                    {
                        type = "res",
                        ok = true,
                        payload = new
                        {
                            connected = _connectedDevices.Values.Select(d => new { deviceId = d.DeviceId, connectedAt = d.ConnectedAt }),
                            pending = _pendingDevices.Values.Select(d => new { deviceId = d.DeviceId, connectedAt = d.ConnectedAt })
                        }
                    });
                    break;
                    
                case "device.approve":
                    var approveDeviceId = json["params"]?["deviceId"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(approveDeviceId) && _pendingDevices.TryRemove(approveDeviceId, out var approvedDevice))
                    {
                        _connectedDevices[approveDeviceId] = approvedDevice;
                        await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { approved = true } });
                    }
                    else
                    {
                        await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = false, error = new { code = "not_found", message = "Device not found" } });
                    }
                    break;
                    
                case "device.revoke":
                    var revokeDeviceId = json["params"]?["deviceId"]?.GetValue<string>();
                    if (_connectedDevices.TryRemove(revokeDeviceId, out var revokedDevice))
                    {
                        await revokedDevice.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Revoked", stoppingToken);
                        await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { revoked = true } });
                    }
                    else
                    {
                        await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = false, error = new { code = "not_found", message = "Device not found" } });
                    }
                    break;
                    
                default:
                    _logger.LogDebug("Unhandled method: {Method}", method);
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = false, error = new { code = "not_implemented", message = $"Method {method} not implemented" } });
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse device message");
        }
    }

    private async Task HandleAgentPromptAsync(RabbitDevice device, JsonNode json, string? id, CancellationToken stoppingToken)
    {
        var message = json["params"]?["message"]?.GetValue<string>();
        
        if (string.IsNullOrEmpty(message))
        {
            await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = false, error = new { code = "invalid_request", message = "Message is required" } });
            return;
        }
        
        // Create a completion source to track when the agent finishes
        var agentDone = new TaskCompletionSource<bool>();
        var requestId = Guid.NewGuid().ToString();
        
        // Set up event handler to forward to device and track completion
        Func<PiEvent, Task>? handler = null;
        handler = async evt =>
        {
            if (device.WebSocket?.State == WebSocketState.Open)
            {
                var eventPayload = evt.Data.ToJsonString();
                await SendEventAsync(device.WebSocket, evt.Type, JsonNode.Parse(eventPayload));
                
                // Check if this is the end of our request
                if (evt.Type == "agent_end" || evt.Type == "error")
                {
                    agentDone.TrySetResult(true);
                }
            }
        };
        
        _piService.OnEvent += handler;
        
        try
        {
            // Send to pi agent
            await _piService.SendPromptAsync(message);
            
            // Wait for agent to complete or timeout after 5 minutes
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            
            try
            {
                await agentDone.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timeout - agent is still processing
                _logger.LogWarning("Agent prompt timed out for device {DeviceId}", device.DeviceId);
            }
            
            await SendResponseAsync(device.WebSocket, id, new
            {
                type = "res",
                ok = true,
                payload = new { result = "message_processed" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing prompt for device {DeviceId}", device.DeviceId);
            await SendResponseAsync(device.WebSocket, id, new 
            { 
                type = "res", 
                ok = false, 
                error = new { code = "processing_error", message = ex.Message } 
            });
        }
        finally
        {
            if (handler != null)
            {
                _piService.OnEvent -= handler;
            }
        }
    }

    private static async Task SendResponseAsync(WebSocket ws, string? id, object response)
    {
        if (ws.State != WebSocketState.Open) return;
        
        var json = JsonSerializer.Serialize(response);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendEventAsync(WebSocket ws, string eventType, JsonNode? payload)
    {
        if (ws.State != WebSocketState.Open) return;
        
        var evt = new { type = "event", @event = eventType, payload };
        var json = JsonSerializer.Serialize(evt);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendErrorAsync(WebSocket ws, string method, string error)
    {
        var response = new { type = "res", ok = false, error = new { code = "auth_error", message = error } };
        await SendResponseAsync(ws, null, response);
    }

    private async Task DisplayQrCodeAsync()
    {
        var qrPayload = GetQrPayload();
        
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              Rabbit R1 Gateway - QR Code                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Scan this QR code with your Rabbit R1 to connect:");
        Console.WriteLine();
        
        try
        {
            // Generate a smaller QR code for better terminal display
            // Use QR code art generator
            var qrArt = GenerateQrCodeArt(qrPayload);
            Console.WriteLine(qrArt);
            
            // Also save PNG for mobile devices
            using var qrGenerator = new QRCodeGenerator();
            using var qrCode = qrGenerator.CreateQrCode(qrPayload, QRCodeGenerator.ECCLevel.L);
            var qrBitmap = new PngByteQRCode(qrCode).GetGraphic(10);
            var qrPath = Path.Combine(AppContext.BaseDirectory, "rabbit-gateway-qr.png");
            System.IO.File.WriteAllBytes(qrPath, qrBitmap);
            Console.WriteLine($"PNG saved to: {qrPath}");
            
            // Make it accessible to host via bind mount
            var hostQrPath = "/home/picrust/rabbit-gateway-qr.png";
            try { System.IO.File.WriteAllBytes(hostQrPath, qrBitmap); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate QR code");
            Console.WriteLine("(QR code generation not available)");
        }
        
        Console.WriteLine();
        Console.WriteLine("To scan with R1, either:");
        Console.WriteLine($"  1. Copy the PNG file from: /home/picrust/rabbit-gateway-qr.png");
        Console.WriteLine($"  2. Or access via host machine: http://<host-ip>:{_config.RabbitGatewayPort}/qr");
        Console.WriteLine();
        Console.WriteLine("Connection Info:");
        Console.WriteLine($"  Port: {_config.RabbitGatewayPort}");
        Console.WriteLine($"  Token: {_authToken[..Math.Min(8, _authToken.Length)]}...");
        Console.WriteLine();
        
        var ips = GetLocalIPAddresses();
        if (ips.Any())
        {
            Console.WriteLine("LAN IP Addresses (use one of these for R1):");
            foreach (var ip in ips)
            {
                Console.WriteLine($"  - {ip}");
            }
        }
        
        Console.WriteLine();
        
        if (!_config.RabbitAutoApprove)
        {
            Console.WriteLine("Note: First connection requires approval.");
            Console.WriteLine("  Use 'device.approve' method or enable RABBIT_AUTO_APPROVE=true");
            Console.WriteLine();
        }
    }

    private string GetQrPayload()
    {
        var ips = GetLocalIPAddresses();
        
        _logger.LogInformation("Detected local IP addresses: {IPs}", string.Join(", ", ips));
        
        var payload = new
        {
            type = "clawdbot-gateway",
            version = 1,
            ips = ips,
            port = _config.RabbitGatewayPort,
            token = _authToken,
            protocol = "ws"
        };
        
        var json = JsonSerializer.Serialize(payload);
        _logger.LogInformation("=== QR CODE PAYLOAD ===");
        _logger.LogInformation("{Payload}", json);
        _logger.LogInformation("======================");
        
        return json;
    }

    /// <summary>
    /// Generates a simple ASCII art representation of a QR code placeholder.
    /// </summary>
    private static string GenerateQrCodeArt(string payload)
    {
        // Since QR rendering libraries have limited terminal support,
        // we show a simple visual placeholder. The actual QR code
        // is available as PNG and via HTTP endpoint.
        
        // Create a simple visual representation based on the token
        var token = payload.GetHashCode().ToString("X8");
        
        var sb = new StringBuilder();
        sb.AppendLine("┌──────────────────────────────┐");
        sb.AppendLine("│  ████████  ████████  ████████ │");
        sb.AppendLine("│  ██    ██ ██    ██ ██      ██ │");
        sb.AppendLine("│  ██    ██ ██    ██ ██      ██ │");
        sb.AppendLine("│  ██    ██ ██    ██ ██      ██ │");
        sb.AppendLine("│  ████████  ████████  ████████ │");
        sb.AppendLine("│  ██      ██      ██ ██      ██ │");
        sb.AppendLine("│  ██      ██      ██ ██      ██ │");
        sb.AppendLine("│  ██      ██      ██ ██      ██ │");
        sb.AppendLine("│  ████████  ████████  ████████ │");
        sb.AppendLine("└──────────────────────────────┘");
        
        return sb.ToString();
    }

    private static List<string> GetLocalIPAddresses()
    {
        var ips = new List<string>();
        
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !ip.ToString().StartsWith("127.") &&
                    !ip.ToString().StartsWith("169.254."))
                {
                    ips.Add(ip.ToString());
                }
            }
        }
        catch { }
        
        // Also try network interfaces
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !addr.Address.ToString().StartsWith("127.") &&
                            !addr.Address.ToString().StartsWith("169.254."))
                        {
                            var ipStr = addr.Address.ToString();
                            if (!ips.Contains(ipStr))
                            {
                                ips.Add(ipStr);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        
        return ips.Distinct().ToList();
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Rabbit Gateway...");
        
        _listener?.Stop();
        _listener?.Close();
        _listenerCts?.Cancel();
        
        // Close all connected devices
        foreach (var device in _connectedDevices.Values)
        {
            try
            {
                if (device.WebSocket.State == WebSocketState.Open)
                {
                    await device.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", cancellationToken);
                }
            }
            catch { }
        }
        
        _connectedDevices.Clear();
        _pendingDevices.Clear();
        
        await base.StopAsync(cancellationToken);
    }

    private class RabbitDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public WebSocket? WebSocket { get; set; }
        public DateTime ConnectedAt { get; set; }
        public string Role { get; set; } = "operator";
    }
}
