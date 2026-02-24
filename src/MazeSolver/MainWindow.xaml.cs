using MazeSolver.Models;
using MazeSolver.Services;
using MazeSolver.Services.GitHub;
using Serilog;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MazeSolver;

public partial class MainWindow : Window
{
    private Maze? _maze;
    private readonly MazeGenerator _generator;
    private ILlmService? _llmService;
    private MazeSolverService? _solverService;
    private CancellationTokenSource? _cts;
    
    // GitHub Copilot state
    private GitHubCopilotTokenProvider? _copilotTokenProvider;
    private string? _githubAccessToken;
    
    private const int CellSize = 8; // pixels per cell
    private readonly Dictionary<Position, Rectangle> _cellRectangles = new();

    // Colors
    private static readonly SolidColorBrush WallBrush = new(Colors.Black);
    private static readonly SolidColorBrush PathBrush = new(Colors.White);
    private static readonly SolidColorBrush EntryBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush ExitBrush = new(Colors.Red);
    private static readonly SolidColorBrush VisitedBrush = new(Color.FromRgb(144, 238, 144)); // Light green
    private static readonly SolidColorBrush ErrorVisitBrush = new(Colors.OrangeRed); // Red for invalid queries
    private static readonly SolidColorBrush GridBrush = new(Color.FromRgb(200, 200, 200));

