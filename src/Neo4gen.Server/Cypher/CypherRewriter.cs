using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Neo4gen.Server.Cypher;

/// <summary>
/// Rewrites an aggregating Cypher query into one that returns graph elements, using an OpenAI
/// chat model with structured (JSON-schema) output. Used by the frontend's "Visualise" button:
/// aggregation queries (WITH ... COUNT ... RETURN scalar columns) render as a table in the graph
/// canvas, so we ask the model to preserve the ranking block and re-expand the surviving nodes.
///
/// The OpenAI API key lives in user-secrets (from the Neo4gen.Server directory):
///   dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
/// If no key is configured, <see cref="RewriteAsync"/> returns the original query unchanged so the
/// button still works (it just runs the query as-is).
/// </summary>
public sealed class CypherRewriter
{
    // System prompt describing the rewrite task, verbatim.
    private const string SystemPrompt =
        "Rewrites an aggregating Cypher query so Neo4j Browser renders a graph instead of a table. " +
        "When a query uses WITH ... COUNT/collect ... ORDER BY ... LIMIT and then returns scalar " +
        "columns (e.g. p.name, counts), the browser shows a table because no nodes or relationships " +
        "are returned. Aggregation and graph-element return can't happen in the same pass, so this " +
        "tool preserves the aggregation/ranking block, then adds a second MATCH after the LIMIT to " +
        "re-expand the surviving nodes and returns the nodes and relationships themselves " +
        "(e.g. RETURN p, r, d) or a path (RETURN path). Aggregate values may be carried through for " +
        "styling (node size, captions). Optionally caps per-node expansion to avoid cluttering the canvas.";

    private readonly IChatClient? _chatClient;
    private readonly ILogger<CypherRewriter> _logger;

    public CypherRewriter(IConfiguration configuration, ILogger<CypherRewriter> logger)
    {
        _logger = logger;

        // Same resolution order as ProverbsAgentFactory so both share one OpenAI configuration.
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? configuration["OPENAI_API_KEY"]
            ?? configuration["GitHubToken"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "No OpenAI API key configured; the Visualise button will run Cypher as-is without rewriting. " +
                "Set one with: dotnet user-secrets set \"OpenAI:ApiKey\" \"<your-openai-key>\"");
            return;
        }

        var model = configuration["OpenAI:Model"]
            ?? configuration["OPENAI_MODEL"]
            ?? "gpt-4o-mini";

        var baseUrl = configuration["OpenAI:BaseUrl"]
            ?? configuration["OPENAI_BASE_URL"];

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }

        _chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    /// <summary>
    /// Returns a graph-returning rewrite of <paramref name="cypher"/>, or the original query if
    /// OpenAI is not configured or the call fails (so the caller always has something to run).
    /// </summary>
    public async Task<string> RewriteAsync(string cypher, CancellationToken cancellationToken)
    {
        if (_chatClient is null || string.IsNullOrWhiteSpace(cypher))
        {
            return cypher;
        }

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, cypher),
            };

            var options = new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<CypherRewriteResult>(
                    schemaName: "cypher_rewrite",
                    schemaDescription: "The rewritten Cypher query that returns graph nodes/relationships."),
            };

            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            var result = JsonSerializer.Deserialize<CypherRewriteResult>(response.Text, JsonOptions);
            var rewritten = result?.Query;

            return string.IsNullOrWhiteSpace(rewritten) ? cypher : rewritten;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cypher rewrite via OpenAI failed; falling back to the original query.");
            return cypher;
        }
    }

    // Case-insensitive so both "query" and "Query" from the model parse.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Structured-output shape requested from the model.
    private sealed record CypherRewriteResult(
        [property: JsonPropertyName("query")] string Query);
}
