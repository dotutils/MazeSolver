using Anthropic.Models.Messages;
using MazeSolver.Models;
using Serilog;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MazeSolver.Services;

/// <summary>
/// Service that coordinates the LLM solving the maze using tool calls
/// </summary>
public class MazeSolverService
{
    // Configuration flags
    /// <summary>
    /// If true, forces the solver to only discover cells adjacent to already discovered cells.
    /// Non-adjacent cells will result in an error response from the tool call.
    /// </summary>
    public bool ForceAdjacentDiscovery { get; set; } = true;
    
    /// <summary>
    /// If true, includes verbose cell description in tool responses (consumes more tokens).
    /// If false, returns "N/A" for the description field.
    /// </summary>
    public bool UseVerboseDescription { get; set; } = true;
    
    /// <summary>
    /// If true, provides the GetContextUsage tool to the LLM, allowing it to query context usage statistics.
    /// </summary>
    public bool ProvideContextUsageTool { get; set; } = false;
    
    /// <summary>
    /// If true, adds a statement to the system prompt indicating unlimited context through automatic summarization.
    /// </summary>
    public bool UnlimitedContextStatement { get; set; } = false;

    private readonly LlmService _llmService;
    private readonly HashSet<Position> _discoveredCells = new();
    
    public event EventHandler<ToolCallEventArgs>? OnToolCall;
    public event EventHandler<TokenUsageEventArgs>? OnTokenUsage;
    public event EventHandler<string>? OnStatusChanged;
    public event EventHandler<ContextOverflowException>? OnContextOverflow;
    public event EventHandler<string>? OnSolved;
    public event EventHandler<int>? OnContextUsageToolCall;

    public int ToolCallCount { get; private set; }
    public int ContextUsageToolCallCount { get; private set; }
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public long TotalTokens => TotalInputTokens + TotalOutputTokens;
    public bool IsSolving { get; private set; }

    public MazeSolverService(LlmService llmService)
    {
        _llmService = llmService;
    }

    public void Reset()
    {
        ToolCallCount = 0;
        ContextUsageToolCallCount = 0;
        TotalInputTokens = 0;
        TotalOutputTokens = 0;
        IsSolving = false;
        _discoveredCells.Clear();
    }

