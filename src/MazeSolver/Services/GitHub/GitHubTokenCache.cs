using Serilog;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MazeSolver.Services.GitHub;

/// <summary>
/// Persists the GitHub OAuth access token to disk with DPAPI encryption (per-user scope).
/// The Copilot API token is NOT persisted — it is short-lived (~30 min) and re-obtained
/// at runtime by exchanging the long-lived GitHub access token.
/// 
/// Storage location: %LocalAppData%\MazeSolver\auth.json
/// </summary>
public static class GitHubTokenCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MazeSolver");

    private static readonly string CacheFile = Path.Combine(CacheDir, "auth.json");

    private class CachedAuth
    {
        [JsonPropertyName("encrypted_token")]
        public string? EncryptedToken { get; set; }

        [JsonPropertyName("last_model")]
        public string? LastModel { get; set; }
    }

    /// <summary>
    /// Saves the GitHub OAuth access token encrypted with Windows DPAPI (CurrentUser scope).
    /// Optionally stores the last used model name alongside it.
    /// </summary>
    public static void SaveToken(string githubAccessToken, string? lastModel = null)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(githubAccessToken),
                null,
                DataProtectionScope.CurrentUser);

            var cached = new CachedAuth
            {
                EncryptedToken = Convert.ToBase64String(encrypted),
                LastModel = lastModel
            };

            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CacheFile, json);

            Log.Information("GitHub access token cached to {Path}", CacheFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cache GitHub access token");
        }
    }

    /// <summary>
    /// Loads the cached GitHub OAuth access token, decrypting with DPAPI.
    /// Returns (null, null) if no cached token exists or decryption fails.
    /// </summary>
    public static (string? Token, string? LastModel) LoadToken()
    {
        try
        {
            if (!File.Exists(CacheFile))
            {
                return (null, null);
            }

            var json = File.ReadAllText(CacheFile);
            var cached = JsonSerializer.Deserialize<CachedAuth>(json);

            if (string.IsNullOrEmpty(cached?.EncryptedToken))
            {
                return (null, cached?.LastModel);
            }

            var encrypted = Convert.FromBase64String(cached.EncryptedToken);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(decrypted);

            Log.Information("Loaded cached GitHub access token from {Path}", CacheFile);
            return (token, cached.LastModel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load cached GitHub access token");
            return (null, null);
        }
    }

    /// <summary>
    /// Clears the cached token file entirely.
    /// Called when a cached token turns out to be invalid/revoked.
    /// </summary>
    public static void ClearToken()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                File.Delete(CacheFile);
                Log.Information("Cleared cached GitHub access token");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear cached GitHub access token");
        }
    }

    /// <summary>
    /// Whether a cached token file exists on disk.
    /// Does not validate whether the token is still valid.
    /// </summary>
    public static bool HasCachedToken()
    {
        try
        {
            return File.Exists(CacheFile);
        }
        catch
        {
            return false;
        }
    }
}
