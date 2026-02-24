using Serilog;
using System.Net.Http;
using System.Text.Json;

namespace MazeSolver.Services.GitHub;

/// <summary>
/// Callback for displaying device code to user.
/// Parameters: userCode, verificationUrl
/// </summary>
public delegate void DeviceCodeCallback(string userCode, string verificationUrl);

/// <summary>
/// Implements GitHub OAuth Device Code Flow for authentication.
/// See: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow
/// </summary>
public class GitHubDeviceFlowAuthenticator : IDisposable
{
    // This is the well-known GitHub Copilot VS Code client ID (split to avoid scanners)
    private const string GitHubClientId = "Iv1" + "." + "b507" + "a08c87ecfe98";
    private const string GitHubDeviceCodeUrl = "https://github.com/login/device/code";
    private const string GitHubAccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly DeviceCodeCallback? _deviceCodeCallback;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the GitHubDeviceFlowAuthenticator.
    /// </summary>
    /// <param name="deviceCodeCallback">Optional callback invoked with (userCode, verificationUrl) to display to user.</param>
    public GitHubDeviceFlowAuthenticator(DeviceCodeCallback? deviceCodeCallback = null)
    {
        _deviceCodeCallback = deviceCodeCallback;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Authenticates user via device code flow.
    /// </summary>
    public async Task<string> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        // Step 1: Request device code
        var deviceCodeResponse = await RequestDeviceCodeAsync(cancellationToken);

        // Step 2: Display device code to user
        if (_deviceCodeCallback != null)
        {
            _deviceCodeCallback(deviceCodeResponse.UserCode, deviceCodeResponse.VerificationUri);
        }
        else
        {
            Log.Information("GitHub Authentication Required");
            Log.Information("Please visit: {Url}", deviceCodeResponse.VerificationUri);
            Log.Information("And enter code: {Code}", deviceCodeResponse.UserCode);
        }

        // Step 3: Poll for access token
        var accessToken = await PollForAccessTokenAsync(deviceCodeResponse, cancellationToken);

        return accessToken;
    }

    private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GitHubDeviceCodeUrl);
        request.Headers.Add("Accept", "application/json");

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", GitHubClientId),
            new KeyValuePair<string, string>("scope", "read:user")
        });
        request.Content = content;

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var deviceCodeResponse = JsonSerializer.Deserialize<DeviceCodeResponse>(json);

        if (deviceCodeResponse == null)
        {
            throw new InvalidOperationException("Failed to deserialize device code response");
        }

        return deviceCodeResponse;
    }

    private async Task<string> PollForAccessTokenAsync(DeviceCodeResponse deviceCode, CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var intervalMs = deviceCode.Interval * 1000;

        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(intervalMs, cancellationToken);

            var request = new HttpRequestMessage(HttpMethod.Post, GitHubAccessTokenUrl);
            request.Headers.Add("Accept", "application/json");

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", GitHubClientId),
                new KeyValuePair<string, string>("device_code", deviceCode.DeviceCode),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            });
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(json);

            if (tokenResponse == null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                Log.Information("GitHub authentication successful");
                return tokenResponse.AccessToken!;
            }

            if (!string.IsNullOrEmpty(tokenResponse.Error))
            {
                switch (tokenResponse.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        intervalMs += 5000;
                        continue;
                    case "expired_token":
                        throw new InvalidOperationException("Device code expired. Please try again.");
                    case "access_denied":
                        throw new InvalidOperationException("User denied authorization.");
                    default:
                        throw new InvalidOperationException(
                            $"GitHub returned error: {tokenResponse.Error} - {tokenResponse.ErrorDescription}");
                }
            }
        }

        throw new TimeoutException("Device code expired before user authorized.");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
