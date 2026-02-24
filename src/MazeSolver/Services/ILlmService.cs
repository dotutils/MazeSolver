namespace MazeSolver.Services;

/// <summary>
/// Abstraction for LLM providers. Uses provider-independent types
/// so the maze solver works with any backend (Anthropic, GitHub Copilot, etc.).
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// Maximum context window tokens for this provider/model.
    /// </summary>
    int MaxContextTokens { get; }

    /// <summary>
    /// Sends a message to the LLM and returns the response.
    /// </summary>
    Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        List<LlmToolDefinition>? tools = null,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests that the connection is working.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
