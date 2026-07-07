using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Neo4gen.Server.Agent;
using Neo4gen.Server.Cypher;
using Neo4gen.Server.Graph;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- AG-UI (Microsoft Agent Framework) ---
builder.Services.AddAGUI();

// --- Neo4j Aura Agent (REST API + OAuth client-credentials) ---
builder.Services.AddOptions<AuraAgentOptions>()
    .Bind(builder.Configuration.GetSection(AuraAgentOptions.SectionName));

// Dedicated HttpClient: allow up to the agent's documented 60s (plus headroom) and drop the
// Aspire default resilience handler, whose short per-attempt/total timeouts would abort a slow
// agent turn. Registered by name and consumed via IHttpClientFactory from the singleton client.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental but stable enough here.
builder.Services.AddHttpClient(AuraAgentClient.HttpClientName, client =>
        client.Timeout = TimeSpan.FromSeconds(120))
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

builder.Services.AddSingleton<AuraAgentClient>();

// --- Neo4j graph data (official .NET driver) ---
builder.Services.AddNeo4j(builder.Configuration);

// --- Cypher rewriting for the "Visualise" button (OpenAI structured output) ---
builder.Services.AddSingleton<CypherRewriter>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}


string[] messages =
[
    "Hello from the backend!",
    "Aspire is orchestrating this app.",
    "Random text, freshly generated.",
    "The server says hi.",
    "Minimal API, maximum vibes.",
    "Another day, another random message.",
    "This message was chosen at random.",
    "Beep boop, here's your text."
];

var api = app.MapGroup("/api");
api.MapGet("message", () => new MessageResponse(messages[Random.Shared.Next(messages.Length)]))
.WithName("GetRandomMessage");
api.MapGraphEndpoints();
api.MapCypherEndpoints();

app.MapDefaultEndpoints();

// --- AG-UI agent endpoint ---
// The thin CopilotKit runtime (Node/bun service) forwards browser traffic here.
// Mounted under /agui so it does not collide with the SPA served at "/".
// Each user question is relayed to the Neo4j Aura Agent and its answer is streamed back as
// plain text to the chat.
var auraAgentClient = app.Services.GetRequiredService<AuraAgentClient>();
var auraAgent = new ChatClientAgent(
    new AuraAgentChatClient(auraAgentClient),
    new ChatClientAgentOptions
    {
        Name = "AuraAgent",
        Description = "Relays user questions to a Neo4j Aura Agent and returns its answers.",
        // The Aura Agent resolves its own tool calls server-side and AuraAgentChatClient reports
        // that trace as already-completed FunctionCallContent/FunctionResultContent pairs. Without
        // this, ChatClientAgent's default automatic function-invocation decorator treats every
        // reported tool call as unresolved and keeps re-invoking the chat client to "finish" it —
        // and since each call just re-asks the Aura Agent the same question, it loops forever.
        UseProvidedChatClientAsIs = true,
    });
app.MapAGUI("/agui", auraAgent);

app.UseFileServer();

app.Run();

record MessageResponse(string Message);
