using MazeSolver.Services.GitHub;
using Serilog;
using System.Net.Http;
using System.Text.Json;

namespace MazeSolver.Services;

/// <summary>
/// LLM service implementation that uses GitHub Copilot as the backend.
/// Translates between the unified LlmTypes and the Copilot API DTOs.
/// </summary>
public class GitHubCopilotLlmService : ILlmService, IDisposable
{
    private readonly GitHubCopilotChatClient _client;
    private readonly string _modelName;
    private readonly GitHubCopilotTokenProvider? _tokenProvider;

    /// <summary>
    /// Context window varies by model; default to 128K which is conservative for most models.
    /// Will be updated from the models API when available.
    /// </summary>
    public int MaxContextTokens { get; set; } = 128_000;

    public GitHubCopilotLlmService(GitHubCopilotChatClient client, string modelName, GitHubCopilotTokenProvider? tokenProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelName = modelName;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Queries the models API to set the accurate context window size for the current model.
    /// Should be called after construction. Returns the resolved value.
    /// </summary>
    public async Task<int> ResolveContextWindowAsync(CancellationToken cancellationToken = default)
    {
        if (_tokenProvider != null)
        {
            var contextWindow = await _tokenProvider.GetModelContextWindowAsync(_modelName, cancellationToken);
            if (contextWindow.HasValue)
            {
                MaxContextTokens = contextWindow.Value;
                Log.Information("Set MaxContextTokens to {MaxContextTokens:N0} for model {Model}", MaxContextTokens, _modelName);
            }
        }
        return MaxContextTokens;
    }

    public async Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        List<LlmToolDefinition>? tools = null,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        Log.Debug("[CopilotLlm] Sending message. Messages count: {Count}, Has tools: {HasTools}",
            messages.Count, tools != null);

        // Convert unified messages → Copilot messages
        var copilotMessages = new List<CopilotChatMessageDto>();

        // System message first
        copilotMessages.Add(new CopilotChatMessageDto
        {
            Role = "system",
            Content = systemPrompt
        });

        // Convert conversation messages
        foreach (var msg in messages)
        {
            ConvertAndAddMessages(copilotMessages, msg);
        }

        // Convert tools
        List<CopilotToolDto>? copilotTools = null;
        if (tools != null && tools.Count > 0)
        {
            copilotTools = tools.Select(ConvertTool).ToList();
        }

        var request = new CopilotChatCompletionRequest
        {
            Model = _modelName,
            Messages = copilotMessages,
            MaxTokens = maxTokens,
            Tools = copilotTools,
            ToolChoice = copilotTools != null ? "auto" : null
        };

        int maxRetries = 5;
        int retryDelaySeconds = 10;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _client.SendAsync(request, cancellationToken);
                return ConvertResponse(response);
            }
            catch (HttpRequestException ex) when (
                attempt < maxRetries &&
                (ex.Message.Contains("429") || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warning("Rate limit hit (attempt {Attempt}/{MaxRetries}). Waiting {Delay}s before retry...",
                    attempt, maxRetries, retryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                retryDelaySeconds *= 2;
            }
            catch (HttpRequestException ex) when (
                ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(ex, "Context overflow detected");
                throw new ContextOverflowException("Context window exceeded", ex);
            }
            catch (HttpRequestException ex) when (
                ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) &&
                attempt < maxRetries)
            {
                Log.Warning(ex, "HTTP timeout (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s...",
                    attempt, maxRetries, retryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                retryDelaySeconds *= 2;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Log.Warning("Operation cancelled by user");
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                Log.Warning(ex, "Unexpected error (attempt {Attempt}/{MaxRetries}): {Type} - {Message}. Retrying in {Delay}s...",
                    attempt, maxRetries, ex.GetType().Name, ex.Message, retryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                retryDelaySeconds *= 2;
            }
        }

        throw new InvalidOperationException("Max retries exceeded for rate limit");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Information("Testing GitHub Copilot connection...");

            var request = new CopilotChatCompletionRequest
            {
                Model = _modelName,
                Messages = new List<CopilotChatMessageDto>
                {
                    new() { Role = "user", Content = "Say 'Connection successful!' and nothing else." }
                },
                MaxTokens = 50
            };

            var response = await _client.SendAsync(request, cancellationToken);
            var text = response.Choices.FirstOrDefault()?.Message.Content;

            Log.Information("GitHub Copilot connection test successful. Response: {Response}", text ?? "No text");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GitHub Copilot connection test failed");
            return false;
        }
    }

