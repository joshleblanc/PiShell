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
                
                // Send initial health + presence events that OpenCLAW clients expect after hello-ok
                try
                {
                    // Send health event
                    var healthPayload = JsonNode.Parse(JsonSerializer.Serialize(new
                    {
                        ok = true,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        durationMs = 0L,
                        channels = new { },
                        channelOrder = Array.Empty<string>(),
                        channelLabels = new { },
                        heartbeatSeconds = 300,
                        defaultAgentId = "default",
                        agents = Array.Empty<object>(),
                        sessions = new { path = "", count = 0, recent = Array.Empty<object>() }
                    }));
                    await SendEventAsync(ws, "health", healthPayload);
                    _logger.LogInformation("Sent initial health event to device {DeviceId}", deviceId);
                    
                    // Send presence event with this device's info
                    var presencePayload = JsonNode.Parse(JsonSerializer.Serialize(new
                    {
                        entries = new[]
                        {
                            new
                            {
                                text = "connected",
                                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                host = Environment.MachineName,
                                version = "1.0.0",
                                platform = "windows",
                                deviceId = deviceId,
                                roles = new[] { "operator" },
                                scopes = new[] { "operator.read", "operator.write" }
                            }
                        }
                    }));
                    await SendEventAsync(ws, "presence", presencePayload);
                    _logger.LogInformation("Sent initial presence event to device {DeviceId}", deviceId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error sending initial events to device {DeviceId}", deviceId);
                }
                
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
            
            // OpenCLAW protocol: messages are wrapped in a "req" frame
            // Expected format: { type: "req", id: "uuid", method: "connect", params: {...} }
            var frameType = json["type"]?.GetValue<string>();
            var requestId = json["id"]?.GetValue<string>();
            var method = json["method"]?.GetValue<string>();
            
            _logger.LogDebug("Frame: type={FrameType}, id={RequestId}, method={Method}", frameType, requestId, method);
            
            // Validate this is a request frame
            if (frameType != "req")
            {
                _logger.LogWarning("Expected 'req' frame type, got: {FrameType}", frameType);
                await SendErrorAsync(ws, requestId, "invalid_frame_type", "First frame must be a 'req' frame");
                return string.Empty;
            }
            
            if (method != "connect")
            {
                _logger.LogWarning("Expected 'connect' method, got: {Method}", method);
                await SendErrorAsync(ws, requestId, "invalid_method", "First request must be 'connect'");
                return string.Empty;
            }
            
            var @params = json["params"];
            if (@params == null)
            {
                _logger.LogWarning("Missing params in connect request");
                await SendErrorAsync(ws, requestId, "invalid_params", "Missing params in connect request");
                return string.Empty;
            }
            
            // Validate protocol version (required by OpenCLAW)
            var minProtocol = @params["minProtocol"]?.GetValue<int>() ?? 0;
            var maxProtocol = @params["maxProtocol"]?.GetValue<int>() ?? 0;
            
            _logger.LogDebug("Client protocol range: min={MinProtocol}, max={MaxProtocol}", minProtocol, maxProtocol);
            
            // We support protocol version 3
            const int serverProtocol = 3;
            if (minProtocol > serverProtocol || maxProtocol < serverProtocol)
            {
                _logger.LogWarning("Protocol version mismatch. Client supports {Min}-{Max}, server supports {Server}", 
                    minProtocol, maxProtocol, serverProtocol);
                await SendErrorAsync(ws, requestId, "protocol_unsupported", 
                    $"Protocol version not supported. We support version {serverProtocol}");
                return string.Empty;
            }
            
            // Parse client info (required)
            var client = @params["client"];
            if (client == null)
            {
                _logger.LogWarning("Missing client info in connect request");
                await SendErrorAsync(ws, requestId, "invalid_params", "Missing client info");
                return string.Empty;
            }
            
            var clientId = client["id"]?.GetValue<string>();
            var clientVersion = client["version"]?.GetValue<string>();
            var clientPlatform = client["platform"]?.GetValue<string>();
            var clientMode = client["mode"]?.GetValue<string>();
            
            _logger.LogInformation("Connect request from client: {ClientId}, version: {Version}, platform: {Platform}, mode: {Mode}", 
                clientId, clientVersion, clientPlatform, clientMode);
            
            // Get auth token
            var auth = @params["auth"];
            var authToken = auth?["token"]?.GetValue<string>();
            var deviceToken = auth?["deviceToken"]?.GetValue<string>();
            
            // Determine device ID
            var role = @params["role"]?.GetValue<string>() ?? "operator";
            var deviceId = @params["device"]?["id"]?.GetValue<string>() ?? clientId ?? Guid.NewGuid().ToString();
            
            // Validate token (either gateway token or device token)
            //if (authToken != _authToken && !string.IsNullOrEmpty(authToken))
            //{
            //    _logger.LogWarning("Invalid token attempt from device {DeviceId}", deviceId);
            //    await SendErrorAsync(ws, requestId, "auth_failed", "Invalid authentication token");
            //    return string.Empty;
            //}
            
            // Check if device is pending approval
            if (!_config.RabbitAutoApprove && !_pendingDevices.ContainsKey(deviceId) && string.IsNullOrEmpty(deviceToken))
            {
                // Add to pending for manual approval
                var pendingDevice = new RabbitDevice
                {
                    DeviceId = deviceId,
                    WebSocket = ws,
                    ConnectedAt = DateTime.UtcNow,
                    Role = role
                };
                _pendingDevices[deviceId] = pendingDevice;
                
                // Send pending response
                await SendResponseAsync(ws, requestId, new
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
            
            // Get scopes from params or use defaults
            var scopes = @params["scopes"]?.AsArray()?.Select(s => s?.GetValue<string>()).Where(s => s != null).ToList() 
                ?? new List<string> { "operator.read", "operator.write" };
            
            // Determine client mode - R1 connects as "node" but with operator role
            var isNode = clientMode == "node";
            
            // Build comprehensive features list matching OpenCLAW
            var allMethods = new List<string>
            {
                // Agent methods
                "agent.start",
                "agent.prompt", 
                "agent.stop",
                "agent.wait",
                "agent.abort",
                // Ping
                "ping",
                // Device methods
                "device.list",
                "device.approve",
                "device.revoke",
                "device.token.rotate",
                "device.token.revoke",
                // Channel methods
                "channel.send",
                "channel.history",
                "channel.subscribe",
                // Chat methods
                "chat.send",
                "chat.history",
                "chat.inject",
                // Presence
                "presence.list",
                // Nodes (for node mode)
                "node.list",
                "node.describe",
                "node.invoke",
                // Tools
                "tools.catalog",
                // Sessions
                "session.list",
                "session.get",
                "session.delete",
                // Config
                "config.get",
                "config.set",
                // Talk / TTS
                "talk.config",
                "talk.mode",
                "tts.status",
                "tts.enable",
                "tts.disable",
            };
            
            var allEvents = new List<string>
            {
                // Agent events
                "agent_start",
                "agent_end",
                "agent_error",
                "agent_message",
                // Tick
                "tick",
                // Channel events
                "channel_message",
                "channel_typing",
                // Presence
                "presence_update",
                // Nodes
                "node_connect",
                "node_disconnect",
                "node_event",
                // System
                "shutdown",
                "restarting",
                // Chat
                "chat",
            };
            
            // Build the hello-ok payload per OpenCLAW protocol
            var helloOkPayload = new
            {
                type = "hello-ok",
                protocol = serverProtocol,
                server = new
                {
                    version = "1.0.0",
                    connId = Guid.NewGuid().ToString()
                },
                features = new
                {
                    methods = allMethods.ToArray(),
                    events = allEvents.ToArray()
                },
                snapshot = new
                {
                    presence = Array.Empty<object>(),
                    health = new
                    {
                        ok = true,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        durationMs = 0L,
                        channels = new { },
                        channelOrder = Array.Empty<string>(),
                        channelLabels = new { },
                        heartbeatSeconds = 300,
                        defaultAgentId = "default",
                        agents = Array.Empty<object>(),
                        sessions = new { path = "", count = 0, recent = Array.Empty<object>() }
                    },
                    stateVersion = new { presence = 1, health = 1 },
                    uptimeMs = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds,
                    authMode = "token"
                },
                policy = new 
                { 
                    tickIntervalMs = 15000,
                    maxPayload = 25 * 1024 * 1024,
                    maxBufferedBytes = 50 * 1024 * 1024
                },
                auth = new
                {
                    deviceToken = GenerateToken(),
                    role = role,
                    scopes = scopes,
                    issuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            };
            
            // Wrap in a proper "res" frame as required by OpenCLAW protocol:
            // { type: "res", id: "<requestId>", ok: true, payload: { ... } }
            var resFrame = new
            {
                type = "res",
                id = requestId,
                ok = true,
                payload = helloOkPayload
            };
            
            await SendResponseAsync(ws, requestId, resFrame);
            
            _logger.LogInformation("Sent hello-ok to device {DeviceId}", deviceId);
            
            return deviceId;
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
        _logger.LogInformation("=== Entering message loop for device {DeviceId}, WebSocket state: {State} ===", 
            device.DeviceId, device.WebSocket?.State);
        
        var buffer = new byte[64 * 1024]; // 64KB buffer for large messages
        
        // Start tick event sender
        var tickCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tickTask = Task.Run(async () =>
        {
            while (!tickCts.Token.IsCancellationRequested && device?.WebSocket?.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Delay(15000, tickCts.Token); // Tick every 15 seconds
                    if (device?.WebSocket?.State == WebSocketState.Open)
                    {
                        var tickPayload = JsonNode.Parse(JsonSerializer.Serialize(new { ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
                        await SendEventAsync(device.WebSocket, "tick", tickPayload);
                        _logger.LogDebug("Sent tick to device {DeviceId}", device.DeviceId);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error sending tick to device");
                    break;
                }
            }
        }, tickCts.Token);
        
        try
        {
            _logger.LogInformation("Message loop started for device {DeviceId}, waiting for messages...", device.DeviceId);
            while (device?.WebSocket?.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Accumulate multi-frame messages until EndOfMessage
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        var segment = new ArraySegment<byte>(buffer);
                        result = await device.WebSocket.ReceiveAsync(segment, stoppingToken);
                        _logger.LogInformation("WebSocket.ReceiveAsync returned: Type={MessageType}, Count={Count}, EndOfMessage={EndOfMessage}", 
                            result.MessageType, result.Count, result.EndOfMessage);
                        
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("Received close request on websocket");
                            if (device.WebSocket.State == WebSocketState.CloseReceived)
                            {
                                await device.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", stoppingToken);
                            }
                            break;
                        }
                        
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(ms.ToArray());
                        _logger.LogInformation("Received message ({Length} bytes): {Message}", message.Length, message);
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
        finally
        {
            tickCts.Cancel();
            try { await tickTask; } catch { }
        }
    }

    private async Task HandleDeviceMessageAsync(RabbitDevice device, string message, CancellationToken stoppingToken)
    {
        try
        {
            var json = JsonNode.Parse(message);
            if (json == null) return;
            
            // Handle both direct method calls and wrapped req frames
            var frameType = json["type"]?.GetValue<string>();
            var method = json["method"]?.GetValue<string>();
            var id = json["id"]?.GetValue<string>();
            
            // If it's a req frame, extract params for method calls
            // If it's a direct call (for backward compatibility), use the json as params
            JsonNode? @params = json["params"];
            
            _logger.LogDebug("Device {DeviceId} sent frameType: {FrameType}, method: {Method}, id: {Id}", 
                device.DeviceId, frameType, method, id);
            
            // Ensure this is a request frame if type is specified
            if (frameType != null && frameType != "req" && frameType != "event")
            {
                _logger.LogWarning("Unknown frame type: {FrameType}", frameType);
                return;
            }
            
            switch (method)
            {
                case "agent":
                case "agent.prompt":
                case "agent.start":
                case "chat.send":
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
                    var approveDeviceId = @params?["deviceId"]?.GetValue<string>();
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
                    
                case "talk.config":
                    var hasTtsKey = !string.IsNullOrEmpty(_config.ElevenLabsApiKey);
                    await SendResponseAsync(device.WebSocket, id, new
                    {
                        type = "res",
                        ok = true,
                        payload = new
                        {
                            config = new
                            {
                                talk = hasTtsKey ? (object)new
                                {
                                    voiceId = string.IsNullOrEmpty(_config.ElevenLabsVoiceId) ? (string?)null : _config.ElevenLabsVoiceId,
                                    modelId = _config.ElevenLabsModelId,
                                    outputFormat = "pcm_24000",
                                    apiKey = _config.ElevenLabsApiKey,
                                    interruptOnSpeech = true
                                } : null,
                                session = new { mainKey = "main" },
                                ui = (object?)null
                            }
                        }
                    });
                    _logger.LogInformation("Sent talk.config to device {DeviceId}, TTS enabled: {Enabled}", device.DeviceId, hasTtsKey);
                    break;
                    
                case "talk.mode":
                    var talkEnabled = json["params"]?["enabled"]?.GetValue<bool>() ?? false;
                    _logger.LogInformation("Device {DeviceId} talk.mode enabled={Enabled}", device.DeviceId, talkEnabled);
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { enabled = talkEnabled } });
                    break;
                    
                case "tts.status":
                    var ttsEnabled = !string.IsNullOrEmpty(_config.ElevenLabsApiKey);
                    await SendResponseAsync(device.WebSocket, id, new
                    {
                        type = "res",
                        ok = true,
                        payload = new
                        {
                            enabled = ttsEnabled,
                            auto = ttsEnabled ? "elevenlabs" : "off",
                            provider = ttsEnabled ? "elevenlabs" : "",
                            fallbackProvider = (string?)null,
                            fallbackProviders = Array.Empty<string>(),
                            prefsPath = "",
                            hasOpenAIKey = false,
                            hasElevenLabsKey = ttsEnabled,
                            edgeEnabled = false
                        }
                    });
                    break;
                    
                case "tts.enable":
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { enabled = !string.IsNullOrEmpty(_config.ElevenLabsApiKey) } });
                    break;
                    
                case "tts.disable":
                    await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = true, payload = new { enabled = false } });
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
        var method = json["method"]?.GetValue<string>() ?? "agent";
        var message = json["params"]?["message"]?.GetValue<string>();
        var sessionKey = json["params"]?["sessionKey"]?.GetValue<string>() ?? "main";
        
        if (string.IsNullOrEmpty(message))
        {
            await SendResponseAsync(device.WebSocket, id, new { type = "res", ok = false, error = new { code = "invalid_request", message = "Message is required" } });
            return;
        }
        
        _logger.LogInformation("Agent prompt from device {DeviceId} (method: {Method}): {Message}", device.DeviceId, method, message);
        
        var runId = Guid.NewGuid().ToString();
        var seq = 0;
        var responseText = new StringBuilder();
        
        // Step 1: Immediately respond with "accepted" status per OpenCLAW protocol
        await SendResponseAsync(device.WebSocket, id, new
        {
            type = "res",
            ok = true,
            payload = new
            {
                status = "accepted",
                runId,
                sessionKey
            }
        });
        _logger.LogInformation("Sent accepted ack for runId {RunId} to device {DeviceId}", runId, device.DeviceId);
        
        // Create a completion source to track when the agent finishes
        var agentDone = new TaskCompletionSource<bool>();
        
        // Step 2: Stream chat events as content arrives, with proper message object format
        Func<PiEvent, Task>? handler = null;
        handler = async evt =>
        {
            if (device.WebSocket?.State != WebSocketState.Open) return;
            
            try
            {
                switch (evt.Type)
                {
                    case "message_update":
                        var assistantEvent = evt.Data["assistantMessageEvent"];
                        if (assistantEvent != null)
                        {
                            var eventType = assistantEvent["type"]?.GetValue<string>();
                            if (eventType == "text_delta")
                            {
                                var delta = assistantEvent["delta"]?.GetValue<string>() ?? "";
                                responseText.Append(delta);
                                
                                seq++;
                                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                
                                // Send agent event (for TTS/streaming playback)
                                await SendEventAsync(device.WebSocket, "agent", JsonNode.Parse(JsonSerializer.Serialize(new
                                {
                                    runId,
                                    seq,
                                    stream = "stdout",
                                    ts,
                                    data = new { type = "text", text = delta }
                                })));
                                
                                // Send chat event (for display)
                                await SendEventAsync(device.WebSocket, "chat", JsonNode.Parse(JsonSerializer.Serialize(new
                                {
                                    runId,
                                    sessionKey,
                                    seq,
                                    state = "delta",
                                    message = responseText.ToString()
                                })));
                            }
                        }
                        break;
                        
                    case "turn_end":
                    case "agent_end":
                        seq++;
                        var finalTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        
                        // Send final agent event
                        await SendEventAsync(device.WebSocket, "agent", JsonNode.Parse(JsonSerializer.Serialize(new
                        {
                            runId,
                            seq,
                            stream = "end",
                            ts = finalTs,
                            data = new { type = "end", text = responseText.ToString() }
                        })));
                        
                        // Send final chat event with state: "final"
                        await SendEventAsync(device.WebSocket, "chat", JsonNode.Parse(JsonSerializer.Serialize(new
                        {
                            runId,
                            sessionKey,
                            seq,
                            state = "final",
                            message = responseText.ToString(),
                            stopReason = "stop"
                        })));
                        _logger.LogInformation("Sent final agent+chat events for runId {RunId}, response: {Response}", runId, responseText.ToString());
                        agentDone.TrySetResult(true);
                        break;
                        
                    case "error":
                        seq++;
                        await SendEventAsync(device.WebSocket, "chat", JsonNode.Parse(JsonSerializer.Serialize(new
                        {
                            runId,
                            sessionKey,
                            seq,
                            state = "error",
                            errorMessage = evt.Data.ToJsonString()
                        })));
                        agentDone.TrySetResult(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error forwarding event to device {DeviceId}", device.DeviceId);
            }
        };
        
        _piService.OnEvent += handler;
        
        try
        {
            await _piService.SendPromptAsync(message);
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            
            try
            {
                await agentDone.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Agent prompt timed out for device {DeviceId}", device.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing prompt for device {DeviceId}", device.DeviceId);
        }
        finally
        {
            _piService.OnEvent -= handler;
        }
    }

    private async Task SendResponseAsync(WebSocket ws, string? id, object response)
    {
        if (ws.State != WebSocketState.Open)
        {
            _logger.LogWarning("SendResponseAsync: WebSocket not open (state={State}), skipping", ws.State);
            return;
        }
        
        // Inject request id into response frame per OpenCLAW protocol
        var jsonNode = JsonSerializer.SerializeToNode(response);
        if (jsonNode is JsonObject obj && id != null && !obj.ContainsKey("id"))
        {
            obj["id"] = id;
        }
        
        var json = jsonNode?.ToJsonString() ?? JsonSerializer.Serialize(response);
        _logger.LogInformation(">>> SEND RES: {Json}", json);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task SendEventAsync(WebSocket ws, string eventType, JsonNode? payload)
    {
        if (ws.State != WebSocketState.Open)
        {
            _logger.LogWarning("SendEventAsync: WebSocket not open (state={State}), skipping", ws.State);
            return;
        }
        
        var evt = new { type = "event", @event = eventType, payload };
        var json = JsonSerializer.Serialize(evt);
        _logger.LogInformation(">>> SEND EVT [{EventType}]: {Json}", eventType, json);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task SendErrorAsync(WebSocket ws, string? requestId, string code, string message)
    {
        var response = new 
        { 
            type = "res", 
            id = requestId,
            ok = false, 
            error = new { code = code, message = message } 
        };
        await SendResponseAsync(ws, requestId, response);
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
        
        // OpenCLAW/Rabbit R1 expects "openclaw-gateway" type
        var payload = new
        {
            type = "openclaw-gateway",
            version = 1,
            ips,
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
        ips.Add("192.168.2.139");
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
