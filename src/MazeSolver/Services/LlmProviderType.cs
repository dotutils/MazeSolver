namespace MazeSolver.Services;

/// <summary>
/// Represents the type of LLM provider being used.
/// </summary>
public enum LlmProviderType
{
    Anthropic,
    GitHubCopilot
}

/// <summary>
/// Event args for provider status changes.
/// </summary>
public class ProviderStatusEventArgs : EventArgs
{
    public LlmProviderType Provider { get; set; }
    public bool IsConnected { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? SelectedModel { get; set; }
}