    /// <summary>
    /// Starts the maze solving process using the LLM
    /// </summary>
    public async Task<SolveResult> SolveAsync(Maze maze, CancellationToken cancellationToken = default)
    {
        Reset();
        maze.ClearLlmVisited();
        IsSolving = true;
        
        var result = new SolveResult();

        try
        {
            OnStatusChanged?.Invoke(this, "Starting maze solver...");
            Log.Information("Starting maze solver. Entry: {Entry}, Exit: {Exit}, Size: {Width}x{Height}",
                maze.Entry, maze.Exit, maze.Width, maze.Height);

            var systemPrompt = CreateSystemPrompt(maze);
            var tools = new List<ToolUnion> { LlmService.CreateGetNeighboursTool() };
            if (ProvideContextUsageTool)
            {
                tools.Add(LlmService.CreateGetContextUsageTool());
            }
            var messages = new List<MessageParam>
            {
                new()
                {
                    Role = Role.User,
                    Content = $"Please solve the maze. Start from the entry at position ({maze.Entry.X}, {maze.Entry.Y}). " +
                             $"Use GetNeighbours to explore cells and find the exit. " +
                             $"When you find the exit, tell me the path you found."
                }
            };

            int maxIterations = 10000; // Safety limit
            int iteration = 0;

            while (iteration < maxIterations && !cancellationToken.IsCancellationRequested)
            {
                iteration++;
                OnStatusChanged?.Invoke(this, $"Solving... (iteration {iteration}, {ToolCallCount} tool calls)");

                var response = await _llmService.SendMessageAsync(
                    systemPrompt, 
                    messages, 
                    tools,
                    maxTokens: 4096,
                    cancellationToken);

                // Token counts from API are per-request (already include full history)
                TotalInputTokens = response.InputTokens;
                TotalOutputTokens = response.OutputTokens;

                OnTokenUsage?.Invoke(this, new TokenUsageEventArgs
                {
                    InputTokens = TotalInputTokens,
                    OutputTokens = TotalOutputTokens,
                    TotalTokens = TotalTokens,
                    MaxTokens = LlmService.MaxContextTokens
                });

                Log.Debug("Iteration {Iteration}: Stop reason = {StopReason}, Total tokens = {Total}",
                    iteration, response.StopReason, TotalTokens);

                // Add assistant response to messages
                var contentBlocks = new List<ContentBlockParam>();
                foreach (var block in response.Message.Content)
                {
                    if (block.TryPickText(out var textBlock))
                    {
                        contentBlocks.Add(new TextBlockParam { Text = textBlock.Text });
                    }
                    else if (block.TryPickToolUse(out var toolUseBlock))
                    {
                        contentBlocks.Add(new ToolUseBlockParam 
                        { 
                            ID = toolUseBlock.ID, 
                            Name = toolUseBlock.Name, 
                            Input = toolUseBlock.Input 
                        });
                    }
                }
                messages.Add(new MessageParam
                {
                    Role = Role.Assistant,
                    Content = contentBlocks
                });

                // Check stop reason
                if (response.StopReason == "end_turn" || response.StopReason == "EndTurn")
                {
                    // LLM finished - check if it found the solution
                    string textContent = "";
                    foreach (var block in response.Message.Content)
                    {
                        if (block.TryPickText(out var textBlock))
                        {
                            textContent = textBlock.Text;
                            break;
                        }
                    }

                    result.Success = true;
                    result.Message = textContent;
                    result.ToolCallCount = ToolCallCount;
                    result.TotalTokens = TotalTokens;

                    Log.Information("Maze solved! Tool calls: {ToolCalls}, Total tokens: {Tokens}",
                        ToolCallCount, TotalTokens);
                    
                    OnSolved?.Invoke(this, textContent);
                    OnStatusChanged?.Invoke(this, "Solved!");
                    break;
                }

                // Process tool calls
                if (response.StopReason == "tool_use" || response.StopReason == "ToolUse")
                {
                    var toolCalls = new List<ToolUseBlock>();
                    foreach (var block in response.Message.Content)
                    {
                        if (block.TryPickToolUse(out var toolUse))
                        {
                            toolCalls.Add(toolUse);
                        }
                    }

                    if (toolCalls.Count == 0)
                    {
                        Log.Warning("Stop reason was tool_use but no tool calls found");
                        break;
                    }

                    var toolResults = new List<ContentBlockParam>();

                    foreach (var toolCall in toolCalls)
                    {
                        ToolCallCount++;
                        
                        Log.Debug("Tool call #{Count}: {Name} with input {Input}",
                            ToolCallCount, toolCall.Name, toolCall.Input);

                        var toolResult = ProcessToolCall(maze, toolCall);
                        toolResults.Add(new ToolResultBlockParam(toolCall.ID)
                        {
                            Content = toolResult
                        });
                    }

                    // Add tool results to messages
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = toolResults.Cast<ContentBlockParam>().ToList()
                    });
                }
                else
                {
                    Log.Warning("Unexpected stop reason: {StopReason}", response.StopReason);
                    break;
                }
            }

