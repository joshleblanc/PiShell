using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiShell.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace PiShell.Services;

/// <summary>
/// Discord bot service that relays messages between Discord and pi.
/// </summary>
public class DiscordService(
    DiscordSocketClient client,
    PiClient piClient,
    ILogger<DiscordService> logger,
    Configuration config) : BackgroundService
{
    private readonly DiscordSocketClient _client = client;
    private readonly PiClient _piClient = piClient;
    private readonly ILogger<DiscordService> _logger = logger;

    // Track pending requests: message ID -> channel/response info
    private readonly ConcurrentDictionary<ulong, PendingRequest> _pendingRequests = new();

    // Response buffer per message
    private readonly ConcurrentDictionary<ulong, StringBuilder> _responseBuffers = new();

    // Track the last used channel for system messages (heartbeats, etc.)
    private ISocketMessageChannel? _lastChannel;

    // Event that other services can subscribe to for channel tracking
    public event Action<ISocketMessageChannel>? OnChannelUsed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discord service starting...");

        _client.Log += msg =>
        {
            _logger.LogDebug("Discord: {Message}", msg.Message);
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageReceivedAsync;
        _client.Ready += () =>
        {
            _logger.LogInformation("Discord bot connected as {Username}", _client.CurrentUser.Username);
            return Task.CompletedTask;
        };

        // Subscribe to pi events
        _piClient.OnEvent += HandlePiEventAsync;

        // Login and start
        await _client.LoginAsync(TokenType.Bot, config.DiscordToken, true);
        await _client.StartAsync();

        // Keep running until cancelled
        await Task.Delay(-1, stoppingToken);
    }

    private async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        // Ignore own messages
        if (message.Author.Id == _client.CurrentUser?.Id) return;

        // Only handle user messages
        if (message is not SocketUserMessage userMessage) return;

        // Track this channel as the last used channel (for heartbeats, etc.)
        if (userMessage.Channel is ISocketMessageChannel channel)
        {
            _lastChannel = channel;
            OnChannelUsed?.Invoke(channel);
            _logger.LogDebug("Tracking channel: {ChannelType} ({ChannelId})",
                channel.GetType().Name, channel.Id);
        }

        // Check if we should respond
        if (!ShouldRespond(userMessage))
        {
            _logger.LogDebug("Ignoring message: {Content}", Truncate(message.Content, 100));
            return;
        }

        _logger.LogInformation("Received message from {User}: {Content}",
            message.Author.Username,
            Truncate(message.Content, 100));

        // Track this request for response
        var pendingRequest = new PendingRequest
        {
            Channel = message.Channel,
            OriginalMessage = userMessage,
            StartedAt = DateTime.UtcNow
        };
        _pendingRequests[message.Id] = pendingRequest;
        _responseBuffers[message.Id] = new StringBuilder();

        // Extract images if any
        var images = await ExtractImagesAsync(userMessage);

        // Send to pi
        await _piClient.SendPromptAsync(userMessage.Content, images);
    }

    private bool ShouldRespond(SocketUserMessage message)
    {
        if(message.Author.Id != config.OwnerId)
        {
            return false;
        }
        // DM to the bot 
        if (message.Channel is SocketDMChannel)
        {
            return true;
        }
        else
        {
            return false;
        }


        //// Direct mentions
        //if (message.MentionedUsers.Any(u => u.Id == _client.CurrentUser?.Id))
        //    return true;

        //// Command prefix
        //if (!string.IsNullOrEmpty(_settings.CommandPrefix) &&
        //    message.Content.StartsWith(_settings.CommandPrefix))
        //    return true;
        //// If configured to respond to all messages in a specific channel
        //if (!string.IsNullOrEmpty(_settings.ChannelId))
        //{
        //    if (message.Channel.Id.ToString() == _settings.ChannelId)
        //        return true;
        //}

        //return false;
    }

    private async Task<List<PiImage>> ExtractImagesAsync(SocketUserMessage message)
    {
        var images = new List<PiImage>();

        foreach (var attachment in message.Attachments)
        {
            if (IsImage(attachment.Filename))
            {
                try
                {
                    var httpClient = new HttpClient();
                    var data = await httpClient.GetByteArrayAsync(attachment.Url);
                    var base64 = Convert.ToBase64String(data);
                    var mimeType = GetMimeType(attachment.Filename);

                    images.Add(new PiImage(base64, mimeType));
                    _logger.LogDebug("Loaded image: {Filename}", attachment.Filename);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load image: {Filename}", attachment.Filename);
                }
            }
        }

        return images;
    }

    private async Task HandlePiEventAsync(PiEvent evt)
    {
        switch (evt.Type)
        {
            case "message_update":
                await HandleMessageUpdateAsync(evt.Data);
                break;
            case "agent_end":
                await HandleAgentEndAsync(evt.Data);
                break;
            case "tool_execution_start":
                _logger.LogDebug("Tool execution: {Tool}", evt.Data["toolName"]);
                break;
            case "tool_execution_end":
                _logger.LogDebug("Tool execution completed: {Tool}", evt.Data["toolName"]);
                break;
        }
    }

    private async Task HandleMessageUpdateAsync(JsonNode data)
    {
        var assistantEvent = data["assistantMessageEvent"];
        if (assistantEvent == null) return;

        var eventType = assistantEvent["type"]?.GetValue<string>();
        if (eventType != "text_delta") return;

        var delta = assistantEvent["delta"]?.GetValue<string>() ?? "";

        // Find the oldest pending request and append to its buffer
        var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).FirstOrDefault();
        if (oldestRequest.Value != null)
        {
            _responseBuffers.AddOrUpdate(oldestRequest.Key,
                new StringBuilder(delta),
                (_, existing) => existing.Append(delta));
        }
    }

    private async Task HandleAgentEndAsync(JsonNode data)
    {
        // Find the oldest pending request
        if (!_pendingRequests.Any())
        {
            _logger.LogDebug("No pending requests, ignoring agent_end");
            return;
        }

        var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).First();
        var requestId = oldestRequest.Key;
        var pendingRequest = oldestRequest.Value;

        // Get the final response text
        var messages = data["messages"]?.AsArray();
        if (messages == null) return;

        var assistantMessage = messages.LastOrDefault(m =>
            m?["role"]?.GetValue<string>() == "assistant");

        if (assistantMessage == null) return;

        var responseText = ExtractTextContent(assistantMessage);

        // Use streamed response if available, otherwise use final message
        var finalResponse = responseText;
        if (_responseBuffers.TryRemove(requestId, out var buffer))
        {
            if (buffer.Length > 0)
            {
                finalResponse = buffer.ToString();
            }
        }

        if (string.IsNullOrEmpty(finalResponse))
        {
            _logger.LogDebug("No text content in assistant response");
            _pendingRequests.TryRemove(requestId, out _);
            return;
        }

        _logger.LogInformation("pi response: {Response}", Truncate(finalResponse, 200));

        // Send response back to Discord
        try
        {
            await SendResponseToDiscordAsync(pendingRequest, finalResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response to Discord");
        }
        finally
        {
            // Clean up
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task SendResponseToDiscordAsync(PendingRequest request, string response)
    {
        // DM Channel - use SocketDMChannel directly
        if (request.Channel is SocketDMChannel dmChannel)
        {
            await SendMessageToDMChannelAsync(dmChannel, response);
            return;
        }

        // Try to reply to the original message first
        if (request.OriginalMessage != null)
        {
            try
            {
                await request.OriginalMessage.ReplyAsync(response);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reply to original message, trying channel");
            }
        }

        // Fall back to last used channel (for heartbeats or lost context)
        if (_lastChannel != null)
        {
            _logger.LogDebug("Falling back to last used channel: {ChannelId}", _lastChannel.Id);
            if (_lastChannel is SocketDMChannel fallbackDmChannel)
            {
                await SendMessageToDMChannelAsync(fallbackDmChannel, response);
            }
            else if (_lastChannel is ITextChannel fallbackTextChannel)
            {
                await SendMessageToTextChannelAsync(fallbackTextChannel, response);
            }
            return;
        }

        // Fall back to sending in the channel (guild text channels)
        if (request.Channel is ITextChannel textChannel)
        {
            await SendMessageToTextChannelAsync(textChannel, response);
        }
    }

    private async Task SendMessageToDMChannelAsync(SocketDMChannel dmChannel, string response)
    {
        const int maxLength = 2000;
        if (response.Length <= maxLength)
        {
            await dmChannel.SendMessageAsync(response);
            _logger.LogDebug("Sent response to DM channel");
        }
        else
        {
            // Split into chunks
            var chunks = SplitMessage(response, maxLength);
            foreach (var chunk in chunks)
            {
                await dmChannel.SendMessageAsync(chunk);
                _logger.LogDebug("Sent DM chunk ({Length} chars)", chunk.Length);
                // Small delay between chunks to avoid rate limiting
                await Task.Delay(100);
            }
        }
    }

    private async Task SendMessageToTextChannelAsync(ITextChannel textChannel, string response)
    {
        const int maxLength = 2000;
        if (response.Length <= maxLength)
        {
            await textChannel.SendMessageAsync(response);
        }
        else
        {
            // Split into chunks
            var chunks = SplitMessage(response, maxLength);
            foreach (var chunk in chunks)
            {
                await textChannel.SendMessageAsync(chunk);
                // Small delay between chunks to avoid rate limiting
                await Task.Delay(100);
            }
        }
    }

    private static string ExtractTextContent(JsonNode message)
    {
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

    private static bool IsImage(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    private static string GetMimeType(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }

    private static string Truncate(string s, int maxLength) =>
        string.IsNullOrEmpty(s) || s.Length <= maxLength ? s : s[..maxLength] + "...";

    private record PendingRequest
    {
        public ISocketMessageChannel? Channel { get; init; }
        public SocketUserMessage? OriginalMessage { get; init; }
        public DateTime StartedAt { get; init; }
    }
}