    #region Conversion: Unified types → Copilot types

    private static void ConvertAndAddMessages(List<CopilotChatMessageDto> copilotMessages, LlmMessage msg)
    {
        string role = msg.Role == LlmRole.Assistant ? "assistant" : "user";

        var textParts = new List<string>();
        var toolCallParts = new List<CopilotToolCallDto>();
        var toolResults = new List<(string toolCallId, string content)>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case LlmTextBlock textBlock:
                    textParts.Add(textBlock.Text);
                    break;
                case LlmToolUseBlock toolUse:
                    toolCallParts.Add(new CopilotToolCallDto
                    {
                        Id = toolUse.Id,
                        Type = "function",
                        Function = new CopilotFunctionCallDto
                        {
                            Name = toolUse.Name,
                            Arguments = JsonSerializer.Serialize(toolUse.Input)
                        }
                    });
                    break;
                case LlmToolResultBlock toolResult:
                    toolResults.Add((toolResult.ToolUseId, toolResult.ResultContent));
                    break;
            }
        }

        // Add assistant/user message with text and/or tool calls
        if (textParts.Any() || toolCallParts.Any())
        {
            copilotMessages.Add(new CopilotChatMessageDto
            {
                Role = role,
                Content = textParts.Any() ? string.Join("\n", textParts) : null,
                ToolCalls = toolCallParts.Any() ? toolCallParts : null
            });
        }

        // Add tool result messages (each as separate "tool" role message)
        foreach (var (toolCallId, content) in toolResults)
        {
            copilotMessages.Add(new CopilotChatMessageDto
            {
                Role = "tool",
                ToolCallId = toolCallId,
                Content = content
            });
        }
    }

    private static CopilotToolDto ConvertTool(LlmToolDefinition tool)
    {
        return new CopilotToolDto
        {
            Type = "function",
            Function = new CopilotFunctionDto
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.InputSchema
            }
        };
    }

    #endregion

    #region Conversion: Copilot response → Unified types

    private static LlmResponse ConvertResponse(CopilotChatCompletionResponse copilotResponse)
    {
        if (copilotResponse.Choices.Count == 0)
            throw new InvalidOperationException("No choices in Copilot response");

        var contentBlocks = new List<LlmContentBlock>();
        string? stopReason = null;

        // Copilot API (especially for Claude models) may split text and tool_calls
        // across multiple choices. Merge them all into a single unified response.
        foreach (var choice in copilotResponse.Choices)
        {
            // Text content
            if (!string.IsNullOrEmpty(choice.Message.Content))
            {
                contentBlocks.Add(new LlmTextBlock { Text = choice.Message.Content });
            }

            // Tool calls
            if (choice.Message.ToolCalls != null)
            {
                foreach (var toolCall in choice.Message.ToolCalls)
                {
                    var args = string.IsNullOrEmpty(toolCall.Function.Arguments)
                        ? new Dictionary<string, JsonElement>()
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolCall.Function.Arguments)
                          ?? new Dictionary<string, JsonElement>();

                    contentBlocks.Add(new LlmToolUseBlock
                    {
                        Id = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Input = args
                    });
                }
            }

            // Use the most significant stop reason across choices
            // (tool_calls > stop > anything else)
            var mapped = choice.FinishReason switch
            {
                "stop" => "end_turn",
                "tool_calls" => "tool_use",
                "length" => "max_tokens",
                _ => choice.FinishReason ?? "end_turn"
            };

            if (stopReason == null || mapped == "tool_use" || mapped == "max_tokens")
            {
                stopReason = mapped;
            }
        }

        var usage = copilotResponse.Usage ?? new CopilotUsageDto();

        return new LlmResponse
        {
            Content = contentBlocks,
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
            StopReason = stopReason ?? "end_turn"
        };
    }

    #endregion

    public void Dispose()
    {
        _client?.Dispose();
    }
}
