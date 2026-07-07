var builder = DistributedApplication.CreateBuilder(args);

// Docker Compose publisher: `aspire publish -o <dir>` emits a docker-compose.yaml plus
// auto-generated Dockerfiles for each service. We then adapt those to two Fly.io apps.
builder.AddDockerComposeEnvironment("fly");

// Deterministic Fly.io public hostnames (the apps are created ahead of deploy, so these are
// known up front). Used only in publish mode to break the frontend<->runtime URL chicken-egg:
//   - the frontend bakes the runtime URL in at build time (Vite compiles it in),
//   - the runtime needs the server's /agui URL at runtime.
const string ServerPublicUrl = "https://neo4gen.fly.dev";
const string RuntimePublicUrl = "https://neo4gen-copilot.fly.dev";

// C# backend: hosts the app API, the AG-UI agent (/agui) and serves the SPA in production.
var server = builder.AddProject<Projects.Neo4gen_Server>("server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Thin CopilotKit runtime (Bun/Hono): the browser's "front door". Speaks the CopilotKit
// protocol to the frontend and forwards to the C# AG-UI endpoint. Holds no business logic.
var copilotRuntime = builder.AddBunApp("copilotruntime", "../copilot-runtime", "server.ts")
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .WithEnvironment("COPILOTKIT_TELEMETRY_DISABLED", "true")
    .WaitFor(server);

// Frontend (Vite + CopilotKit). Point the CopilotKit provider at the runtime's external URL.
var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithBun()
    .WithReference(server)
    .WaitFor(server)
    .WaitFor(copilotRuntime);

if (builder.ExecutionContext.IsPublishMode)
{
    // Publish (Fly): use the fixed public Fly hostnames.
    copilotRuntime.WithEnvironment("AGENT_URL", $"{ServerPublicUrl}/agui");
    webfrontend.WithEnvironment("VITE_COPILOTKIT_RUNTIME_URL", $"{RuntimePublicUrl}/api/copilotkit");
}
else
{
    // Local run (Aspire orchestration): resolve endpoints dynamically.
    copilotRuntime.WithEnvironment(context =>
    {
        context.EnvironmentVariables["AGENT_URL"] =
            ReferenceExpression.Create($"{server.GetEndpoint("http")}/agui");
    });
    webfrontend.WithEnvironment(context =>
    {
        context.EnvironmentVariables["VITE_COPILOTKIT_RUNTIME_URL"] =
            ReferenceExpression.Create($"{copilotRuntime.GetEndpoint("http")}/api/copilotkit");
    });
}

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();
