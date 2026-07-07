/**
 * Thin CopilotKit runtime.
 *
 * This is the "front door" the browser (CopilotKit React) talks to. It speaks the CopilotKit
 * protocol on one side and forwards to the C# AG-UI agent on the other. It holds no business
 * logic — all tools/state live in the .NET server. Ported from the CopilotKit <> Microsoft
 * Agent Framework starter's Next.js route, re-hosted as a standalone Hono app served by Bun so
 * Aspire can orchestrate it as its own service (no Next.js required).
 *
 * Config (injected by the Aspire AppHost):
 *   PORT       - port to listen on (allocated by Aspire via WithHttpEndpoint(env: "PORT"))
 *   AGENT_URL  - URL of the C# AG-UI endpoint, e.g. http://localhost:5xxx/agui
 */
import {
  CopilotRuntime,
  createCopilotEndpoint,
  InMemoryAgentRunner,
} from "@copilotkit/runtime/v2";
import { HttpAgent } from "@ag-ui/client";
import { Hono } from "hono";
import { cors } from "hono/cors";

const AGENT_URL = process.env.AGENT_URL ?? "http://localhost:8000/agui";
const PORT = Number(process.env.PORT ?? 4000);
const BASE_PATH = "/api/copilotkit";

const runtime = new CopilotRuntime({
  agents: {
    // Must match the agentId used by the frontend (CopilotChatConfigurationProvider agentId="default").
    default: new HttpAgent({ url: AGENT_URL }),
  },
  runner: new InMemoryAgentRunner(),
});

const copilot = createCopilotEndpoint({
  runtime,
  basePath: BASE_PATH,
});

const app = new Hono();

// The browser calls this runtime from the frontend's (different) origin, so allow CORS.
// Reflect the caller's origin and allow credentials to be safe across dev/container setups.
app.use(
  "*",
  cors({
    origin: (origin) => origin ?? "*",
    allowMethods: ["GET", "POST", "PATCH", "DELETE", "OPTIONS"],
    allowHeaders: ["Content-Type", "Authorization"],
    credentials: true,
  }),
);

app.get("/health", (c) => c.json({ status: "ok", agentUrl: AGENT_URL }));

// Mount the CopilotKit endpoint (it owns everything under BASE_PATH).
app.route("/", copilot);

console.log(`[copilot-runtime] listening on :${PORT}  ->  agent ${AGENT_URL}`);

export default {
  port: PORT,
  fetch: app.fetch,
  // A run streams RUN_STARTED immediately, then the connection sits idle while the C# server
  // waits on the Aura Agent (Neo4j documents up to 60s per invocation; our HttpClient allows
  // 120s). Bun.serve's default idleTimeout is only 10s, so it would kill the streaming response
  // mid-run — the browser sees ERR_INCOMPLETE_CHUNKED_ENCODING and CopilotKit reports
  // agent_run_failed. Raise it to Bun's maximum (255s) to comfortably outlast a slow agent turn.
  idleTimeout: 255,
};
