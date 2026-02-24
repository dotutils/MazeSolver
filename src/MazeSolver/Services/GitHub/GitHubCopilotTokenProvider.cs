using Serilog;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MazeSolver.Services.GitHub;

/// <summary>
/// Manages GitHub Copilot tokens - exchanges GitHub access tokens for Copilot tokens
/// and handles token refresh.
/// </summary>
public class GitHubCopilotTokenProvider : IDisposable
{
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string GitHubApiVersion = "2022-11-28";

    private readonly string _githubAccessToken;
    private readonly CopilotAccountType _accountType;
    private readonly HttpClient _httpClient;
    private CopilotToken? _currentToken;

    public event EventHandler<CopilotToken>? TokenRefreshed;

    public GitHubCopilotTokenProvider(
        string githubAccessToken,
        CopilotAccountType accountType = CopilotAccountType.Individual)
    {
        if (string.IsNullOrWhiteSpace(githubAccessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(githubAccessToken));

        _githubAccessToken = githubAccessToken;
        _accountType = accountType;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Gets a Copilot token by exchanging the GitHub access token.
    /// </summary>
    public async Task<CopilotToken> GetCopilotTokenAsync(CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.Add("Authorization", $"Bearer {_githubAccessToken}");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", "MazeSolver");
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(
                "GitHub access token is invalid or expired. Please re-authenticate using GitHub Login.");
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var token = JsonSerializer.Deserialize<CopilotToken>(json);

        if (token == null)
        {
            throw new InvalidOperationException("Failed to deserialize Copilot token response");
        }

        token.BaseUrl = ExtractBaseUrlFromToken(token.Token, _accountType);
        _currentToken = token;

        Log.Debug("Copilot token obtained. Expires at: {ExpiresAt}", token.ExpiresAt);

        return token;
    }

    /// <summary>
    /// Fetches the list of available models from the Copilot API.
    /// </summary>
    public async Task<List<ModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var copilotToken = await RefreshIfNeededAsync(cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{copilotToken.BaseUrl}/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {copilotToken.Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "MazeSolver/1.0");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.107.0");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.35.0");
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Log.Debug("Models API response: {Json}", json);

        var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(json);
        return modelsResponse?.Data ?? new List<ModelInfo>();
    }

    private string ExtractBaseUrlFromToken(string token, CopilotAccountType accountType)
    {
        var match = Regex.Match(token, @"proxy-ep=([^;]+)");
        if (match.Success)
        {
            var proxyHost = match.Groups[1].Value;
            var apiHost = proxyHost.Replace("proxy.", "api.");
            return $"https://{apiHost}";
        }

        return accountType switch
        {
            CopilotAccountType.Business => "https://api.business.githubcopilot.com",
            CopilotAccountType.Enterprise => "https://api.enterprise.githubcopilot.com",
            _ => "https://api.individual.githubcopilot.com"
        };
    }

    public CopilotToken? GetCurrentToken() => _currentToken;

    public async Task<CopilotToken> RefreshIfNeededAsync(CancellationToken cancellationToken = default)
    {
        if (_currentToken == null || _currentToken.IsExpired)
        {
            var newToken = await GetCopilotTokenAsync(cancellationToken);
            TokenRefreshed?.Invoke(this, newToken);
            return newToken;
        }

        return _currentToken;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
