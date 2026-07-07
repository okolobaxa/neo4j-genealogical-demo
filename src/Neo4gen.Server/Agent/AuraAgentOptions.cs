namespace Neo4gen.Server.Agent;

/// <summary>
/// Configuration for talking to a Neo4j Aura Agent over its REST API.
/// See https://neo4j.com/docs/aura/aura-agent/ (agent access via API + Aura API authentication).
///
/// Non-secret values (<see cref="EndpointUrl"/>, <see cref="ClientId"/>) live in appsettings;
/// the <see cref="ClientSecret"/> is kept out of source control in user-secrets (from the
/// Neo4gen.Server directory):
///   dotnet user-secrets set "AuraAgent:ClientSecret" "&lt;your-aura-api-client-secret&gt;"
/// </summary>
public sealed class AuraAgentOptions
{
    public const string SectionName = "AuraAgent";

    /// <summary>
    /// The agent's invocation endpoint, copied from the agent's menu in the Aura console once it
    /// has been configured for external (API) access. Requests are POSTed here.
    /// </summary>
    public string? EndpointUrl { get; set; }

    /// <summary>Client ID from an Aura API key.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret from an Aura API key. Store this in user-secrets, not appsettings.</summary>
    public string? ClientSecret { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(EndpointUrl)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
