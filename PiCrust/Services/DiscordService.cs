using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiCrust.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace PiCrust.Services;

/// <summary>
/// Discord bot service that relays messages between Discord and pi.
/// </summary>
public class DiscordService(
    DiscordSocketClient client,
    PiService piClient,
    ILogger<DiscordService> logger,
    Configuration config) : BackgroundService
{
    private readonly DiscordSocketClient _client = client;
    private readonly PiService _piClient = piClient;
    private readonly ILogger<DiscordService> _logger = logger;

    // Track pending requests: message ID -> channel/response info
    private readonly ConcurrentDictionary<ulong, PendingRequest> _pendingRequests = new();

    // Response buffer per message
    private readonly ConcurrentDictionary<ulong, StringBuilder> _responseBuffers = new();

    // Track sent Discord messages for streaming updates: Discord message ID -> request message ID
    private readonly ConcurrentDictionary<ulong, ulong> _discordMessagesToRequests = new();

    // Track the last used channel for system messages (heartbeats, etc.)
    private ISocketMessageChannel? _lastChannel;

    // Event that other services can subscribe to for channel tracking
    public event Action<ISocketMessageChannel>? OnChannelUsed;

    private IDisposable? _typing;

    // Track whether a runtime reload is needed after the current agent run
    private bool _reloadNeeded = false;

    // Streaming configuration
    private const int FlushThreshold = 500; // Characters before flushing a chunk (lowered for more responsive streaming)
    private const int MinChunkSize = 100;    // Minimum chunk size to avoid too many small messages
    

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
            StartedAt = DateTime.UtcNow,
        };
        _typing ??= message.Channel.EnterTypingState();
        _pendingRequests[message.Id] = pendingRequest;
        _responseBuffers[message.Id] = new StringBuilder();

        // Extract images if any
        var images = await ExtractImagesAsync(userMessage);

        // Send to pi - runs asynchronously, events stream to Discord in real-time
        await _piClient.SendPromptFireAndForgetAsync(userMessage.Content, images);
        
        // Don't wait for agent_end - let it run in background
        // The events will still stream via OnEvent handler
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
                // Notify Discord that a tool is starting
                await NotifyToolExecutionAsync(evt.Data, isStart: true);
                break;
            case "tool_execution_update":
                // Stream tool output in real-time
                await HandleToolExecutionUpdateAsync(evt.Data);
                break;
            case "tool_execution_end":
                HandleToolExecutionEnd(evt.Data);
                // Notify Discord that tool completed
                await NotifyToolExecutionAsync(evt.Data, isStart: false);
                break;
        }
    }

    /// <summary>
    /// Handles tool execution update events - streams tool output to Discord in real-time.
    /// This is the key to getting immediate feedback during long-running tasks like browsing.
    /// </summary>
    private async Task HandleToolExecutionUpdateAsync(JsonNode data)
    {
        var toolName = data["toolName"]?.GetValue<string>();
        var partialResult = data["partialResult"];

        if (partialResult == null) return;

        // Find the oldest pending request
        if (!_pendingRequests.Any()) return;

        var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).First();
        var requestId = oldestRequest.Key;
        var pendingRequest = oldestRequest.Value;

        // Extract the partial content from the tool
        var content = partialResult["content"];
        if (content == null) return;

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

        var toolOutput = sb.ToString();
        if (string.IsNullOrEmpty(toolOutput)) return;

        // Append to buffer and flush if needed
        var buffer = _responseBuffers.AddOrUpdate(requestId,
            new StringBuilder(toolOutput),
            (_, existing) => existing.Append(toolOutput));

        // Flush more frequently for tool output (lower threshold)
        var toolFlushThreshold = 500; // Lower threshold for tool output
        if (buffer.Length >= toolFlushThreshold)
        {
            var textToSend = buffer.ToString();
            buffer.Clear();

            var sentMessage = await SendStreamChunkToDiscordAsync(pendingRequest, 
                $"⚙️ **{toolName}** output:\n{textToSend}", isFirst: false);
            
            _logger.LogDebug("Streamed tool output ({Length} chars) to Discord", textToSend.Length);
        }
    }

    /// <summary>
    /// Notifies Discord about tool execution start/end.
    /// </summary>
    private async Task NotifyToolExecutionAsync(JsonNode data, bool isStart)
    {
        if (!_pendingRequests.Any()) return;

        var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).First();
        var pendingRequest = oldestRequest.Value;

        var toolName = data["toolName"]?.GetValue<string>();
        var statusMessage = isStart 
            ? $"🔧 **{toolName}** starting..."
            : $"✅ **{toolName}** completed";

        try
        {
            if (pendingRequest.Channel is SocketDMChannel dmChannel)
            {
                await dmChannel.SendMessageAsync(statusMessage);
            }
            else if (pendingRequest.Channel is ITextChannel textChannel)
            {
                await textChannel.SendMessageAsync(statusMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send tool execution notification");
        }
    }

    private void HandleToolExecutionEnd(JsonNode data)
    {
        var toolName = data["toolName"]?.GetValue<string>();
        var isError = data["isError"]?.GetValue<bool>() ?? true;

        _logger.LogDebug("Tool execution completed: {Tool} (error: {IsError})", toolName, isError);

        if (!isError && toolName is "install_package" or "uninstall_package" or "update_packages" or "reload_runtime")
        {
            _reloadNeeded = true;
            _logger.LogDebug("Runtime reload will be triggered after agent completes");
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
        if (oldestRequest.Value == null) return;

        var requestId = oldestRequest.Key;
        var pendingRequest = oldestRequest.Value;

        // Append to buffer
        var buffer = _responseBuffers.AddOrUpdate(requestId,
            new StringBuilder(delta),
            (_, existing) => existing.Append(delta));

        var currentLength = buffer.Length;

        // Stream to Discord if we've accumulated enough content
        if (currentLength >= FlushThreshold)
        {
            // Find any existing Discord messages for this request
            var existingMessageIds = _discordMessagesToRequests
                .Where(kvp => kvp.Value == requestId)
                .Select(kvp => kvp.Key)
                .ToList();

            string textToSend;

            if (existingMessageIds.Count == 0)
            {
                // First message - send as reply or new message
                textToSend = buffer.ToString();
                buffer.Clear();

                var sentMessage = await SendStreamChunkToDiscordAsync(pendingRequest, textToSend, isFirst: true);
                if (sentMessage != null)
                {
                    _discordMessagesToRequests[sentMessage.Id] = requestId;
                }

                // Stop typing briefly to show message was received, then restart
                _typing?.Dispose();
                _typing = null;
                _typing ??= pendingRequest.Channel?.EnterTypingState();
            }
            else
            {
                // Subsequent chunks - send as follow-up messages
                textToSend = buffer.ToString();
                buffer.Clear();

                var sentMessage = await SendStreamChunkToDiscordAsync(pendingRequest, textToSend, isFirst: false);
                if (sentMessage != null)
                {
                    _discordMessagesToRequests[sentMessage.Id] = requestId;
                }
            }

            _logger.LogDebug("Streamed chunk ({Length} chars) to Discord", textToSend.Length);
        }
    }

    private async Task<IMessage?> SendStreamChunkToDiscordAsync(PendingRequest request, string text, bool isFirst)
    {
        try
        {
            // DM Channel
            if (request.Channel is SocketDMChannel dmChannel)
            {
                if (isFirst)
                {
                    return await dmChannel.SendMessageAsync(text);
                }
                else
                {
                    return await dmChannel.SendMessageAsync(text);
                }
            }

            // Try to reply to the original message first (for first chunk only)
            if (isFirst && request.OriginalMessage != null)
            {
                try
                {
                    return await request.OriginalMessage.ReplyAsync(text);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reply to original message, trying channel");
                }
            }

            // Fall back to channel
            if (request.Channel is ITextChannel textChannel)
            {
                return await textChannel.SendMessageAsync(text);
            }

            // Last resort - any channel
            if (request.Channel != null)
            {
                var result = await request.Channel.SendMessageAsync(text);
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send stream chunk to Discord");
        }

        return null;
    }

    private async Task HandleAgentEndAsync(JsonNode data)
    {
        // Find the oldest pending request
        if (!_pendingRequests.Any())
        {
            _logger.LogDebug("No pending requests, ignoring agent_end");
        }
        else
        {
            var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).First();
            var requestId = oldestRequest.Key;
            var pendingRequest = oldestRequest.Value;

            // Check if we've already sent messages for this request (streaming happened)
            var existingMessageIds = _discordMessagesToRequests
                .Where(kvp => kvp.Value == requestId)
                .Select(kvp => kvp.Key)
                .ToList();

            // Process any custom messages (like discord_reaction)
            var messages = data["messages"]?.AsArray();
            if (messages != null)
            {
                foreach (var message in messages)
                {
                    if (message == null) continue;
                    
                    var customType = message["customType"]?.GetValue<string>();
                    if (customType == "discord_reaction")
                    {
                        await ProcessDiscordReactionMessageAsync(message, pendingRequest);
                    }
                }
            }

            // Get any remaining buffered content that wasn't streamed yet
            string? remainingContent = null;
            if (_responseBuffers.TryRemove(requestId, out var buffer) && buffer.Length > 0)
            {
                remainingContent = buffer.ToString();
            }

            if (existingMessageIds.Any())
            {
                // We've been streaming - send any remaining content as a follow-up
                if (!string.IsNullOrEmpty(remainingContent))
                {
                    try
                    {
                        _logger.LogInformation("Sending final streaming chunk ({Length} chars)", remainingContent.Length);
                        await SendStreamChunkToDiscordAsync(pendingRequest, remainingContent, isFirst: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send final streaming chunk");
                    }
                }
                else
                {
                    _logger.LogDebug("No remaining content to send after streaming");
                }
            }
            else if (!string.IsNullOrEmpty(remainingContent) || (messages != null))
            {
                // No streaming happened (response was small) - send everything at once
                var finalResponse = remainingContent ?? "";
                
                if (messages != null && string.IsNullOrEmpty(finalResponse))
                {
                    // Try to get text from assistant message if nothing in buffer
                    var assistantMessage = messages.LastOrDefault(m =>
                        m?["role"]?.GetValue<string>() == "assistant");
                    
                    if (assistantMessage != null)
                    {
                        finalResponse = ExtractTextContent(assistantMessage);
                    }
                }

                if (!string.IsNullOrEmpty(finalResponse))
                {
                    _logger.LogInformation("pi response: {Response}", Truncate(finalResponse, 200));
                    try
                    {
                        await SendResponseToDiscordAsync(pendingRequest, finalResponse);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send response to Discord");
                    }
                }
            }

            // Clean up tracking for this request
            foreach (var msgId in existingMessageIds)
            {
                _discordMessagesToRequests.TryRemove(msgId, out _);
            }
            _pendingRequests.TryRemove(requestId, out _);
        }

        // Trigger runtime reload if a package operation was performed
        // This runs regardless of whether there was a pending Discord request
        if (_reloadNeeded)
        {
            _reloadNeeded = false;
            _logger.LogInformation("Package operation detected, triggering runtime reload");

            // Fire restart on a separate thread — we can't await it here because
            // this code runs inside the event listener, and RestartAsync needs
            // the listener to exit first (otherwise it deadlocks).
            var channel = _lastChannel;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _piClient.RestartAsync();

                    if (channel != null)
                    {
                        await channel.SendMessageAsync("Runtime reloaded. Extensions are up to date.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to trigger runtime reload");
                }
            });
        }

        // Always stop the typing indicator when the agent finishes
        _typing?.Dispose();
        _typing = null;
    }

    private async Task ProcessDiscordReactionMessageAsync(JsonNode message, PendingRequest pendingRequest)
    {
        try
        {
            var content = message["content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(content))
            {
                _logger.LogDebug("Discord reaction message has no content");
                return;
            }

            var reactionData = JsonNode.Parse(content);
            if (reactionData == null)
            {
                _logger.LogDebug("Failed to parse Discord reaction data");
                return;
            }

            var emoji = reactionData["emoji"]?.GetValue<string>();


            // Add reaction to the original message
            if (pendingRequest.OriginalMessage != null)
            {
                var emote = new Emoji(emoji);
                await pendingRequest.OriginalMessage.AddReactionAsync(emote);
                _logger.LogInformation("Added reaction {Emoji} to message {MessageId}", emoji, pendingRequest.OriginalMessage.Id);
            }
            else
            {
                _logger.LogDebug("No original message available for reaction");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Discord reaction message");
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
        public IDisposable Typing { get; internal set; }
    }
}
