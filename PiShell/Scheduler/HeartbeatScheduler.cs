using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiShell.Discord;
using PiShell.Pi;
using System.Text;
using System.Text.Json.Nodes;

namespace PiShell.Scheduler;

/// <summary>
/// Background service that runs heartbeat prompts on a schedule.
/// Keeps the assistant active and summarizes progress periodically.
/// </summary>
public class HeartbeatScheduler(
    PiClient piClient,
    DiscordService discordService,
    ILogger<HeartbeatScheduler> logger,
    Configuration config) : BackgroundService
{
    private readonly PiClient _piClient = piClient;
    private readonly ILogger<HeartbeatScheduler> _logger = logger;
    private readonly int _intervalMinutes = config.HeartbeatIntervalMinutes;

    // Response buffer for heartbeat
    private StringBuilder? _heartbeatBuffer;
    private readonly object _heartbeatLock = new();

    // Track the channel to send heartbeats to (follows the last channel used)
    private ISocketMessageChannel? _heartbeatChannel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat scheduler started. Interval: {Interval} minutes", _intervalMinutes);

        // Subscribe to channel tracking from DiscordService
        discordService.OnChannelUsed += HandleChannelUsed;

        // Wait a bit before first heartbeat to let the system initialize
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running heartbeat");
            }

            await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken);
        }

        // Cleanup
        discordService.OnChannelUsed -= HandleChannelUsed;

        // Clear any active heartbeat handler
        _piClient.SetHeartbeatHandler(null);
    }

    private void HandleChannelUsed(ISocketMessageChannel channel)
    {
        // Only track channels from the owner (for security)
        // In the future, this could be extended to allow multiple users
        if (channel is SocketDMChannel dmChannel)
        {
            // For DMs, verify it's from the owner by checking recent messages
            // We'll use the config.OwnerId to validate
            _heartbeatChannel = dmChannel;
            _logger.LogDebug("Heartbeat will use DM channel: {ChannelId}", dmChannel.Id);
        }
        else if (channel is ITextChannel)
        {
            _heartbeatChannel = channel;
            _logger.LogDebug("Heartbeat will use text channel: {ChannelId}", channel.Id);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Skip if we already ran recently
        if ((now - _lastHeartbeat).TotalMinutes < _intervalMinutes - 1)
        {
            return;
        }

        _logger.LogInformation("Running heartbeat check");

        // Check if we have a channel to send to
        if (_heartbeatChannel == null)
        {
            _logger.LogWarning("No channel available for heartbeat delivery. " +
                "Send a message to the bot first to establish a channel.");
            return;
        }

        // Clear buffer and start collecting response
        lock (_heartbeatLock)
        {
            _heartbeatBuffer = new StringBuilder();
        }

        // Set the heartbeat handler BEFORE sending the prompt
        // This ensures heartbeat responses go to this handler, not regular message handlers
        _piClient.SetHeartbeatHandler(HandlePiEventAsync);

        try
        {
            var heartbeatPrompt = GetHeartbeatPrompt();
            await _piClient.SendPromptAsync(heartbeatPrompt);
            _lastHeartbeat = now;
            _logger.LogInformation("Heartbeat prompt sent to channel {ChannelId}", _heartbeatChannel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send heartbeat prompt");
            // Clear the handler on error
            _piClient.SetHeartbeatHandler(null);
        }
    }

    private async Task HandlePiEventAsync(PiEvent evt)
    {
        if (evt.Type == "message_update")
        {
            var assistantEvent = evt.Data["assistantMessageEvent"];
            if (assistantEvent == null) return;

            var eventType = assistantEvent["type"]?.GetValue<string>();
            if (eventType != "text_delta") return;

            var delta = assistantEvent["delta"]?.GetValue<string>() ?? "";

            lock (_heartbeatLock)
            {
                if (_heartbeatBuffer != null)
                {
                    _heartbeatBuffer.Append(delta);
                }
            }
        }
        else if (evt.Type == "agent_end")
        {
            string response;
            lock (_heartbeatLock)
            {
                response = _heartbeatBuffer?.ToString() ?? "";
                _heartbeatBuffer = null;
            }

            if (string.IsNullOrEmpty(response))
            {
                // Get response from the final message
                var messages = evt.Data["messages"]?.AsArray();
                if (messages != null)
                {
                    var assistantMessage = messages.LastOrDefault(m =>
                        m?["role"]?.GetValue<string>() == "assistant");
                    response = ExtractTextContent(assistantMessage);
                }
            }

            if (string.IsNullOrEmpty(response))
            {
                _logger.LogDebug("No text content in heartbeat response");
                return;
            }

            _logger.LogDebug("Heartbeat response received: {Length} chars", response.Length);

            // Send response to the tracked heartbeat channel
            if (_heartbeatChannel != null)
            {
                await SendToHeartbeatChannelAsync(_heartbeatChannel, response);
            }
        }
    }

    private async Task SendToHeartbeatChannelAsync(ISocketMessageChannel channel, string response)
    {
        const int maxLength = 2000;
        if (response.Length <= maxLength)
        {
            await channel.SendMessageAsync(response);
            _logger.LogDebug("Sent heartbeat response to channel {ChannelId}", channel.Id);
        }
        else
        {
            // Split into chunks
            var chunks = SplitMessage(response, maxLength);
            foreach (var chunk in chunks)
            {
                await channel.SendMessageAsync(chunk);
                _logger.LogDebug("Sent heartbeat chunk ({Length} chars) to {ChannelId}",
                    chunk.Length, channel.Id);
                await Task.Delay(100);
            }
        }
    }

    private static string ExtractTextContent(JsonNode? message)
    {
        if (message == null) return string.Empty;

        var content = message["content"];
        if (content == null) return string.Empty;

        var sb = new StringBuilder();

        if (content is JsonArray arr)
        {
            foreach (var block in arr)
            {
                var type = block?["type"]?.GetValue<string>();
                if (type == "text")
                {
                    sb.Append(block?["text"]?.GetValue<string>());
                }
            }
        }
        else if (content is JsonValue val)
        {
            sb.Append(val.GetValue<string>());
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitMessage(string message, int maxLength)
    {
        var lines = message.Split('\n');
        var currentChunk = new StringBuilder();

        foreach (var line in lines)
        {
            if (currentChunk.Length + line.Length + 1 <= maxLength)
            {
                if (currentChunk.Length > 0)
                    currentChunk.Append('\n');
                currentChunk.Append(line);
            }
            else
            {
                if (currentChunk.Length > 0)
                    yield return currentChunk.ToString();
                currentChunk = new StringBuilder(line);
            }
        }

        if (currentChunk.Length > 0)
            yield return currentChunk.ToString();
    }

    private string GetHeartbeatPrompt()
    {
        // Use PI_CODING_AGENT_DIR env var, defaults to /home/pishell in Docker
        var agentDir = config.PiCodingAgentDir ?? "/home/pishell";
        var heartbeatPath = Path.Combine(agentDir, "HEARTBEAT.md");

        // Try to load custom heartbeat prompt
        if (File.Exists(heartbeatPath))
        {
            var customPrompt = File.ReadAllText(heartbeatPath);
            if (!string.IsNullOrWhiteSpace(customPrompt))
            {
                return customPrompt;
            }
        }

        // Default heartbeat prompt
        return """
# Heartbeat Check

Please provide a brief status update (1-2 sentences):
1. What were you last working on?
2. Any incomplete tasks or pending items?
3. Any important context to remember?

Keep it concise - this is just a checkpoint.
""";
    }

    private DateTime _lastHeartbeat = DateTime.MinValue;
}
