import { useMemo, useRef, useState } from "react";
import { InteractiveNvlWrapper } from "@neo4j-nvl/react";
import type NVL from "@neo4j-nvl/base";
import { useGraph, type GraphNode } from "../lib/graphContext";

export function GraphCard() {
  const nvlRef = useRef<NVL | null>(null);
  const { nodes, rels, status, error, cypher, mergeNodes, deleteNodes } = useGraph();
  const [selectedNode, setSelectedNode] = useState<GraphNode | null>(null);

  // Ctrl/⌘-click builds a selection of up to two nodes to merge or remove.
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [merging, setMerging] = useState(false);
  const [removing, setRemoving] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const busy = merging || removing;

  const toggleSelect = (id: string) => {
    setActionError(null);
    setSelectedIds((prev) => {
      if (prev.includes(id)) return prev.filter((x) => x !== id);
      // Cap at two: once two are picked, a third slides the oldest one out.
      if (prev.length >= 2) return [prev[1], id];
      return [...prev, id];
    });
  };

  const clearSelection = () => {
    setSelectedIds([]);
    setActionError(null);
  };

  const handleMerge = async () => {
    if (selectedIds.length !== 2) return;
    setMerging(true);
    setActionError(null);
    try {
      await mergeNodes(selectedIds[0], selectedIds[1]);
      setSelectedIds([]);
    } catch (err: unknown) {
      setActionError(err instanceof Error ? err.message : "Failed to merge the nodes.");
    } finally {
      setMerging(false);
    }
  };

  const handleRemove = async () => {
    if (selectedIds.length === 0) return;
    const count = selectedIds.length;
    const ok = window.confirm(
      `Delete ${count} node${count > 1 ? "s" : ""} and ${count > 1 ? "their" : "its"} relationships? This can't be undone.`,
    );
    if (!ok) return;
    setRemoving(true);
    setActionError(null);
    try {
      await deleteNodes(selectedIds);
      setSelectedIds([]);
    } catch (err: unknown) {
      setActionError(err instanceof Error ? err.message : "Failed to remove the node(s).");
    } finally {
      setRemoving(false);
    }
  };

  // Reflect the ctrl-selection into NVL so the chosen nodes render highlighted.
  const displayNodes = useMemo(
    () => nodes.map((n) => ({ ...n, selected: selectedIds.includes(n.id) })),
    [nodes, selectedIds],
  );

  // Zoom to fit the whole graph once the force layout settles — otherwise the
  // camera stays centered on a single node and the rest sit off-screen.
  const fitToGraph = () => {
    const nvl = nvlRef.current;
    if (!nvl) return;
    const ids = nvl.getNodes().map((n) => n.id);
    if (ids.length > 0) nvl.fit(ids);
  };

  const zoomBy = (factor: number) => {
    const nvl = nvlRef.current;
    if (!nvl) return;
    nvl.setZoom(nvl.getScale() * factor);
  };

  return (
    <div className="graph-card">
      <div className="graph-canvas">
      <InteractiveNvlWrapper
        ref={nvlRef}
        nodes={displayNodes}
        rels={rels}
        layout="d3Force"
        style={{ width: "100%", height: "100%" }}
        mouseEventCallbacks={{
          onZoom: true,
          onPan: true,
          onDrag: true,
          // Ctrl (or ⌘ on macOS) + click toggles the node in the merge selection;
          // a plain click opens its properties.
          onNodeClick: (node, _hitTargets, evt) => {
            if (evt?.ctrlKey || evt?.metaKey) {
              toggleSelect(node.id);
            } else {
              setSelectedNode(node as GraphNode);
            }
          },
          onCanvasDoubleClick: () => zoomBy(1.5),
          onNodeDoubleClick: (node) => nvlRef.current?.fit([node.id]),
        }}
        nvlCallbacks={{ onLayoutDone: fitToGraph }}
      />
      {selectedIds.length > 0 && (
        <div className="merge-bar">
          <span className="merge-bar-count">
            {selectedIds.length === 1
              ? "1 node selected — Ctrl-click one more to merge"
              : "2 nodes selected"}
          </span>
          <button
            type="button"
            className="merge-btn"
            disabled={selectedIds.length !== 2 || busy}
            onClick={handleMerge}
          >
            {merging ? "Merging…" : "Merge selected"}
          </button>
          <button
            type="button"
            className="remove-btn"
            disabled={busy}
            onClick={handleRemove}
          >
            {removing ? "Removing…" : selectedIds.length === 1 ? "Remove" : "Remove both"}
          </button>
          <button
            type="button"
            className="merge-clear-btn"
            onClick={clearSelection}
            disabled={busy}
          >
            Clear
          </button>
          {actionError && <span className="merge-bar-error">{actionError}</span>}
        </div>
      )}
      <div className="graph-controls">
        <button
          type="button"
          className="graph-control-btn"
          aria-label="Zoom in"
          onClick={() => zoomBy(1.2)}
        >
          +
        </button>
        <button
          type="button"
          className="graph-control-btn"
          aria-label="Zoom out"
          onClick={() => zoomBy(1 / 1.2)}
        >
          −
        </button>
        <button
          type="button"
          className="graph-control-btn"
          aria-label="Reset view"
          onClick={fitToGraph}
        >
          ⤾
        </button>
      </div>
      {status === "loading" && (
        <p className="card-empty card-empty-overlay">Loading graph…</p>
      )}
      {status === "error" && (
        <p className="card-empty card-empty-overlay">
          {error ?? "Couldn't load graph data from /api/graph."}
        </p>
      )}
      {status === "ready" && nodes.length === 0 && (
        <p className="card-empty card-empty-overlay">No nodes returned.</p>
      )}
      {selectedNode && (
        <NodePropertiesModal node={selectedNode} onClose={() => setSelectedNode(null)} />
      )}
      </div>
      <CypherPanel cypher={cypher} />
    </div>
  );
}

