import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from "react";
import type { Node, Relationship } from "@neo4j-nvl/base";

// The server includes each node's Neo4j label and properties alongside the NVL-shaped fields.
export interface GraphNode extends Node {
  label: string;
  properties: Record<string, string>;
}

// Response shape from the C# server's graph endpoints (Neo4j via the .NET driver).
interface GraphResponse {
  nodes: GraphNode[];
  relationships: Relationship[];
}

type GraphStatus = "loading" | "ready" | "error";

interface GraphContextValue {
  nodes: GraphNode[];
  rels: Relationship[];
  status: GraphStatus;
  error: string | null;
  /** The Cypher backing the graph currently on screen. */
  cypher: string;
  /** Runs caller-supplied Cypher and replaces the graph with its results. */
  runCypher: (cypher: string) => Promise<void>;
  /** Combines two nodes (by element id) into one via APOC, then refreshes the view. */
  mergeNodes: (idA: string, idB: string) => Promise<void>;
  /** Deletes nodes (by element id) and their relationships, then refreshes the view. */
  deleteNodes: (ids: string[]) => Promise<void>;
  /** Reloads the default full-graph view. */
  reloadDefault: () => void;
}

// Mirrors the fixed query the server's GET /api/graph endpoint runs, so the Cypher
// panel has something meaningful to show for the default full-graph view.
const DEFAULT_CYPHER = `MATCH (n)
WHERE NOT n.name STARTS WITH '__'
MATCH (n)-[r]->(m)
WHERE NOT m.name STARTS WITH '__'
RETURN n, r, m
ORDER BY rand()
LIMIT 500`;

// Fixed palette so colors stay stable across reloads instead of being randomized.
const NODE_COLORS = [
  "#4C8DFF",
  "#F5A623",
  "#7ED321",
  "#D0021B",
  "#9013FE",
  "#50E3C2",
  "#B8860B",
  "#FF6F91",
];

function colorForLabel(label: string): string {
  let hash = 0;
  for (let i = 0; i < label.length; i++) {
    hash = (hash * 31 + label.charCodeAt(i)) | 0;
  }
  return NODE_COLORS[Math.abs(hash) % NODE_COLORS.length];
}

const GraphContext = createContext<GraphContextValue | null>(null);

// Shared graph state: the main GraphCard renders it, and the chat's "Visualise"
// button drives it by running the Cypher an agent answer suggested.
export function GraphProvider({ children }: { children: ReactNode }) {
  const [nodes, setNodes] = useState<GraphNode[]>([]);
  const [rels, setRels] = useState<Relationship[]>([]);
  const [status, setStatus] = useState<GraphStatus>("loading");
  const [error, setError] = useState<string | null>(null);
  const [cypher, setCypher] = useState<string>(DEFAULT_CYPHER);

  const applyData = useCallback((data: GraphResponse) => {
    setNodes(data.nodes.map((node) => ({ ...node, color: colorForLabel(node.label) })));
    setRels(data.relationships);
    setError(null);
    setStatus("ready");
  }, []);

  const reloadDefault = useCallback(() => {
    setStatus("loading");
    setError(null);
    setCypher(DEFAULT_CYPHER);
    fetch("/api/graph?limit=500")
      .then(async (res) => {
        if (!res.ok) throw new Error(`API ${res.status}`);
        return (await res.json()) as GraphResponse;
      })
      .then(applyData)
      .catch(() => setStatus("error"));
  }, [applyData]);

  const runCypher = useCallback(
    async (query: string) => {
      setStatus("loading");
      setError(null);
      setCypher(query);
      try {
        const res = await fetch("/api/graph/query", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ cypher: query }),
        });
        if (!res.ok) {
          // The server returns the Cypher/syntax error message as plain text.
          throw new Error((await res.text()) || `API ${res.status}`);
        }
        applyData((await res.json()) as GraphResponse);
      } catch (err: unknown) {
        setStatus("error");
        setError(err instanceof Error ? err.message : "Failed to run the query.");
      }
    },
    [applyData],
  );

  const mergeNodes = useCallback(
    async (idA: string, idB: string) => {
      const res = await fetch("/api/graph/merge", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ a: idA, b: idB }),
      });
      if (!res.ok) {
        // The server returns the error (e.g. APOC missing) as plain text.
        throw new Error((await res.text()) || `API ${res.status}`);
      }
      // Re-run whatever query is currently on screen so the merged node replaces the two.
      await runCypher(cypher);
    },
    [cypher, runCypher],
  );

  const deleteNodes = useCallback(
    async (ids: string[]) => {
      const res = await fetch("/api/graph/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ids }),
      });
      if (!res.ok) {
        throw new Error((await res.text()) || `API ${res.status}`);
      }
      // Refresh the current view so the removed nodes disappear.
      await runCypher(cypher);
    },
    [cypher, runCypher],
  );

  useEffect(() => reloadDefault(), [reloadDefault]);

  return (
    <GraphContext.Provider
      value={{ nodes, rels, status, error, cypher, runCypher, mergeNodes, deleteNodes, reloadDefault }}
    >
      {children}
    </GraphContext.Provider>
  );
}

export function useGraph(): GraphContextValue {
  const ctx = useContext(GraphContext);
  if (!ctx) throw new Error("useGraph must be used within a GraphProvider");
  return ctx;
}
