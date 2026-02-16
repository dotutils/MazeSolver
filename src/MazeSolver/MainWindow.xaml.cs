using MazeSolver.Models;
using MazeSolver.Services;
using Serilog;
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
    private LlmService? _llmService;
    private MazeSolverService? _solverService;
    private CancellationTokenSource? _cts;
    
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
            // Initialize LLM service
            try
            {
                _llmService = new LlmService();
                _solverService = new MazeSolverService(_llmService);
                SetupSolverEvents();
                
                // Generate initial maze
                GenerateMaze();

                if (autoSolve)
                {
                    await Task.Delay(500); // Brief delay to show the maze
                    await StartSolving();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize LLM service");
                MessageBox.Show($"Failed to initialize LLM service: {ex.Message}\n\nCheck environment variables.",
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        
        SolveButton.IsEnabled = true;
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
