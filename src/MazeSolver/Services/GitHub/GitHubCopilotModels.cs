using System.Text.Json.Serialization;

namespace MazeSolver.Services.GitHub;

/// <summary>
/// Account type for GitHub Copilot.
/// </summary>
public enum CopilotAccountType
{
    Individual,
    Business,
    Enterprise
}

/// <summary>
/// GitHub Copilot token with metadata.
/// </summary>
public class CopilotToken
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public long ExpiresAtUnix { get; set; }

    [JsonPropertyName("refresh_in")]
    public int RefreshIn { get; set; }

    [JsonIgnore]
    public DateTimeOffset ExpiresAt
    {
        get => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix);
        set => ExpiresAtUnix = value.ToUnixTimeSeconds();
    }

    [JsonIgnore]
    public string? BaseUrl { get; set; }

    [JsonIgnore]
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-5);
}

/// <summary>
/// Response from GitHub device code flow initiation.
/// </summary>
public class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>
/// Response from GitHub access token polling.
/// </summary>
public class AccessTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Response from GitHub Copilot models API.
/// </summary>
public class ModelsResponse
{
    [JsonPropertyName("data")]
    public List<ModelInfo> Data { get; set; } = new();
}

/// <summary>
/// Information about an available GitHub Copilot model.
/// </summary>
public class ModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model_picker_enabled")]
    public bool ModelPickerEnabled { get; set; }

    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelCapabilities? Capabilities { get; set; }

    [JsonPropertyName("policy")]
    public ModelPolicy Policy { get; set; } = new();

    /// <summary>
    /// Returns the max context window tokens from capabilities, or null if not available.
    /// </summary>
    [JsonIgnore]
    public int? MaxContextWindowTokens => Capabilities?.Limits?.MaxContextWindowTokens;

    public override string ToString() => string.IsNullOrEmpty(Name) ? Id : $"{Name} ({Id})";
}

/// <summary>
/// Model capabilities returned by the Copilot models API.
/// </summary>
public class ModelCapabilities
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelLimits? Limits { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// Token limits for a model.
/// </summary>
public class ModelLimits
{
    [JsonPropertyName("max_context_window_tokens")]
    public int? MaxContextWindowTokens { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("max_prompt_tokens")]
    public int? MaxPromptTokens { get; set; }
}

/// <summary>
/// Model policy information.
/// </summary>
public class ModelPolicy
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

#region Chat Completion DTOs

/// <summary>
/// Chat completion request for GitHub Copilot API (OpenAI-compatible format).
/// </summary>
public class CopilotChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<CopilotChatMessageDto> Messages { get; set; } = new();

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CopilotToolDto>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

/// <summary>
/// Chat message DTO for Copilot API requests/responses.
/// </summary>
public class CopilotChatMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CopilotToolCallDto>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

/// <summary>
/// Tool definition for function calling.
/// </summary>
public class CopilotToolDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public CopilotFunctionDto Function { get; set; } = new();
}

/// <summary>
/// Function definition.
/// </summary>
public class CopilotFunctionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

/// <summary>
/// Tool call from assistant.
/// </summary>
public class CopilotToolCallDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public CopilotFunctionCallDto Function { get; set; } = new();
}

/// <summary>
/// Function call details.
/// </summary>
public class CopilotFunctionCallDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>
/// Response from GitHub Copilot chat completions.
/// </summary>
public class CopilotChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<CopilotChoiceDto> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public CopilotUsageDto? Usage { get; set; }

    [JsonPropertyName("created")]
    public long? Created { get; set; }
}

/// <summary>
/// Choice in the response.
/// </summary>
public class CopilotChoiceDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public CopilotChatMessageDto Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>
/// Token usage information.
/// </summary>
public class CopilotUsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

#endregion
