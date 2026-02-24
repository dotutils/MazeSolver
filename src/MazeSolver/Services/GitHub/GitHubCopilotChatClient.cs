using Serilog;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MazeSolver.Services.GitHub;

/// <summary>
/// GitHub Copilot chat client that provides a simple send-message interface
/// compatible with the MazeSolver's LLM abstraction.
/// </summary>
public class GitHubCopilotChatClient : IDisposable
{
    private readonly GitHubCopilotTokenProvider _tokenProvider;
    private readonly string _modelName;
    private readonly HttpClient _httpClient;

    public string ModelName => _modelName;

    public GitHubCopilotChatClient(
        GitHubCopilotTokenProvider tokenProvider,
        string modelName)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Sends a chat completion request to the Copilot API and returns the response.
    /// </summary>
    public async Task<CopilotChatCompletionResponse> SendAsync(
        CopilotChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var copilotToken = await _tokenProvider.RefreshIfNeededAsync(cancellationToken);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            $"{copilotToken.BaseUrl}/chat/completions");

        // Use TryAddWithoutValidation because Copilot token contains special characters (semicolons)
        httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {copilotToken.Token}");
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json");

        // Required Copilot API headers
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", "MazeSolver/1.0");
        httpRequest.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.107.0");
        httpRequest.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.35.0");
        httpRequest.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        httpRequest.Headers.TryAddWithoutValidation("openai-intent", "conversation-panel");
        httpRequest.Headers.TryAddWithoutValidation("x-request-id", Guid.NewGuid().ToString());
        httpRequest.Headers.TryAddWithoutValidation("X-Initiator", "user");

        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorContent = await httpResponse.Content.ReadAsStringAsync();
            Log.Error("[GitHubCopilot] API Error {StatusCode}: {Error}",
                (int)httpResponse.StatusCode, errorContent);
            throw new HttpRequestException(
                $"GitHub Copilot API returned {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase}): {errorContent}");
        }

        var responseJson = await httpResponse.Content.ReadAsStringAsync();
        Log.Debug("[GitHubCopilot] Raw response: {Json}", responseJson);
        var copilotResponse = JsonSerializer.Deserialize<CopilotChatCompletionResponse>(responseJson);

        if (copilotResponse == null || copilotResponse.Choices.Count == 0)
        {
            throw new InvalidOperationException("Invalid response from GitHub Copilot");
        }

        return copilotResponse;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _tokenProvider?.Dispose();
    }
}
