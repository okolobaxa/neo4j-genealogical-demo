using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Neo4gen.Server.Agent;

/// <summary>
/// One block of an Aura Agent answer, preserving the distinction between the final text,
/// its reasoning trace, and any tool calls it made along the way. <see cref="AuraAgentChatClient"/>
/// maps these onto <c>Microsoft.Extensions.AI</c> content types so they survive as separate
/// AG-UI events (THINKING_*, TOOL_CALL_*) instead of being flattened into one text blob.
/// </summary>
public abstract record AuraContentBlock;

public sealed record AuraTextBlock(string Text) : AuraContentBlock;

public sealed record AuraThinkingBlock(string Thinking) : AuraContentBlock;

public sealed record AuraToolUseBlock(string Id, string Name, IDictionary<string, object?> Input) : AuraContentBlock;

public sealed record AuraToolResultBlock(string ToolUseId, string OutputJson) : AuraContentBlock;

/// <summary>
/// Thin client for a Neo4j Aura Agent. Handles the OAuth 2.0 client-credentials flow
/// (caching the bearer token until it nears expiry) and POSTs a user question to the agent's
/// endpoint, returning the agent's answer as an ordered list of content blocks.
///
/// Registered as a singleton so the cached token survives across requests; the underlying
/// <see cref="HttpClient"/> is obtained per call from <see cref="IHttpClientFactory"/>.
/// </summary>
public sealed class AuraAgentClient
{
    // Neo4j documents a 60s max for an agent invocation; the HttpClient timeout is set higher
    // than that in DI so we surface the agent's own timeout rather than a client-side one.
    public const string HttpClientName = "aura-agent";

    // OAuth 2.0 token endpoint for the client-credentials grant. Fixed by Neo4j.
    private const string TokenUrl = "https://api.neo4j.io/oauth/token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuraAgentOptions _options;
    private readonly ILogger<AuraAgentClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public AuraAgentClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AuraAgentOptions> options,
        ILogger<AuraAgentClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="question"/> to the Aura Agent and returns its answer as an ordered
    /// list of content blocks (thinking, tool use, tool result, text — in the order the agent
    /// produced them). Never throws for expected failures (missing config, HTTP errors) — instead
    /// returns a single text block with a short human-readable message so the chat surface always
    /// has something to display.
    /// </summary>
    public async Task<IReadOnlyList<AuraContentBlock>> AskAsync(string question, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            _logger.LogWarning(
                "Aura Agent is not configured. Set AuraAgent:EndpointUrl and AuraAgent:ClientId in " +
                "appsettings, and the secret with: dotnet user-secrets set \"AuraAgent:ClientSecret\" \"<secret>\"");
            return [new AuraTextBlock("The Aura Agent is not configured yet. Please set the endpoint URL and Aura API credentials.")];
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return [new AuraTextBlock("Please provide a question for the agent.")];
        }

