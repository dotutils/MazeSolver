using System.Text.Json;

namespace MazeSolver.Services;

/// <summary>
/// Provider-independent LLM message types.
/// These decouple the maze solver logic from any specific SDK.
/// </summary>

public enum LlmRole
{
    User,
    Assistant
}

/// <summary>
/// A message in the conversation.
/// </summary>
public class LlmMessage
{
    public LlmRole Role { get; set; }
    public List<LlmContentBlock> Content { get; set; } = new();

    public static LlmMessage User(string text) => new()
    {
        Role = LlmRole.User,
        Content = { new LlmTextBlock { Text = text } }
    };

    public static LlmMessage User(List<LlmContentBlock> content) => new()
    {
        Role = LlmRole.User,
        Content = content
    };

    public static LlmMessage Assistant(List<LlmContentBlock> content) => new()
    {
        Role = LlmRole.Assistant,
        Content = content
    };
}

/// <summary>
/// Base class for content blocks within a message.
/// </summary>
public abstract class LlmContentBlock { }

/// <summary>
/// A text content block.
/// </summary>
public class LlmTextBlock : LlmContentBlock
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// A tool use (function call) from the assistant.
/// </summary>
public class LlmToolUseBlock : LlmContentBlock
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, JsonElement> Input { get; set; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// A tool result from the user, responding to a tool use.
/// </summary>
public class LlmToolResultBlock : LlmContentBlock
{
    public string ToolUseId { get; set; } = string.Empty;
    public string ResultContent { get; set; } = string.Empty;
}

/// <summary>
/// A tool definition that can be passed to the LLM.
/// </summary>
public class LlmToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// JSON Schema object describing the parameters.
    /// </summary>
    public object InputSchema { get; set; } = new { type = "object", properties = new { }, required = Array.Empty<string>() };
}

/// <summary>
/// Response from the LLM including token usage.
/// </summary>
public class LlmResponse
{
    public List<LlmContentBlock> Content { get; set; } = new();
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string StopReason { get; set; } = string.Empty;
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Gets the first text content from the response, or empty string.
    /// </summary>
    public string GetText()
    {
        foreach (var block in Content)
        {
            if (block is LlmTextBlock text)
                return text.Text;
        }
        return string.Empty;
    }

    /// <summary>
    /// Gets all tool use blocks from the response.
    /// </summary>
    public List<LlmToolUseBlock> GetToolCalls()
    {
        return Content.OfType<LlmToolUseBlock>().ToList();
    }
}

/// <summary>
/// Custom exception for context overflow.
/// </summary>
public class ContextOverflowException : Exception
{
    public ContextOverflowException(string message) : base(message) { }
    public ContextOverflowException(string message, Exception inner) : base(message, inner) { }
}
