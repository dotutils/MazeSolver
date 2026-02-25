using MazeSolver.Models;
using MazeSolver.Services;
using MazeSolver.Services.GitHub;
using Serilog;

namespace MazeSolver;

public class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Configure Serilog
        ConfigureLogging();

        // Global unhandled exception handler - catches exceptions that bypass try/catch
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "UNHANDLED DOMAIN EXCEPTION (IsTerminating={IsTerminating})", e.IsTerminating);
            Console.Error.WriteLine($"UNHANDLED DOMAIN EXCEPTION: {ex}");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Fatal(e.Exception, "UNOBSERVED TASK EXCEPTION");
            Console.Error.WriteLine($"UNOBSERVED TASK EXCEPTION: {e.Exception}");
        };

        Log.Information("=== Maze Solver Starting ===");
        Log.Information("Arguments: {Args}", string.Join(" ", args));

        // Simple manual argument parsing
        bool cli = args.Contains("--cli");
        bool autoSolve = args.Contains("--auto-solve");
        bool testConnection = args.Contains("--test-connection");
        bool listModels = args.Contains("--list-models");
        int width = GetIntArg(args, "--width", 100);
        int height = GetIntArg(args, "--height", 100);
        string provider = GetStringArg(args, "--provider", "anthropic");
        string model = GetStringArg(args, "--model", "");

        try
        {
            if (testConnection)
            {
                TestConnectionAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (listModels)
            {
                ListModelsAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (cli)
            {
                RunCliAsync(width, height, autoSolve, provider, model).GetAwaiter().GetResult();
            }
            else
            {
                RunGui(width, height, autoSolve);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception");
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int GetIntArg(string[] args, string name, int defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out int value))
            {
                return value;
            }
        }
        return defaultValue;
    }

    private static string GetStringArg(string[] args, string name, string defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return defaultValue;
    }

    private static void ConfigureLogging()
    {
        var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "maze-solver-.log");
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static async Task TestConnectionAsync()
    {
        Log.Information("Testing LLM connection...");
        Console.WriteLine("Testing LLM connection...");

        try
        {
            var llmService = new LlmService();
            var success = await llmService.TestConnectionAsync();

            if (success)
            {
                Console.WriteLine("✓ Connection successful!");
                Log.Information("Connection test passed");
            }
            else
            {
                Console.WriteLine("✗ Connection failed!");
                Log.Error("Connection test failed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Connection error: {ex.Message}");
            Log.Error(ex, "Connection test error");
        }
    }

    private static async Task RunCliAsync(int width, int height, bool autoSolve, string provider, string model)
    {
        Log.Information("Running in CLI mode. Width: {Width}, Height: {Height}, AutoSolve: {AutoSolve}, Provider: {Provider}, Model: {Model}",
            width, height, autoSolve, provider, model);

        Console.WriteLine($"Maze Solver CLI Mode");
        Console.WriteLine($"====================");
        Console.WriteLine();

        // Generate maze
        var generator = new MazeGenerator();
        var maze = generator.Generate(width, height);

        Console.WriteLine($"Generated {maze.Width}x{maze.Height} maze");
        Console.WriteLine($"Entry: {maze.Entry}");
        Console.WriteLine($"Exit: {maze.Exit}");
        Console.WriteLine();

        // Show maze if small enough
        if (width <= 50 && height <= 50)
        {
            Console.WriteLine("Maze:");
            Console.WriteLine(maze.Render());
        }

        if (!autoSolve)
        {
            Console.Write("Press Enter to solve or 'q' to quit: ");
            var input = Console.ReadLine();
            if (input?.ToLower() == "q")
            {
                return;
            }
        }

        // Create LLM service based on provider
        ILlmService llmService;
        string providerNorm = provider.ToLowerInvariant();

        if (providerNorm is "copilot" or "github" or "githubcopilot")
        {
            var modelName = !string.IsNullOrEmpty(model) ? model : null;
            llmService = await CreateCopilotServiceAsync(modelName);
            Console.WriteLine($"Provider: GitHub Copilot ({modelName ?? "default"})");
        }
        else
        {
            llmService = new LlmService();
            Console.WriteLine($"Provider: Anthropic (env vars)");
        }

        Console.WriteLine("Starting maze solver...");
        Console.WriteLine();

        var solverService = new MazeSolverService(llmService);

        // Set up event handlers for CLI output
        solverService.OnToolCall += (s, e) =>
        {
            if (e.ToolCallNumber % 10 == 0 || e.ToolCallNumber <= 5)
            {
                Console.WriteLine($"  Tool call #{e.ToolCallNumber}: GetNeighbours({e.Position})");
            }
        };

        solverService.OnTokenUsage += (s, e) =>
        {
            if (e.TotalTokens % 5000 < 100) // Print every ~5000 tokens
            {
                Console.WriteLine($"  Tokens: {e.TotalTokens:N0} / {e.MaxTokens:N0} ({e.UsagePercentage:F1}%)");
            }
        };

        solverService.OnStatusChanged += (s, status) =>
        {
            Log.Information("Status: {Status}", status);
        };

        solverService.OnContextOverflow += (s, ex) =>
        {
            Console.WriteLine();
            Console.WriteLine("!!! CONTEXT OVERFLOW !!!");
            Console.WriteLine($"Error: {ex.Message}");
            Log.Error("Context overflow detected: {Message}", ex.Message);
        };

        var cts = new CancellationTokenSource();
        
        // Allow cancellation with Ctrl+C
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nCancelling...");
        };

        var result = await solverService.SolveAsync(maze, cts.Token);

        Console.WriteLine();
        Console.WriteLine("=== RESULT ===");
        Console.WriteLine($"Success: {result.Success}");
        Console.WriteLine($"Tool calls: {result.ToolCallCount}");
        Console.WriteLine($"Total tokens: {result.TotalTokens:N0}");
        Console.WriteLine($"Context overflow: {result.IsContextOverflow}");
        Console.WriteLine();

        if (result.Success)
        {
            Console.WriteLine("Solution:");
            Console.WriteLine(result.Message);
        }
        else
        {
            Console.WriteLine($"Failed: {result.Message}");
        }

        // Show maze with visited cells
        if (width <= 50 && height <= 50)
        {
            Console.WriteLine();
            Console.WriteLine("Maze with visited cells (· = visited):");
            Console.WriteLine(maze.Render(showVisited: true));
        }

        Log.Information("CLI run completed. Success: {Success}, Tool calls: {ToolCalls}, Tokens: {Tokens}",
            result.Success, result.ToolCallCount, result.TotalTokens);
    }

    /// <summary>
    /// Lists available GitHub Copilot models.
    /// </summary>
    private static async Task ListModelsAsync()
    {
        var (cachedToken, _) = GitHubTokenCache.LoadToken();
        if (string.IsNullOrEmpty(cachedToken))
        {
            Console.WriteLine("No cached GitHub token. Run with --cli first to authenticate.");
            return;
        }

        var tokenProvider = new GitHubCopilotTokenProvider(cachedToken);
        await tokenProvider.GetCopilotTokenAsync();
        var models = await tokenProvider.GetAvailableModelsAsync();

        Console.WriteLine($"Available models ({models.Count}):");
        Console.WriteLine(new string('-', 80));
        foreach (var m in models.OrderBy(x => x.Id))
        {
            var enabled = m.ModelPickerEnabled ? "✓" : " ";
            var policy = m.Policy?.State ?? "";
            Console.WriteLine($"  [{enabled}] {m.Id,-50} {policy}");
        }
    }

    /// <summary>
    /// Creates a GitHub Copilot LLM service using cached token or device flow fallback.
    /// Shared between GUI (via cache saved by GUI login) and CLI.
    /// </summary>
    private static async Task<ILlmService> CreateCopilotServiceAsync(string? modelName)
    {
        var (cachedToken, lastModel) = GitHubTokenCache.LoadToken();
        string? githubAccessToken = cachedToken;
        modelName ??= lastModel ?? "claude-sonnet-4";

        // Try cached token first
        if (!string.IsNullOrEmpty(githubAccessToken))
        {
            try
            {
                Console.WriteLine("Using cached GitHub token...");
                var service = await BuildCopilotService(githubAccessToken, modelName);
                // Update cache with current model
                GitHubTokenCache.SaveToken(githubAccessToken, modelName);
                return service;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Cached token is invalid/expired. Clearing and re-authenticating...");
                Log.Warning("Cached GitHub token is invalid. Clearing cache.");
                GitHubTokenCache.ClearToken();
                githubAccessToken = null;
            }
        }

        // No cached token or it was invalid → device flow
        Console.WriteLine();
        Console.WriteLine("GitHub authentication required.");
        using var authenticator = new GitHubDeviceFlowAuthenticator((userCode, verificationUrl) =>
        {
            Console.WriteLine($"  Please visit: {verificationUrl}");
            Console.WriteLine($"  And enter code: {userCode}");
            Console.WriteLine();
            Console.WriteLine("  Waiting for authorization...");
        });

        githubAccessToken = await authenticator.AuthenticateAsync();
        var newService = await BuildCopilotService(githubAccessToken, modelName);

        // Cache the new token
        GitHubTokenCache.SaveToken(githubAccessToken, modelName);
        Console.WriteLine("GitHub token cached for future runs.");

        return newService;
    }

    private static async Task<ILlmService> BuildCopilotService(string githubAccessToken, string modelName)
    {
        var tokenProvider = new GitHubCopilotTokenProvider(githubAccessToken);
        await tokenProvider.GetCopilotTokenAsync();

        var chatClient = new GitHubCopilotChatClient(tokenProvider, modelName);
        var service = new GitHubCopilotLlmService(chatClient, modelName, tokenProvider);
        
        // Query the actual context window size for this model
        await service.ResolveContextWindowAsync();
        
        return service;
    }

    private static void RunGui(int width, int height, bool autoSolve)
    {
        Log.Information("Running in GUI mode");

        var app = new App();
        app.InitializeComponent();
        
        var mainWindow = new MainWindow(width, height, autoSolve);
        app.Run(mainWindow);
    }
}
