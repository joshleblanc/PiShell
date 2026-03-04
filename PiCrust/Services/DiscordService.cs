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

        var images = await ExtractImagesAsync(userMessage);

        await _piClient.SendPromptFireAndForgetAsync(userMessage.Content, images);
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

    private async Task HandleTurnEndAsync(JsonNode data)
    {

        var oldestRequest = _pendingRequests.OrderBy(kvp => kvp.Value.StartedAt).FirstOrDefault();
        if (oldestRequest.Value == null) return;

        var message = data["message"];
        if (message == null) return;

        if (message["role"]?.GetValue<string>() != "assistant")
        {
            return;
        }

        var customType = message["customType"]?.GetValue<string>();
        if (customType == "discord_reaction")
        {
            await ProcessDiscordReactionMessageAsync(message, oldestRequest.Value);
            return;
        }

        var content = message["content"]?.AsArray();
        var text = content?.FirstOrDefault(c => c?["type"]?.GetValue<string>() == "text");
        if (text == null)
        {
            return;
        }

        var textContent = text["text"]?.GetValue<string>();
        if(textContent == null)
        {
            return;
        }

        SendMessageToDMChannelAsync((SocketDMChannel)oldestRequest.Value.Channel!, textContent).Wait();
    }

    private async Task HandlePiEventAsync(PiEvent evt)
    {
        switch (evt.Type)
        {
            case "message_update":
                //await HandleMessageUpdateAsync(evt.Data);
                break;
            case "agent_end":
                await HandleAgentEndAsync(evt.Data);
                break;
            case "turn_end":
                await HandleTurnEndAsync(evt.Data);
                break;
            case "tool_execution_start":
            case "tool_execution_end":
                // Tool notifications are logged but not sent to Discord
                _logger.LogDebug("Tool execution: {Tool}", evt.Data["toolName"]);
                break;
        }
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
