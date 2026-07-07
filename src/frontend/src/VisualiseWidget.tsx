import { useState } from "react";
import { useGraph } from "./lib/graphContext";

// Asks the server to rewrite an aggregating query into a graph-returning one (OpenAI).
// Falls back to the original query if the request fails, so Visualise always renders.
async function rewriteCypher(cypher: string): Promise<string> {
  try {
    const res = await fetch("/api/cypher/rewrite", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cypher }),
    });
    if (res.ok) {
      const data = (await res.json()) as { query?: string };
      if (data.query) return data.query;
    }
  } catch {
    // Ignore and fall back to the original query.
  }
  return cypher;
}

// Generative-UI widget rendered inline in the chat for a `visualise_cypher` tool
// call (see the useRenderTool registration in App.tsx). On click it first asks the
// server to rewrite the (possibly aggregating) Cypher into a graph-returning query
// via OpenAI, then renders the result in the main graph panel.
export function VisualiseWidget({ cypher }: { cypher: string }) {
  const { runCypher } = useGraph();
  const [pending, setPending] = useState(false);

  const handleClick = async () => {
    setPending(true);
    try {
      const query = await rewriteCypher(cypher);
      await runCypher(query);
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="visualise-row">
      <button
        type="button"
        className="visualise-button"
        disabled={pending}
        onClick={handleClick}
      >
        {pending ? "Visualising…" : "Visualise"}
      </button>
    </div>
  );
}