    public MainWindow(int initialWidth = 100, int initialHeight = 100, bool autoSolve = false)
    {
        InitializeComponent();
        
        _generator = new MazeGenerator();
        
        WidthTextBox.Text = initialWidth.ToString();
        HeightTextBox.Text = initialHeight.ToString();

        Loaded += async (s, e) =>
        {
            // Auto-connect priority: 1) Anthropic env vars, 2) Cached GitHub Copilot token
            try
            {
                var endpoint = Environment.GetEnvironmentVariable("LLM_ENDPOINT");
                var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
                var model = Environment.GetEnvironmentVariable("LLM_MODEL");

                if (!string.IsNullOrEmpty(endpoint) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(model))
                {
                    _llmService = new LlmService();
                    _solverService = new MazeSolverService(_llmService);
                    SetupSolverEvents();
                    UpdateConnectionStatus(true, "Anthropic", model);
                }
                else
                {
                    // Try cached GitHub Copilot token
                    var (cachedToken, lastModel) = GitHubTokenCache.LoadToken();
                    if (!string.IsNullOrEmpty(cachedToken))
                    {
                        try
                        {
                            // Switch UI to Copilot provider
                            ProviderComboBox.SelectedIndex = 1; // GitHub Copilot
                            var modelToUse = lastModel ?? "claude-sonnet-4";
                            ModelComboBox.Text = modelToUse;

                            StatusText.Text = "Connecting with cached GitHub token...";
                            _githubAccessToken = cachedToken;
                            await ConnectToGitHubCopilotAsync(modelToUse);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Log.Warning("Cached GitHub token is invalid/revoked. Clearing cache.");
                            GitHubTokenCache.ClearToken();
                            _githubAccessToken = null;
                            UpdateConnectionStatus(false, null, null);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Failed to auto-connect with cached Copilot token");
                            UpdateConnectionStatus(false, null, null);
                        }
                    }
                    else
                    {
                        UpdateConnectionStatus(false, null, null);
                        Log.Information("No LLM credentials found. Use the UI to connect to a provider.");
                    }
                }
                
                // Generate initial maze regardless of LLM connection
                GenerateMaze();

                if (autoSolve && _solverService != null)
                {
                    await Task.Delay(500);
                    await StartSolving();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to auto-initialize LLM service");
                UpdateConnectionStatus(false, null, null);
                GenerateMaze();
            }
        };
    }

    private void SetupSolverEvents()
    {
        if (_solverService == null) return;

        _solverService.OnToolCall += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                ToolCallCountText.Text = e.ToolCallNumber.ToString("N0");
                
                // Update the visited cell color
                if (_cellRectangles.TryGetValue(e.Position, out var rect))
                {
                    var cell = _maze?[e.Position];
                    if (cell != null && cell.Type == CellType.Path)
                    {
                        // Use red/orange for error attempts, green for valid
                        rect.Fill = e.IsError ? ErrorVisitBrush : VisitedBrush;
                    }
                    else if (e.IsError)
                    {
                        // For walls/other cells that had error, still show the error attempt visually
                        // by adding a red border or different indicator
                        rect.Stroke = ErrorVisitBrush;
                        rect.StrokeThickness = 2;
                    }
                }
                
                // Log error attempts
                if (e.IsError)
                {
                    Log.Warning("GUI: Tool call error at {Position}: {Message}", e.Position, e.ErrorMessage);
                }
            });
        };

        _solverService.OnTokenUsage += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                TokenUsageText.Text = $"{e.TotalTokens:N0} / {e.MaxTokens:N0} tokens";
                TokenPercentageText.Text = $"{e.UsagePercentage:F1}%";
                InputTokensText.Text = e.InputTokens.ToString("N0");
                OutputTokensText.Text = e.OutputTokens.ToString("N0");
                
                ContextProgressBar.Value = e.UsagePercentage;
                ProgressText.Text = $"{e.UsagePercentage:F1}%";

                // Change progress bar color based on usage
                if (e.UsagePercentage >= 90)
                {
                    ContextProgressBar.Foreground = new SolidColorBrush(Colors.Red);
                }
                else if (e.UsagePercentage >= 70)
                {
                    ContextProgressBar.Foreground = new SolidColorBrush(Colors.Orange);
                }
                else
                {
                    ContextProgressBar.Foreground = new SolidColorBrush(Colors.Green);
                }
            });
        };

        _solverService.OnContextUsageToolCall += (s, count) =>
        {
            Dispatcher.Invoke(() =>
            {
                ContextUsageCallCountText.Text = count.ToString("N0");
            });
        };

        _solverService.OnStatusChanged += (s, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        };

        _solverService.OnContextOverflow += (s, ex) =>
        {
            Dispatcher.Invoke(() =>
            {
                OverflowIndicator.Visibility = Visibility.Visible;
                OverflowMessageText.Text = ex.Message;
                StatusText.Text = "CONTEXT OVERFLOW!";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
            });
        };

        _solverService.OnSolved += (s, message) =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Solved!";
                StatusText.Foreground = new SolidColorBrush(Colors.Green);
                
                // Show result in message box
                MessageBox.Show($"Maze solved!\n\nTool calls: {_solverService.ToolCallCount}\nTotal tokens: {_solverService.TotalTokens:N0}\n\nSolution:\n{message.Substring(0, Math.Min(500, message.Length))}...",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        };
    }

    private void UpdateConnectionStatus(bool isConnected, string? provider, string? model)
    {
        if (isConnected)
        {
            ConnectionStatusText.Text = $"🟢 {provider} ({model})";
            ConnectionStatusText.Foreground = new SolidColorBrush(Colors.Green);
            SolveButton.IsEnabled = _maze != null;
        }
        else
        {
            ConnectionStatusText.Text = "⚪ Not connected";
            ConnectionStatusText.Foreground = new SolidColorBrush(Colors.Gray);
            SolveButton.IsEnabled = false;
        }
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: event fires during XAML init before other controls exist
        if (ModelComboBox == null || RefreshModelsButton == null) return;

        if (ProviderComboBox.SelectedItem is ComboBoxItem selected)
        {
            var tag = selected.Tag?.ToString();
            if (tag == "GitHubCopilot")
            {
                // Pre-populate with some well-known Copilot models
                ModelComboBox.Items.Clear();
                ModelComboBox.Items.Add("claude-sonnet-4");
                ModelComboBox.Items.Add("gpt-4o");
                ModelComboBox.Items.Add("gpt-4.1");
                ModelComboBox.Items.Add("o4-mini");
                ModelComboBox.Items.Add("o3");
                ModelComboBox.Items.Add("gemini-2.5-pro");
                ModelComboBox.Text = "claude-sonnet-4";
                RefreshModelsButton.IsEnabled = true;
            }
            else
            {
                // Anthropic mode - use env var model
                ModelComboBox.Items.Clear();
                var envModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "";
                if (!string.IsNullOrEmpty(envModel))
                {
                    ModelComboBox.Items.Add(envModel);
                    ModelComboBox.Text = envModel;
                }
                RefreshModelsButton.IsEnabled = false;
            }
        }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_copilotTokenProvider == null)
        {
            MessageBox.Show("Please connect to GitHub Copilot first.", "Not Connected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            RefreshModelsButton.IsEnabled = false;
            StatusText.Text = "Fetching available models...";

            var models = await _copilotTokenProvider.GetAvailableModelsAsync();

            ModelComboBox.Items.Clear();
            var currentText = ModelComboBox.Text;
            
            foreach (var model in models.Where(m => m.ModelPickerEnabled || !string.IsNullOrEmpty(m.Id)))
            {
                ModelComboBox.Items.Add(model.Id);
            }

            // Restore selection or pick first
            if (ModelComboBox.Items.Contains(currentText))
            {
                ModelComboBox.Text = currentText;
            }
            else if (ModelComboBox.Items.Count > 0)
            {
                ModelComboBox.SelectedIndex = 0;
            }

            StatusText.Text = $"Loaded {models.Count} models";
            Log.Information("Loaded {Count} models from GitHub Copilot", models.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch models");
            StatusText.Text = $"Failed to fetch models: {ex.Message}";
            MessageBox.Show($"Failed to fetch models: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedProvider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var selectedModel = ModelComboBox.Text?.Trim();

        if (string.IsNullOrEmpty(selectedModel))
        {
            MessageBox.Show("Please select or type a model name.", "No Model",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Connecting...";

        try
        {
            if (selectedProvider == "GitHubCopilot")
            {
                await ConnectToGitHubCopilotAsync(selectedModel);
            }
            else
            {
                ConnectToAnthropic(selectedModel);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to connect to {Provider}", selectedProvider);
            UpdateConnectionStatus(false, null, null);
            StatusText.Text = $"Connection failed: {ex.Message}";
            MessageBox.Show($"Connection failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task ConnectToGitHubCopilotAsync(string modelName)
    {
        // 1. Try in-memory token, then cached token, then device flow
        if (string.IsNullOrEmpty(_githubAccessToken))
        {
            var (cachedToken, _) = GitHubTokenCache.LoadToken();
            _githubAccessToken = cachedToken;
        }

        if (string.IsNullOrEmpty(_githubAccessToken))
        {
            _githubAccessToken = await DoDeviceFlowAsync();
        }

        // 2. Try connecting with the token we have
        try
        {
            await SetupCopilotConnection(modelName);
            GitHubTokenCache.SaveToken(_githubAccessToken!, modelName);
        }
        catch (UnauthorizedAccessException)
        {
            // Token is invalid/revoked — clear cache and re-authenticate
            Log.Warning("GitHub access token is invalid. Re-authenticating...");
            GitHubTokenCache.ClearToken();
            _githubAccessToken = null;

            StatusText.Text = "Token expired, re-authenticating...";
            _githubAccessToken = await DoDeviceFlowAsync();
            await SetupCopilotConnection(modelName);
            GitHubTokenCache.SaveToken(_githubAccessToken!, modelName);
        }
    }

    /// <summary>
    /// Runs the GitHub OAuth Device Code Flow, showing a dialog with the user code.
    /// </summary>
    private async Task<string> DoDeviceFlowAsync()
    {
        StatusText.Text = "Starting GitHub authentication...";

        using var authenticator = new GitHubDeviceFlowAuthenticator((userCode, verificationUrl) =>
        {
            Dispatcher.Invoke(() =>
            {
                Clipboard.SetText(userCode);

                var result = MessageBox.Show(
                    $"GitHub authentication required.\n\n" +
                    $"Your code: {userCode}\n" +
                    $"(Already copied to clipboard)\n\n" +
                    $"Click OK to open {verificationUrl} in your browser,\n" +
                    $"then paste the code and authorize.",
                    "GitHub Login",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.OK)
                {
                    Process.Start(new ProcessStartInfo(verificationUrl) { UseShellExecute = true });
                }
            });
        });

        StatusText.Text = "Waiting for GitHub authorization...";
        return await authenticator.AuthenticateAsync();
    }

    /// <summary>
    /// Creates the Copilot token provider, chat client, LLM service, and tests the connection.
    /// Throws UnauthorizedAccessException if the GitHub token is invalid.
    /// </summary>
    private async Task SetupCopilotConnection(string modelName)
    {
        StatusText.Text = "Exchanging for Copilot token...";
        _copilotTokenProvider = new GitHubCopilotTokenProvider(_githubAccessToken!);
        await _copilotTokenProvider.GetCopilotTokenAsync();

        var chatClient = new GitHubCopilotChatClient(_copilotTokenProvider, modelName);
        _llmService = new GitHubCopilotLlmService(chatClient, modelName);

        StatusText.Text = "Testing connection...";
        var success = await _llmService.TestConnectionAsync();

        if (success)
        {
            _solverService = new MazeSolverService(_llmService);
            SetupSolverEvents();
            UpdateConnectionStatus(true, "GitHub Copilot", modelName);
            StatusText.Text = $"Connected to GitHub Copilot ({modelName})";
        }
        else
        {
            throw new InvalidOperationException("Connection test failed");
        }
    }

    private void ConnectToAnthropic(string modelName)
    {
        StatusText.Text = "Connecting to Anthropic...";
        _llmService = new LlmService();
        _solverService = new MazeSolverService(_llmService);
        SetupSolverEvents();
        UpdateConnectionStatus(true, "Anthropic", modelName);
        StatusText.Text = $"Connected to Anthropic ({modelName})";
    }

    private void GenerateMaze()
    {
        if (!int.TryParse(WidthTextBox.Text, out int width) || width < 5 || width > 500)
        {
            MessageBox.Show("Width must be between 5 and 500", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(HeightTextBox.Text, out int height) || height < 5 || height > 500)
        {
            MessageBox.Show("Height must be between 5 and 500", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Log.Information("Generating maze {Width}x{Height}", width, height);

        _maze = _generator.Generate(width, height);
        RenderMaze();
        
        SolveButton.IsEnabled = _solverService != null;
        ClearVisitedButton.IsEnabled = true;
        OverflowIndicator.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Generated {_maze.Width}x{_maze.Height} maze";
        StatusText.Foreground = new SolidColorBrush(Colors.Black);
        
        // Reset counters
        ToolCallCountText.Text = "0";
        ContextUsageCallCountText.Text = "0";
        TokenUsageText.Text = "0 / 200,000 tokens";
        TokenPercentageText.Text = "0.0%";
        InputTokensText.Text = "0";
        OutputTokensText.Text = "0";
        ContextProgressBar.Value = 0;
        ProgressText.Text = "0%";
        ContextProgressBar.Foreground = new SolidColorBrush(Colors.Green);
    }

    private void RenderMaze()
    {
        if (_maze == null) return;

        MazeCanvas.Children.Clear();
        _cellRectangles.Clear();

        // Set canvas size
        MazeCanvas.Width = _maze.Width * CellSize;
        MazeCanvas.Height = _maze.Height * CellSize;

        for (int y = 0; y < _maze.Height; y++)
        {
            for (int x = 0; x < _maze.Width; x++)
            {
                var cell = _maze[x, y];
                var rect = new Rectangle
                {
                    Width = CellSize,
                    Height = CellSize,
                    Fill = GetCellBrush(cell),
                    Stroke = GridBrush,
                    StrokeThickness = 0.5
                };

                Canvas.SetLeft(rect, x * CellSize);
                Canvas.SetTop(rect, y * CellSize);
                
                MazeCanvas.Children.Add(rect);
                _cellRectangles[cell.Position] = rect;
            }
        }
    }

    private static SolidColorBrush GetCellBrush(Cell cell)
    {
        if (cell.IsVisitedByLlm && cell.Type == CellType.Path)
            return VisitedBrush;

        return cell.Type switch
        {
            CellType.Wall => WallBrush,
            CellType.Path => PathBrush,
            CellType.Entry => EntryBrush,
            CellType.Exit => ExitBrush,
            _ => PathBrush
        };
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateMaze();
    }

    private async void SolveButton_Click(object sender, RoutedEventArgs e)
    {
        await StartSolving();
    }

    private async Task StartSolving()
    {
        if (_maze == null || _solverService == null)
        {
            MessageBox.Show("Please generate a maze first", "No Maze", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Clear previous visited cells
        _maze.ClearLlmVisited();
        RenderMaze();
        
        // Reset UI
        OverflowIndicator.Visibility = Visibility.Collapsed;
        StatusText.Foreground = new SolidColorBrush(Colors.Black);

        // Update button states
        GenerateButton.IsEnabled = false;
        SolveButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        _cts = new CancellationTokenSource();

        try
        {
            var result = await _solverService.SolveAsync(_maze, _cts.Token);

            if (!result.Success && !result.IsContextOverflow)
            {
                StatusText.Text = $"Failed: {result.Message}";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during solving");
            StatusText.Text = $"Error: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Colors.Red);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            SolveButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling...";
    }

    private void ClearVisitedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_maze == null) return;

        _maze.ClearLlmVisited();
        RenderMaze();
        
        ToolCallCountText.Text = "0";
        StatusText.Text = "Cleared visited cells";
    }

    private void MazeCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_maze == null || _solverService?.IsSolving == true) return;

        var position = e.GetPosition(MazeCanvas);
        int x = (int)(position.X / CellSize);
        int y = (int)(position.Y / CellSize);

        if (!_maze.IsInBounds(x, y)) return;

        var pos = new Position(x, y);
        _maze.ToggleCell(pos);

        // Update the visual
        if (_cellRectangles.TryGetValue(pos, out var rect))
        {
            rect.Fill = GetCellBrush(_maze[pos]);
        }

        Log.Debug("Toggled cell at ({X}, {Y}) to {Type}", x, y, _maze[x, y].Type);
    }

    private void ForceAdjacentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_solverService != null)
        {
            _solverService.ForceAdjacentDiscovery = ForceAdjacentCheckBox.IsChecked ?? true;
            Log.Information("ForceAdjacentDiscovery set to {Value}", _solverService.ForceAdjacentDiscovery);
        }
    }

    private void VerboseDescriptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_solverService != null)
        {
            _solverService.UseVerboseDescription = VerboseDescriptionCheckBox.IsChecked ?? true;
            Log.Information("UseVerboseDescription set to {Value}", _solverService.UseVerboseDescription);
        }
    }

    private void ContextUsageToolCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_solverService != null)
        {
            _solverService.ProvideContextUsageTool = ContextUsageToolCheckBox.IsChecked ?? false;
            Log.Information("ProvideContextUsageTool set to {Value}", _solverService.ProvideContextUsageTool);
        }
    }

    private void UnlimitedContextCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_solverService != null)
        {
            _solverService.UnlimitedContextStatement = UnlimitedContextCheckBox.IsChecked ?? false;
            Log.Information("UnlimitedContextStatement set to {Value}", _solverService.UnlimitedContextStatement);
        }
    }
}
