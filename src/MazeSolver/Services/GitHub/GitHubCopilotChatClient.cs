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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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

        HttpResponseMessage httpResponse;
        try
        {
            Log.Debug("[GitHubCopilot] Sending HTTP request...");
            httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken);
            Log.Debug("[GitHubCopilot] HTTP response received. Status: {StatusCode}", (int)httpResponse.StatusCode);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Error(ex, "[GitHubCopilot] HTTP request timed out after {Timeout}", _httpClient.Timeout);
            throw new HttpRequestException($"HTTP request timed out after {_httpClient.Timeout}", ex);
        }
        catch (TaskCanceledException ex)
        {
            Log.Warning("[GitHubCopilot] HTTP request was cancelled by user");
            throw new OperationCanceledException("Request cancelled", ex, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "[GitHubCopilot] HTTP request failed: {Message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GitHubCopilot] Unexpected error during HTTP request: {Type} - {Message}", ex.GetType().Name, ex.Message);
            throw;
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorContent = await httpResponse.Content.ReadAsStringAsync();
            Log.Error("[GitHubCopilot] API Error {StatusCode}: {Error}",
                (int)httpResponse.StatusCode, errorContent);
            throw new HttpRequestException(
                $"GitHub Copilot API returned {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase}): {errorContent}");
        }

        string responseJson;
        try
        {
            responseJson = await httpResponse.Content.ReadAsStringAsync();
            Log.Debug("[GitHubCopilot] Response length: {Length} chars", responseJson.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GitHubCopilot] Failed to read response body: {Type} - {Message}", ex.GetType().Name, ex.Message);
            throw;
        }

        Log.Debug("[GitHubCopilot] Raw response: {Json}", responseJson);
        var copilotResponse = JsonSerializer.Deserialize<CopilotChatCompletionResponse>(responseJson);

        if (copilotResponse == null || copilotResponse.Choices.Count == 0)
        {
            Log.Error("[GitHubCopilot] Invalid/empty response. JSON: {Json}", responseJson);
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
