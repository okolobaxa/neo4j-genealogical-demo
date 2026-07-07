import { useState } from "react";

export interface MoonCardProps {
  themeColor: string;
  status: "inProgress" | "executing" | "complete";
  respond?: (response: string) => void;
}

// Human-in-the-loop: the agent asks to "go to the moon" and waits for the user's
// approval before continuing. respond() sends the decision back to the agent.
export function MoonCard({ themeColor, status, respond }: MoonCardProps) {
  const [decision, setDecision] = useState<"launched" | "aborted" | null>(null);

  const handleLaunch = () => {
    setDecision("launched");
    respond?.("You have permission to go to the moon.");
  };

  const handleAbort = () => {
    setDecision("aborted");
    respond?.(
      "You do not have permission to go to the moon. The user you're talking to rejected the request.",
    );
  };

  return (
    <div className="gen-card" style={{ backgroundColor: themeColor }}>
      <div className="gen-card-inner" style={{ textAlign: "center" }}>
        {decision === "launched" ? (
          <>
            <div className="moon-emoji">🌕</div>
            <h2 className="gen-card-title">Mission Launched</h2>
            <p className="gen-card-muted">We made it to the moon!</p>
          </>
        ) : decision === "aborted" ? (
          <>
            <div className="moon-emoji">✋</div>
            <h2 className="gen-card-title">Mission Aborted</h2>
            <p className="gen-card-muted">Staying on Earth 🌍</p>
          </>
        ) : (
          <>
            <div className="moon-emoji">🚀</div>
            <h2 className="gen-card-title">Ready for Launch?</h2>
            <p className="gen-card-muted">Mission to the Moon 🌕</p>
            {status === "executing" && (
              <div className="moon-buttons">
                <button className="btn btn-primary" onClick={handleLaunch}>
                  🚀 Launch!
                </button>
                <button className="btn btn-secondary" onClick={handleAbort}>
                  ✋ Abort
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
