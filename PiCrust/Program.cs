using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using PiCrust.Models;
using PiCrust.Services;

namespace PiCrust;

/// <summary>
/// Docker Personal Assistant - Discord Interface for pi
/// 
/// This service runs pi in RPC mode and connects it to Discord.
/// Messages sent to the bot are forwarded to pi, and responses are returned.
/// 
/// A background scheduler runs heartbeat prompts every 30 minutes to keep
/// the assistant active and summarize progress.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         Docker Personal Assistant - Discord Bot           ║");
        Console.WriteLine("║              Connected to pi coding agent                  ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        // Bind environment variables to Configuration class
        var appConfig = new Configuration
        {
            MiniMaxApiKey = configuration["MINIMAX_API_KEY"] ?? string.Empty,
            DiscordToken = configuration["DISCORD_TOKEN"] ?? string.Empty,
            PiCodingAgentDir = configuration["PI_CODING_AGENT_DIR"] ?? string.Empty,
            OwnerId = ulong.TryParse(configuration["OWNER_ID"], out var ownerId) ? ownerId : 0,
            HeartbeatIntervalMinutes = int.TryParse(configuration["HEARTBEAT_INTERVAL_MINUTES"], out var interval) ? interval : 30,
            
            // Provider/Model configuration - supports "provider/model" format via PI_MODEL
            // or separate PI_PROVIDER and PI_MODEL env vars
            PiProvider = configuration["PI_PROVIDER"] ?? string.Empty,
            PiModel = configuration["PI_MODEL"] ?? string.Empty
        };
        
        // Handle "provider/model" format in PI_MODEL (e.g., "anthropic/claude-sonnet-20241022")
        if (!string.IsNullOrEmpty(appConfig.PiModel) && appConfig.PiModel.Contains('/') && string.IsNullOrEmpty(appConfig.PiProvider))
        {
            var parts = appConfig.PiModel.Split('/', 2);
            appConfig.PiProvider = parts[0];
            appConfig.PiModel = parts[1];
        }

        var piDir = appConfig.PiCodingAgentDir;
        var workingDir = !string.IsNullOrEmpty(piDir)
            ? piDir : Path.Combine(AppContext.BaseDirectory, ".pi");   

        Console.WriteLine($"Working directory: {workingDir}");
        Console.WriteLine();

        // Create host
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Register Configuration as singleton with bound env vars
                services.AddSingleton(appConfig);

                // Working directory
                services.AddSingleton(new WorkingDirectoryOptions { Directory = workingDir });
                
                // Provider/Model configuration
                services.AddSingleton(new PiModelOptions 
                { 
                    Provider = appConfig.PiProvider, 
                    Model = appConfig.PiModel 
                });

                services.AddSingleton<PiService>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<PiService>>();
                    var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    var workingDirOptions = sp.GetRequiredService<WorkingDirectoryOptions>();
                    var modelOptions = sp.GetRequiredService<PiModelOptions>();
                    return new PiService(logger, lifetime, workingDirOptions, modelOptions);
                });
                // Pi client (hosted service - starts/stops with the application)
                services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PiService>());

                // Discord socket client (singleton, needs persistent connection)
                services.AddSingleton(sp =>
                {
                    var config = new DiscordSocketConfig
                    {
                        GatewayIntents = GatewayIntents.All,
                        LogGatewayIntentWarnings = false,
                    };
                    return new DiscordSocketClient(config);
                });

                services.AddSingleton<DiscordService>(sp =>
                {
                    var discordSocketClient = sp.GetRequiredService<DiscordSocketClient>();
                    var piClient = sp.GetRequiredService<PiService>();
                    var logger = sp.GetRequiredService<ILogger<DiscordService>>();
                    var config = sp.GetRequiredService<Configuration>();

                    return new DiscordService(discordSocketClient, piClient, logger, config);
                });

                services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscordService>());

                // Background services - receive singletons via constructor injection
                services.AddHostedService<HeartbeatService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();

                // Enable debug logs in dev - override with LOG_LEVEL env var if needed
                var logLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
                logging.SetMinimumLevel(
                    logLevel?.ToLowerInvariant() switch
                    {
                        "debug" => LogLevel.Debug,
                        "trace" => LogLevel.Trace,
                        _ => LogLevel.Information
                    }
                );
            })
            .ConfigureAppConfiguration(config =>
            {
                config.AddEnvironmentVariables();
            })
            .Build();

        // Start the bot
        await host.RunAsync();
    }
}

/// <summary>
/// Options for working directory.
/// </summary>
public class WorkingDirectoryOptions
{
    public string Directory { get; set; } = string.Empty;
}

/// <summary>
/// Options for pi model configuration.
/// </summary>
public class PiModelOptions
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
