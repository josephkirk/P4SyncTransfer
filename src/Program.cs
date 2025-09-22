using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using P4Sync;
using Perforce.P4;
using Cronos;
using System.CommandLine;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

// Check if running with command line arguments
if (args.Length > 0)
{
    // Run CLI interface
    Environment.Exit(RunCli(args));
}
else
{
    // Backward compatibility: run with default config
    RunP4Sync("config.json");
}

// Create host with dependency injection and logging
IHost CreateHost(string configPath)
{
    return Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration((context, config) =>
        {
            config.AddJsonFile(configPath, optional: false, reloadOnChange: true);
        })
        .ConfigureServices((context, services) =>
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(context.Configuration)
                .WriteTo.File("logs/app.log")
                .CreateLogger();

            // Register services
            services.AddSingleton<IConfiguration>(context.Configuration);

            // Register logging
            services.AddLogging(builder =>
            {
                builder.AddConfiguration(context.Configuration.GetSection("Logging"));
                builder.AddConsole();
                builder.AddSerilog();
            });

            // Register P4SyncHistory
            services.AddSingleton<P4SyncHistory>(sp => new P4SyncHistory(Path.Combine(Directory.GetCurrentDirectory(), "logs", "history"), enableFileWriting: true));

            // Register services
            services.AddTransient<P4Operations>();
            services.AddTransient<P4OperationExternal>();
            services.AddTransient<Scheduler>();

            // Register IP4Operations based on configuration
            services.AddTransient<IP4Operations>(sp =>
            {
                var config = sp.GetRequiredService<IConfiguration>();
                var useExternal = config.GetValue<bool>("UseExternalP4");
                var logger = sp.GetRequiredService<ILogger<IP4Operations>>();

                if (useExternal)
                {
                    return sp.GetRequiredService<P4OperationExternal>();
                }
                else
                {
                    return sp.GetRequiredService<P4Operations>();
                }
            });
        })
        .Build();
}

// Main function to run P4Sync with the configuration file
void RunP4Sync(string configPath)
{
    try
    {
        // Create host with logging and DI
        var host = CreateHost(configPath);
        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Using configuration file: {ConfigPath}", configPath);

        if (!System.IO.File.Exists(configPath))
        {
            logger.LogError("Configuration file '{ConfigPath}' not found", configPath);
            logger.LogInformation("Use 'init' command to create a configuration template");
            return;
        }

        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var appConfig = configuration.Get<AppConfig>();

        // Process sync profiles if available
        if (appConfig?.SyncProfiles != null && appConfig.SyncProfiles.Count > 0)
        {
            logger.LogInformation("Found {ProfileCount} sync profile(s)", appConfig.SyncProfiles.Count);

            foreach (var profile in appConfig.SyncProfiles)
            {
                if (string.IsNullOrEmpty(profile.Name))
                {
                    logger.LogError("Sync profile must have a name");
                    continue;
                }

                if (profile.Source == null || profile.Target == null)
                {
                    logger.LogError("Sync profile '{ProfileName}' must have Source and Target configurations", profile.Name);
                    continue;
                }

                logger.LogInformation("Processing sync profile: {ProfileName}", profile.Name);

                // If schedule is specified, set up a scheduler
                if (!string.IsNullOrEmpty(profile.Schedule))
                {
                    logger.LogInformation("Setting up scheduler with cron expression: {CronExpression}", profile.Schedule);
                    var scheduler = new Scheduler(profile.Schedule, () => ExecuteSync(host.Services, profile), host.Services.GetRequiredService<ILogger<Scheduler>>());
                    scheduler.Start();

                    // Keep the application running
                    logger.LogInformation("Scheduler is running. Press Ctrl+C to exit");
                    Thread.Sleep(Timeout.Infinite);
                }
                else
                {
                    // Execute sync immediately if no schedule
                    logger.LogDebug("Profile PathMappings: {Mappings}", profile.PathMappings != null ? string.Join(",", profile.PathMappings?.Select(kv => $"{kv.Key}=>{kv.Value}") ?? new[] { "null" }) : "null");
                    ExecuteSync(host.Services, profile);
                }
            }
        }
        else
        {
            logger.LogWarning("No sync profiles found in configuration");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}"); // Keep console for critical errors during startup
    }
}

// Function to create a configuration template
void CreateConfigTemplate(string outputPath)
{
    var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<Program>();

    try
    {
        // Create directory if it doesn't exist
        var directory = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        // Create a sample configuration
        var sampleConfig = new AppConfig
        {
            SyncProfiles = new List<SyncProfile>
            {
                new SyncProfile
                {
                    Name = "Sample Profile",
                    Source = new P4Connection
                    {
                        Port = "sourceServer:1666",
                        User = "username",
                        Workspace = "workspace_name"
                    },
                    Target = new P4Connection
                    {
                        Port = "targetServer:1666",
                        User = "username",
                        Workspace = "workspace_name"
                    },
                    Schedule = "0 * * * *", // Every hour
                    SyncFilter = new List<string> { "//depot/path/..." }
                }
            }
        };

        // Serialize to JSON
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        string json = JsonSerializer.Serialize(sampleConfig, options);

        // Write to file
        System.IO.File.WriteAllText(outputPath, json);

        logger.LogInformation("Configuration template created at: {OutputPath}", outputPath);
        logger.LogInformation("Edit this file with your actual Perforce server details and sync profiles");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error creating configuration template");
    }
}