        try
        {
            var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new AgentInvocationRequest(question));

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Aura Agent invocation failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
                return [new AuraTextBlock($"The Aura Agent request failed (HTTP {(int)response.StatusCode}).")];
            }

            _logger.LogInformation("Aura Agent answer (raw JSON): {Body}", body);

            return ParseContentBlocks(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling the Aura Agent.");
            return [new AuraTextBlock("The Aura Agent request could not be completed due to an unexpected error.")];
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // Fast path: a still-valid cached token.
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _cachedToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials"
            });

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Aura OAuth token request failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
                throw new HttpRequestException(
                    $"Aura OAuth token request failed with status {(int)response.StatusCode}.");
            }

            var token = JsonSerializer.Deserialize<OAuthTokenResponse>(body)
                ?? throw new InvalidOperationException("Aura OAuth token response was empty.");

            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException("Aura OAuth token response had no access_token.");
            }

            _cachedToken = token.AccessToken;
            // Refresh a minute early to avoid using a token that expires mid-request.
            var lifetime = token.ExpiresIn > 60 ? token.ExpiresIn - 60 : token.ExpiresIn;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Parses the agent's JSON response into an ordered list of content blocks. The Aura Agent
    /// returns "a structured JSON message"; its exact shape isn't fixed in the docs, so we
    /// recognize the Anthropic-style message shape and fall back to common text-bearing field
    /// names, then the raw body, if it doesn't match.
    /// </summary>
    private static IReadOnlyList<AuraContentBlock> ParseContentBlocks(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return [new AuraTextBlock(root.GetString() ?? string.Empty)];
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Anthropic-style message shape: { role: "assistant", content: [ { type: "thinking" | "tool_use" |
                // "tool_result" | "text", ... } ] }. Keep every block, in order, instead of only "text".
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var raw = content.Deserialize<List<AgentContentBlock>>();
                    var blocks = raw?.Select(ToAuraBlock).OfType<AuraContentBlock>().ToList();

                    if (blocks is { Count: > 0 })
                    {
                        return blocks;
                    }
                }

                string[] candidateFields = ["output", "response", "answer", "message", "content", "text", "result"];
                foreach (var field in candidateFields)
                {
                    if (root.TryGetProperty(field, out var value))
                    {
                        var text = value.ValueKind == JsonValueKind.String
                            ? value.GetString() ?? string.Empty
                            : value.GetRawText();
                        return [new AuraTextBlock(text)];
                    }
                }
            }

            // Unknown shape — return the raw JSON so nothing is silently dropped.
            return [new AuraTextBlock(body)];
        }
        catch (JsonException)
        {
            // Not JSON (already plain text) — return as-is.
            return [new AuraTextBlock(body)];
        }
    }

    /// <summary>
    /// Converts one parsed "content" entry into its typed block. <see cref="AgentContentBlock.Input"/>
    /// and <see cref="AgentContentBlock.Output"/> are round-tripped through <c>GetRawText()</c> before
    /// re-parsing so the result carries no <see cref="JsonElement"/> tied to the disposed source
    /// <see cref="JsonDocument"/>.
    ///
    /// The Aura Agent doesn't use a fixed "tool_use"/"tool_result" pair for every tool: its built-in
    /// Cypher-template tools (e.g. Find_Candidates_for_Event) emit "cypher_template_tool_use" /
    /// "cypher_template_tool_result" instead of the plain Anthropic names used by free-form tools
    /// like Natural_Language_to_Cypher_Tool. Match on suffix so every tool category is captured.
    /// </summary>
    private static AuraContentBlock? ToAuraBlock(AgentContentBlock block)
    {
        if (block.Type is null)
        {
            return null;
        }

        if (block.Type == "text" && block.Text is not null)
        {
            return new AuraTextBlock(block.Text);
        }

        if (block.Type == "thinking" && block.Thinking is not null)
        {
            return new AuraThinkingBlock(block.Thinking);
        }

        if (block.Type.EndsWith("tool_use", StringComparison.Ordinal) && block.Name is not null)
        {
            var input = block.Input.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(block.Input.GetRawText()) ?? []
                : new Dictionary<string, object?>();
            return new AuraToolUseBlock(block.Id ?? Guid.NewGuid().ToString("N"), block.Name, input);
        }

        if (block.Type.EndsWith("tool_result", StringComparison.Ordinal) && block.ToolUseId is not null)
        {
            var output = block.Output.ValueKind != JsonValueKind.Undefined ? block.Output.GetRawText() : string.Empty;
            return new AuraToolResultBlock(block.ToolUseId, output);
        }

        return null;
    }

    private sealed record AgentInvocationRequest(
        [property: JsonPropertyName("input")] string Input);

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    /// <summary>
    /// One entry of an Anthropic-style message's "content" array, covering every block type
    /// ("text", "thinking", "tool_use", "tool_result") the Aura Agent may emit. Fields that don't
    /// apply to a given block's "type" are simply left at their default value.
    /// </summary>
    private sealed record AgentContentBlock(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("thinking")] string? Thinking,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("input")] JsonElement Input,
        [property: JsonPropertyName("tool_use_id")] string? ToolUseId,
        [property: JsonPropertyName("output")] JsonElement Output);
}
