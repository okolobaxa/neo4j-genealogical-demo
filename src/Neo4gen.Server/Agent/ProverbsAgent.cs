using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Neo4gen.Server.Agent;

// =================
// State Management
// =================
public sealed class ProverbsState
{
    public List<string> Proverbs { get; set; } = [];
}

/// <summary>
/// Builds the AG-UI agent exposed by the server. Ported from the CopilotKit &lt;&gt; Microsoft
/// Agent Framework starter and adapted for neo4gen: it wires an OpenAI-compatible chat client
/// (GitHub Models by default), registers the tool set, and wraps everything in
/// <see cref="SharedStateAgent"/> so the frontend state stays in sync.
///
/// This "proverbs" domain is a working demo of the full pipeline (shared state + generative UI +
/// tools). Swap the state, tools, and prompt for real neo4gen (graph) capabilities.
/// </summary>
public sealed class ProverbsAgentFactory
{
    private readonly ProverbsState _state = new();
    private readonly ILogger<ProverbsAgentFactory> _logger;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly OpenAIClient _openAiClient;
    private readonly string _model;

    public ProverbsAgentFactory(IConfiguration configuration, ILoggerFactory loggerFactory, JsonSerializerOptions jsonSerializerOptions)
    {
        _logger = loggerFactory.CreateLogger<ProverbsAgentFactory>();
        _jsonSerializerOptions = jsonSerializerOptions;

        // OpenAI API key. Store it in user-secrets (from the Neo4gen.Server directory):
        //   dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
        // Env var OPENAI_API_KEY and a GitHub Models token are also accepted as fallbacks.
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? configuration["OPENAI_API_KEY"]
            ?? configuration["GitHubToken"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Don't crash Aspire startup — log loudly and defer the failure to request time so the
            // rest of the app (frontend, /api, health checks) still boots.
            _logger.LogWarning(
                "No OpenAI API key configured. The AG-UI agent will return auth errors until a key is set. " +
                "Set one with: dotnet user-secrets set \"OpenAI:ApiKey\" \"<your-openai-key>\"");
            apiKey = "missing-api-key";
        }

        _model = configuration["OpenAI:Model"]
            ?? configuration["OPENAI_MODEL"]
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-4o-mini";

        // Only override the endpoint when explicitly configured (GitHub Models, Azure, a proxy…);
        // otherwise the SDK targets the official OpenAI API at https://api.openai.com/v1.
        var baseUrl = configuration["OpenAI:BaseUrl"]
            ?? configuration["OPENAI_BASE_URL"]
            ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL");

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }

        _openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    public AIAgent CreateAgent()
    {
        var chatClient = _openAiClient.GetChatClient(_model).AsIChatClient();

        var chatClientAgent = new ChatClientAgent(
            chatClient,
            name: "ProverbsAgent",
            description: @"A helpful assistant that helps manage and discuss proverbs.
            You have tools available to add, set, or retrieve proverbs from the list.
            When discussing proverbs, ALWAYS use the get_proverbs tool to see the current list before mentioning, updating, or discussing proverbs with the user.",
            tools:
            [
                AIFunctionFactory.Create(GetProverbs, options: new() { Name = "get_proverbs", SerializerOptions = _jsonSerializerOptions }),
                AIFunctionFactory.Create(AddProverbs, options: new() { Name = "add_proverbs", SerializerOptions = _jsonSerializerOptions }),
                AIFunctionFactory.Create(SetProverbs, options: new() { Name = "set_proverbs", SerializerOptions = _jsonSerializerOptions }),
                AIFunctionFactory.Create(GetWeather, options: new() { Name = "get_weather", SerializerOptions = _jsonSerializerOptions })
            ]);

        return new SharedStateAgent(chatClientAgent, _jsonSerializerOptions);
    }

    // =================
    // Tools
    // =================

    [Description("Get the current list of proverbs.")]
    private List<string> GetProverbs()
    {
        _logger.LogInformation("📖 Getting proverbs: {Proverbs}", string.Join(", ", _state.Proverbs));
        return _state.Proverbs;
    }

    [Description("Add new proverbs to the list.")]
    private void AddProverbs([Description("The proverbs to add")] List<string> proverbs)
    {
        _logger.LogInformation("➕ Adding proverbs: {Proverbs}", string.Join(", ", proverbs));
        _state.Proverbs.AddRange(proverbs);
    }

    [Description("Replace the entire list of proverbs.")]
    private void SetProverbs([Description("The new list of proverbs")] List<string> proverbs)
    {
        _logger.LogInformation("📝 Setting proverbs: {Proverbs}", string.Join(", ", proverbs));
        _state.Proverbs = [.. proverbs];
    }

    [Description("Get the weather for a given location. Ensure location is fully spelled out.")]
    private WeatherInfo GetWeather([Description("The location to get the weather for")] string location)
    {
        _logger.LogInformation("🌤️  Getting weather for: {Location}", location);
        return new WeatherInfo
        {
            Temperature = 20,
            Conditions = "sunny",
            Humidity = 50,
            WindSpeed = 10,
            FeelsLike = 25
        };
    }
}

// =================
// Data Models
// =================

public sealed class AgentStateSnapshot
{
    [JsonPropertyName("proverbs")]
    public List<string> Proverbs { get; set; } = [];
}

public sealed class WeatherInfo
{
    [JsonPropertyName("temperature")]
    public int Temperature { get; init; }

    [JsonPropertyName("conditions")]
    public string Conditions { get; init; } = string.Empty;

    [JsonPropertyName("humidity")]
    public int Humidity { get; init; }

    [JsonPropertyName("wind_speed")]
    public int WindSpeed { get; init; }

    [JsonPropertyName("feelsLike")]
    public int FeelsLike { get; init; }
}

// =================
// Serializer Context (required for AOT-friendly AG-UI JSON handling)
// =================
[JsonSerializable(typeof(AgentStateSnapshot))]
[JsonSerializable(typeof(WeatherInfo))]
internal sealed partial class AgentSerializerContext : JsonSerializerContext;