// Copies text to the clipboard, falling back to a hidden textarea + execCommand
// where the async Clipboard API is unavailable or blocked. Returns whether it worked.
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Fall through to the legacy path below.
  }

  try {
    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.style.position = "fixed";
    textarea.style.opacity = "0";
    document.body.appendChild(textarea);
    textarea.select();
    const ok = document.execCommand("copy");
    document.body.removeChild(textarea);
    return ok;
  } catch {
    return false;
  }
}

// Shows the Cypher backing the current graph. Collapsed by default; a small button
// unfolds the query, and a Copy button is available once expanded.
function CypherPanel({ cypher }: { cypher: string }) {
  const [copied, setCopied] = useState(false);
  const [expanded, setExpanded] = useState(false);

  const handleCopy = async () => {
    if (await copyText(cypher)) {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    }
  };

  return (
    <div className="cypher-panel">
      <div className="cypher-panel-head">
        <button
          type="button"
          className="cypher-toggle-btn"
          aria-expanded={expanded}
          aria-label={expanded ? "Collapse Cypher" : "Expand Cypher"}
          onClick={() => setExpanded((v) => !v)}
        >
          {expanded ? "▾" : "▸"}
        </button>
        <span className="cypher-panel-title">Cypher</span>
        {expanded && (
          <button type="button" className="cypher-copy-btn" onClick={handleCopy}>
            {copied ? "Copied!" : "Copy"}
          </button>
        )}
      </div>
      {expanded && <pre className="cypher-code">{cypher}</pre>}
    </div>
  );
}

function NodePropertiesModal({ node, onClose }: { node: GraphNode; onClose: () => void }) {
  const entries = Object.entries(node.properties ?? {});

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-card" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3 className="modal-title">{node.caption}</h3>
          <button className="modal-close" onClick={onClose} aria-label="Close">
            ×
          </button>
        </div>
        <p className="modal-subtitle">{node.label}</p>
        {entries.length === 0 ? (
          <p className="card-empty">No properties.</p>
        ) : (
          <dl className="modal-props">
            {entries.map(([key, value]) => (
              <div className="modal-prop-row" key={key}>
                <dt>{key}</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>
    </div>
  );
}
