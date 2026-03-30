using MazeSolver.Models;
using Serilog;

namespace MazeSolver.Services;

/// <summary>
/// Generates random solvable mazes using randomized DFS (recursive backtracker)
/// </summary>
public class MazeGenerator
{
    private readonly Random _random = new();

    /// <summary>
    /// Generates a maze with guaranteed path from entry to exit
    /// </summary>
    public Maze Generate(int width, int height)
    {
        Log.Information("Generating maze {Width}x{Height}", width, height);

        // Ensure odd dimensions for proper maze walls
        if (width % 2 == 0) width++;
        if (height % 2 == 0) height++;

        var maze = new Maze(width, height);

        // Set entry at top-left accessible position
        maze.SetEntry(new Position(1, 0));
        maze[1, 0].Type = CellType.Entry;

        // Set exit at bottom-right accessible position
        maze.SetExit(new Position(width - 2, height - 1));
        maze[width - 2, height - 1].Type = CellType.Exit;

        // Ensure entry and exit connect to the maze
        maze[1, 1].Type = CellType.Path;
        maze[width - 2, height - 2].Type = CellType.Path;

        // Carve paths using DFS from (1, 1)
        var startPos = new Position(1, 1);
        bool isSolvable = CarvePath(maze, startPos);

        // Verify the maze is solvable
        if (!isSolvable)
        {
            Log.Warning("Generated maze was not solvable, regenerating...");
            return Generate(width, height);
        }

        Log.Information("Maze generated successfully. Entry: {Entry}, Exit: {Exit}", maze.Entry, maze.Exit);
        return maze;
    }

    private bool CarvePath(Maze maze, Position pos)
    {
        maze[pos].Type = CellType.Path;

        // Get directions in random order
        var directions = new List<(int dx, int dy)> { (0, -2), (2, 0), (0, 2), (-2, 0) };
        Shuffle(directions);

        foreach (var (dx, dy) in directions)
        {
            var newX = pos.X + dx;
            var newY = pos.Y + dy;
            var newPos = new Position(newX, newY);

            if (maze[newPos].Type == maze.Exit)
            {
                return true;
            }
            
            // Check if the new position is valid and unvisited
            if (maze.IsInBounds(newPos) && maze[newPos].Type == CellType.Wall)
            {
                // Carve through the wall between current and new position
                var wallPos = new Position(pos.X + dx / 2, pos.Y + dy / 2);
                maze[wallPos].Type = CellType.Path;

                // Recursively carve from new position
                CarvePath(maze, newPos);
            }
        }

        return false;
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Verifies that a path exists from entry to exit using BFS
    /// </summary>
    private bool IsSolvable(Maze maze)
    {
        var visited = new HashSet<Position>();
        var queue = new Queue<Position>();
        queue.Enqueue(maze.Entry);
        visited.Add(maze.Entry);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current == maze.Exit)
                return true;

            // Check all 8 directions (but for classical maze, 4 directions should suffice)
            foreach (var (_, neighbor) in current.GetAllNeighbours())
            {
                if (!maze.IsInBounds(neighbor) || visited.Contains(neighbor))
                    continue;

                if (maze[neighbor].IsWalkable)
                {
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
        }

        return false;
    }
}