            if (iteration >= maxIterations)
            {
                result.Success = false;
                result.Message = "Max iterations reached without finding solution";
                OnStatusChanged?.Invoke(this, "Failed: Max iterations reached");
            }
        }
        catch (ContextOverflowException ex)
        {
            Log.Error(ex, "Context overflow during maze solving");
            result.Success = false;
            result.Message = $"Context overflow: {ex.Message}";
            result.IsContextOverflow = true;
            OnContextOverflow?.Invoke(this, ex);
            OnStatusChanged?.Invoke(this, "Context Overflow!");
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "Operation cancelled";
            OnStatusChanged?.Invoke(this, "Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during maze solving");
            result.Success = false;
            result.Message = $"Error: {ex.Message}";
            OnStatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
        finally
        {
            IsSolving = false;
            result.ToolCallCount = ToolCallCount;
            result.TotalTokens = TotalTokens;
        }

        return result;
    }

    private string CreateSystemPrompt(Maze maze)
    {
        return $@"You are a maze-solving AI. Your task is to find a path from the entry point to the exit.

You need to continue exploring the maze until you find the exit - even if it takes many steps and even if your context fills up.
Do not stop prematurely!

You have one tool available: GetNeighbours(x, y)
This tool returns the status of all 8 cells around the given position.

Status values:
- ""path"": A walkable cell you can move to
- ""wall"": An impassable cell
- ""exit"": The goal - the maze exit! When you see this, you've found the way out!
- ""out_of_bounds"": Outside the maze boundaries

Strategy:
1. Start from the entry position provided
2. Use GetNeighbours to explore reachable cells
3. Keep track of visited cells to avoid revisiting them
4. When you find a neighbour with status ""exit"", you've solved the maze!
5. Report the path you found from entry to exit

Maze dimensions: {maze.Width} x {maze.Height}
Entry position: ({maze.Entry.X}, {maze.Entry.Y})
Exit position: ({maze.Exit.X}, {maze.Exit.Y})

Important: Only move to cells with status ""path"" or ""exit"". Avoid walls and out of bounds.
Call GetNeighbours repeatedly to explore the maze systematically. A good strategy is depth-first search or similar.
When you find the exit, respond with the complete path as a list of coordinates.{(UnlimitedContextStatement ? @"

The conversation has unlimited context through automatic summarization." : "")}{(ForceAdjacentDiscovery ? @"

CRITICAL CONSTRAINT: You can ONLY query cells that are adjacent (including diagonally) to cells you have already discovered.
Your first query must be the entry position. After that, you can only query cells that are neighbors of any previously queried cell.
Querying a non-adjacent cell will result in an error. Plan your exploration path carefully - you cannot jump to distant cells." : "")}

After each tool call, output exactly this message:
""I'm not finished. I need to continue! I need to explore the path further till the exit!""
";
    }

    private string ProcessToolCall(Maze maze, ToolUseBlock toolCall)
    {
        if (toolCall.Name == "GetContextUsage")
        {
            return ProcessGetContextUsage();
        }
        
        if (toolCall.Name != "GetNeighbours")
        {
            return JsonSerializer.Serialize(new { error = $"Unknown tool: {toolCall.Name}" });
        }

        try
        {
            // Parse the input - it comes as IReadOnlyDictionary<string, JsonElement>
            int x, y;
            
            var input = toolCall.Input;
            if (input.TryGetValue("x", out var xElement) && input.TryGetValue("y", out var yElement))
            {
                x = xElement.GetInt32();
                y = yElement.GetInt32();
            }
            else
            {
                throw new InvalidOperationException("Tool input missing x or y property");
            }

            var position = new Position(x, y);

            // Check adjacency constraint
            if (ForceAdjacentDiscovery && !IsValidDiscovery(maze, position))
            {
                Log.Warning("Non-adjacent cell query attempted: ({X}, {Y}). Discovered cells: {Count}", x, y, _discoveredCells.Count);
                
                OnToolCall?.Invoke(this, new ToolCallEventArgs
                {
                    Position = position,
                    ToolCallNumber = ToolCallCount,
                    IsError = true,
                    ErrorMessage = "Non-adjacent cell"
                });

                return JsonSerializer.Serialize(new 
                { 
                    error = $"Invalid query: Position ({x}, {y}) is not adjacent to any previously discovered cell. " +
                           $"You must explore cells step by step, only querying cells that neighbor already discovered positions. " +
                           $"Discovered cells so far: {_discoveredCells.Count}"
                });
            }

            // Add to discovered cells
            _discoveredCells.Add(position);
            
            // Mark this cell as visited by LLM
            maze.MarkLlmVisited(position);
            
            OnToolCall?.Invoke(this, new ToolCallEventArgs
            {
                Position = position,
                ToolCallNumber = ToolCallCount,
                IsError = false
            });

            Log.Debug("GetNeighbours called for position ({X}, {Y})", x, y);

            // Build the response with all 8 neighbours
            var neighbours = new Dictionary<string, object>();
            
            foreach (var (direction, neighborPos) in position.GetAllNeighbours())
            {
                var status = maze.GetCellStatus(neighborPos);
                neighbours[direction] = new
                {
                    x = neighborPos.X,
                    y = neighborPos.Y,
                    status
                };
            }

            // Add verbose description to consume more tokens and simulate heavy tool output
            var description = UseVerboseDescription ? GenerateVerboseCellDescription(x, y) : "N/A";

            var result = new
            {
                position = new { x, y },
                neighbours,
                currentCellDescription = description
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
            Log.Debug("GetNeighbours result: {Result}", json);

            return json;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing GetNeighbours tool call");
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Generates a verbose description to consume ~1-2k tokens per tool call.
    /// This simulates heavy tool output that will accelerate context filling.
    /// </summary>
    private string GenerateVerboseCellDescription(int x, int y)
    {
        // This text is approximately 1500-2000 tokens and will be included in every tool response
        return $@"This cell at coordinates ({x}, {y}) represents a specific location within the maze grid structure. 
The maze exploration system has successfully analyzed this position and gathered comprehensive data about its spatial relationships.

DETAILED SPATIAL ANALYSIS REPORT:
================================
The current cell occupies a unique position within the Cartesian coordinate system of the maze. 
The X-coordinate value of {x} indicates the horizontal displacement from the origin point (0,0) located at the top-left corner.
The Y-coordinate value of {y} indicates the vertical displacement, with increasing values moving downward in the grid.

NAVIGATION CONTEXT:
==================
When navigating through a maze, understanding the relationship between adjacent cells is crucial for pathfinding algorithms.
The eight possible directions of movement (N, NE, E, SE, S, SW, W, NW) provide comprehensive coverage of all potential paths.
Each neighboring cell has been evaluated to determine its accessibility status.

PATHFINDING CONSIDERATIONS:
==========================
Effective maze navigation requires systematic exploration of reachable cells while avoiding walls and boundaries.
The depth-first search (DFS) algorithm is one approach that explores as far as possible along each branch before backtracking.
Breadth-first search (BFS) alternatively explores all neighbors at the current depth before moving to nodes at the next depth level.
A* search combines the benefits of both approaches by using heuristics to guide exploration toward the goal.

CELL CLASSIFICATION INFORMATION:
===============================
Cells in this maze can be classified into several categories based on their properties:
1. PATH cells - These are traversable locations that form the valid routes through the maze.
2. WALL cells - These are impassable obstacles that block movement and define the maze structure.
3. ENTRY cells - The designated starting point for maze navigation, typically located at the maze perimeter.
4. EXIT cells - The goal destination that must be reached to successfully solve the maze.
5. OUT_OF_BOUNDS - Positions outside the valid maze dimensions, representing the maze boundary.

EXPLORATION STRATEGY RECOMMENDATIONS:
====================================
To efficiently solve this maze, consider maintaining a record of visited cells to avoid redundant exploration.
Track the path taken from the entry point to enable backtracking when dead ends are encountered.
Prioritize exploration of cells that appear to lead toward the exit based on their relative position.
The exit is typically located at the opposite corner or edge from the entry point.

CURRENT POSITION METADATA:
=========================
Position hash identifier: CELL_{x}_{y}_EXPLORED
Exploration timestamp: Current iteration
Cell analysis complete: True
Neighboring cells evaluated: 8 directions analyzed
Valid movement options: See neighbours object for details

ADDITIONAL DIAGNOSTIC INFORMATION:
=================================
This verbose output is intentionally detailed to simulate tool responses that consume significant context window space.
In real-world applications, tool outputs may contain extensive metadata, debugging information, or rich descriptions.
The maze solver must account for context window limitations when processing many tool calls in sequence.
Context overflow occurs when the cumulative token count exceeds the model's maximum context window size.
Proper handling of context overflow includes detecting the condition and implementing appropriate recovery strategies.

END OF CELL ANALYSIS REPORT FOR POSITION ({x}, {y})
===================================================";
    }

    /// <summary>
    /// Processes the GetContextUsage tool call
    /// </summary>
    private string ProcessGetContextUsage()
    {
        ContextUsageToolCallCount++;
        OnContextUsageToolCall?.Invoke(this, ContextUsageToolCallCount);
        
        var usagePercentage = LlmService.MaxContextTokens > 0 
            ? (double)TotalTokens / LlmService.MaxContextTokens * 100 
            : 0;

        var result = new
        {
            totalTokensUsed = TotalTokens,
            inputTokens = TotalInputTokens,
            outputTokens = TotalOutputTokens,
            maxContextTokens = LlmService.MaxContextTokens,
            usagePercentage = Math.Round(usagePercentage, 2),
            remainingTokens = LlmService.MaxContextTokens - TotalTokens,
            status = usagePercentage >= 90 ? "critical" : usagePercentage >= 70 ? "warning" : "ok"
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        Log.Debug("GetContextUsage result: {Result}", json);

        return json;
    }

    /// <summary>
    /// Checks if a position is valid for discovery (either first cell at entry, or adjacent to discovered cells)
    /// </summary>
    private bool IsValidDiscovery(Maze maze, Position position)
    {
        // First discovery must be the entry point or a cell adjacent to it
        if (_discoveredCells.Count == 0)
        {
            // Allow entry point or adjacent to entry
            if (position == maze.Entry)
                return true;
            
            // Also allow cells adjacent to entry for flexibility
            return IsAdjacentTo(position, maze.Entry);
        }

        // Check if position is adjacent to any discovered cell
        foreach (var discovered in _discoveredCells)
        {
            if (IsAdjacentTo(position, discovered))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if two positions are adjacent (including diagonally)
    /// </summary>
    private static bool IsAdjacentTo(Position a, Position b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx <= 1 && dy <= 1 && (dx + dy > 0); // Adjacent but not same cell
    }
}

public class SolveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ToolCallCount { get; set; }
    public long TotalTokens { get; set; }
    public bool IsContextOverflow { get; set; }
}

public class ToolCallEventArgs : EventArgs
{
    public Position Position { get; set; }
    public int ToolCallNumber { get; set; }
    public bool IsError { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TokenUsageEventArgs : EventArgs
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long MaxTokens { get; set; }
    public double UsagePercentage => MaxTokens > 0 ? (double)TotalTokens / MaxTokens * 100 : 0;
}