void ExecuteSync(IServiceProvider services, SyncProfile profile)
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    logger.LogDebug("Starting ExecuteSync for profile: {ProfileName}", profile.Name);
    logger.LogDebug("Source: {SourcePort}, Target: {TargetPort}",
        profile.Source?.Port, profile.Target?.Port);

    var p4Ops = services.GetRequiredService<IP4Operations>();
    logger.LogDebug("IP4Operations instance created");

    try
    {
        p4Ops.ExecuteSync(profile);
        logger.LogDebug("ExecuteSync completed successfully");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "ExecuteSync failed with exception");
        throw;
    }
}

// Simple CLI implementation
int RunCli(string[] args)
{
    if (args.Length == 0)
    {
        ShowHelp();
        return 0;
    }

    var command = args[0].ToLower();

    switch (command)
    {
        case "sync":
            return HandleSyncCommand(args);
        case "init":
            return HandleInitCommand(args);
        case "list-profiles":
            return HandleListProfilesCommand(args);
        case "validate-config":
            return HandleValidateConfigCommand(args);
        case "--help":
        case "-h":
        case "help":
            ShowHelp();
            return 0;
        default:
            Console.WriteLine($"Unknown command: {command}");
            ShowHelp();
            return 1;
    }
}

int HandleSyncCommand(string[] args)
{
    var configFile = "config.json";

    // Parse --config option
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
        {
            configFile = args[i + 1];
            i++; // Skip the next argument
        }
    }

    try
    {
        RunP4Sync(configFile);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during sync: {ex.Message}"); // Keep console for CLI errors
        return 1;
    }
}

int HandleInitCommand(string[] args)
{
    var outputPath = "config.json";

    // Parse --output option
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--output" && i + 1 < args.Length)
        {
            outputPath = args[i + 1];
            i++; // Skip the next argument
        }
    }

    CreateConfigTemplate(outputPath);
    return 0;
}

int HandleListProfilesCommand(string[] args)
{
    var configFile = "config.json";

    // Parse --config option
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
        {
            configFile = args[i + 1];
            i++; // Skip the next argument
        }
    }

    try
    {
        if (!System.IO.File.Exists(configFile))
        {
            Console.WriteLine($"Error: Configuration file '{configFile}' not found.");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configFile, optional: false, reloadOnChange: true)
            .Build();

        var appConfig = configuration.Get<AppConfig>();

        if (appConfig?.SyncProfiles != null && appConfig.SyncProfiles.Count > 0)
        {
            Console.WriteLine($"Found {appConfig.SyncProfiles.Count} sync profile(s):");
            foreach (var profile in appConfig.SyncProfiles)
            {
                Console.WriteLine($"- {profile.Name}");
                Console.WriteLine($"  Source: {profile.Source?.Port} ({profile.Source?.Workspace})");
                Console.WriteLine($"  Target: {profile.Target?.Port} ({profile.Target?.Workspace})");
                if (!string.IsNullOrEmpty(profile.Schedule))
                {
                    Console.WriteLine($"  Schedule: {profile.Schedule}");
                }
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("No sync profiles found in configuration.");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error listing profiles: {ex.Message}");
        return 1;
    }
}

int HandleValidateConfigCommand(string[] args)
{
    var configFile = "config.json";

    // Parse --config option
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
        {
            configFile = args[i + 1];
            i++; // Skip the next argument
        }
    }

    try
    {
        if (!System.IO.File.Exists(configFile))
        {
            Console.WriteLine($"Error: Configuration file '{configFile}' not found.");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configFile, optional: false, reloadOnChange: true)
            .Build();

        var appConfig = configuration.Get<AppConfig>();

        // Basic validation
        bool isValid = true;
        if (appConfig?.SyncProfiles != null)
        {
            foreach (var profile in appConfig.SyncProfiles)
            {
                if (string.IsNullOrEmpty(profile.Name))
                {
                    Console.WriteLine("Error: Sync profile must have a name.");
                    isValid = false;
                }
                if (profile.Source == null || string.IsNullOrEmpty(profile.Source.Port))
                {
                    Console.WriteLine($"Error: Sync profile '{profile.Name}' must have a valid source configuration.");
                    isValid = false;
                }
                if (profile.Target == null || string.IsNullOrEmpty(profile.Target.Port))
                {
                    Console.WriteLine($"Error: Sync profile '{profile.Name}' must have a valid target configuration.");
                    isValid = false;
                }
            }
        }

        if (isValid)
        {
            Console.WriteLine("Configuration file is valid.");
            return 0;
        }
        else
        {
            Console.WriteLine("Configuration file has validation errors.");
            return 1;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error validating configuration: {ex.Message}");
        return 1;
    }
}

void ShowHelp()
{
    Console.WriteLine("P4Sync - Perforce repository synchronization tool");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  sync [--config <file>]              Execute synchronization based on configuration");
    Console.WriteLine("  init [--output <file>]              Create a configuration template");
    Console.WriteLine("  list-profiles [--config <file>]     Display available sync profiles");
    Console.WriteLine("  validate-config [--config <file>]   Validate configuration file");
    Console.WriteLine("  help                                Show this help message");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --config <file>                     Path to configuration file (default: config.json)");
    Console.WriteLine("  --output <file>                     Output path for generated files (default: config.json)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run init --output myconfig.json");
    Console.WriteLine("  dotnet run list-profiles --config myconfig.json");
    Console.WriteLine("  dotnet run sync --config myconfig.json");
}
