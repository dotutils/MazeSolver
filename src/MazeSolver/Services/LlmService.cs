using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Serilog;
using System.Text.Json;

namespace MazeSolver.Services;

/// <summary>
/// Wrapper service for Anthropic LLM API, implementing the provider-independent ILlmService.
/// Converts between Anthropic SDK types and the unified LlmTypes.
/// </summary>
public class LlmService : ILlmService
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    
    /// <summary>
    /// Maximum context window for Claude Sonnet 4.5 (200K tokens)
    /// </summary>
    public int MaxContextTokens { get; set; } = 200_000;

    public LlmService()
    {
        var endpoint = Environment.GetEnvironmentVariable("LLM_ENDPOINT") 
            ?? throw new InvalidOperationException("LLM_ENDPOINT environment variable not set");
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") 
            ?? throw new InvalidOperationException("LLM_API_KEY environment variable not set");
        _model = Environment.GetEnvironmentVariable("LLM_MODEL") 
            ?? throw new InvalidOperationException("LLM_MODEL environment variable not set");

        Log.Information("Initializing LLM service with endpoint: {Endpoint}, model: {Model}", endpoint, _model);

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", endpoint);
        
        _client = new AnthropicClient();
    }

    public async Task<LlmResponse> SendMessageAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        List<LlmToolDefinition>? tools = null,
        int maxTokens = 4096,
        CancellationToken cancellationToken = default)
    {
        Log.Debug("Sending message to LLM. Messages count: {Count}, Has tools: {HasTools}", 
            messages.Count, tools != null);

        // Convert unified messages → Anthropic MessageParam
        var anthropicMessages = messages.Select(ConvertToAnthropicMessage).ToList();

        // Convert unified tools → Anthropic ToolUnion
        List<ToolUnion>? anthropicTools = null;
        if (tools != null && tools.Count > 0)
        {
            anthropicTools = tools.Select(ConvertToAnthropicTool).ToList();
        }

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = maxTokens,
            System = systemPrompt,
            Messages = anthropicMessages,
            Tools = anthropicTools
        };

        int maxRetries = 5;
        int retryDelaySeconds = 10;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _client.Messages.Create(parameters, cancellationToken: cancellationToken);
                return ConvertFromAnthropicResponse(response);
            }
            catch (AnthropicRateLimitException) when (attempt < maxRetries)
            {
                Log.Warning("Rate limit hit (attempt {Attempt}/{MaxRetries}). Waiting {Delay}s before retry...", 
                    attempt, maxRetries, retryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
                retryDelaySeconds *= 2;
            }
            catch (AnthropicBadRequestException ex) when (
                ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("maximum", StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(ex, "Context overflow detected");
                throw new ContextOverflowException("Context window exceeded", ex);
            }
            catch (AnthropicApiException ex)
            {
                Log.Error(ex, "LLM API error: {Message}", ex.Message);
                throw;
            }
        }

        throw new InvalidOperationException("Max retries exceeded for rate limit");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Information("Testing LLM connection...");
            
            var messages = new List<LlmMessage>
            {
                LlmMessage.User("Say 'Connection successful!' and nothing else.")
            };

            var response = await SendMessageAsync(
                "You are a helpful assistant.",
                messages,
                maxTokens: 50,
                cancellationToken: cancellationToken);

            Log.Information("LLM connection test successful. Response: {Response}", 
                response.GetText());
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LLM connection test failed");
            return false;
        }
    }

    #region Conversion: Unified → Anthropic

    private static MessageParam ConvertToAnthropicMessage(LlmMessage msg)
    {
        var role = msg.Role == LlmRole.Assistant ? Role.Assistant : Role.User;
        var contentBlocks = new List<ContentBlockParam>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case LlmTextBlock textBlock:
                    contentBlocks.Add(new TextBlockParam { Text = textBlock.Text });
                    break;
                case LlmToolUseBlock toolUse:
                    contentBlocks.Add(new ToolUseBlockParam
                    {
                        ID = toolUse.Id,
                        Name = toolUse.Name,
                        Input = new Dictionary<string, JsonElement>(toolUse.Input)
                    });
                    break;
                case LlmToolResultBlock toolResult:
                    contentBlocks.Add(new ToolResultBlockParam(toolResult.ToolUseId)
                    {
                        Content = toolResult.ResultContent
                    });
                    break;
            }
        }

        return new MessageParam
        {
            Role = role,
            Content = contentBlocks
        };
    }

    private static ToolUnion ConvertToAnthropicTool(LlmToolDefinition tool)
    {
        // Reconstruct the InputSchema from the generic object
        var schemaJson = JsonSerializer.Serialize(tool.InputSchema);
        var inputSchema = JsonSerializer.Deserialize<InputSchema>(schemaJson) 
            ?? new InputSchema();

        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = inputSchema
        };
    }

    #endregion

    #region Conversion: Anthropic → Unified

    private static LlmResponse ConvertFromAnthropicResponse(Message response)
    {
        // Extract stop reason
        string stopReason = "unknown";
        if (response.StopReason != null)
        {
            var stopReasonStr = response.StopReason.ToString() ?? "";
            if (stopReasonStr.Contains("Json = "))
            {
                var startIdx = stopReasonStr.IndexOf("Json = ") + 7;
                var endIdx = stopReasonStr.IndexOf(" ", startIdx);
                if (endIdx == -1) endIdx = stopReasonStr.IndexOf("}", startIdx);
                if (endIdx > startIdx)
                {
                    stopReason = stopReasonStr.Substring(startIdx, endIdx - startIdx).Trim();
                }
            }
            else
            {
                stopReason = stopReasonStr;
            }
        }

        // Convert content blocks
        var contentBlocks = new List<LlmContentBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                contentBlocks.Add(new LlmTextBlock { Text = textBlock.Text });
            }
            else if (block.TryPickToolUse(out var toolUseBlock))
            {
                contentBlocks.Add(new LlmToolUseBlock
                {
                    Id = toolUseBlock.ID,
                    Name = toolUseBlock.Name,
                    Input = toolUseBlock.Input
                });
            }
        }

        return new LlmResponse
        {
            Content = contentBlocks,
            InputTokens = response.Usage.InputTokens,
            OutputTokens = response.Usage.OutputTokens,
            StopReason = stopReason
        };
    }

    #endregion

    #region Static Tool Definitions (for convenience)

    public static LlmToolDefinition CreateGetNeighboursTool()
    {
        return new LlmToolDefinition
        {
            Name = "GetNeighbours",
            Description = "Get the status of all 8 neighbouring cells around a given position. " +
                         "Returns status for each direction: N, NE, E, SE, S, SW, W, NW. " +
                         "Status can be 'path', 'wall', 'exit', or 'out_of_bounds'. " +
                         "Use this tool to explore the maze and find the path to the exit.",
            InputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["x"] = new { type = "integer", description = "X coordinate (column) of the cell to check neighbours for" },
                    ["y"] = new { type = "integer", description = "Y coordinate (row) of the cell to check neighbours for" }
                },
                required = new[] { "x", "y" }
            }
        };
    }

    public static LlmToolDefinition CreateGetContextUsageTool()
    {
        return new LlmToolDefinition
        {
            Name = "GetContextUsage",
            Description = "Get the current context window usage statistics. " +
                         "Returns the total tokens used so far and the percentage of the context window filled. " +
                         "Use this tool to monitor your context usage and plan accordingly to avoid context overflow.",
            InputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>(),
                required = Array.Empty<string>()
            }
        };
    }

    #endregion
}
