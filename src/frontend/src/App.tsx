import { useState } from "react";
import {
  useFrontendTool,
  useHumanInTheLoop,
  useRenderTool,
  useCopilotChatConfiguration,
  CopilotChat,
  CopilotChatInput,
  CopilotChatConfigurationProvider,
} from "@copilotkit/react-core/v2";
import { z } from "zod";
import { GraphCard } from "./components/graph";
import { WeatherCard } from "./components/weather";
import { MoonCard } from "./components/moon";
import { ChatInputWithPrompts } from "./PromptChips";
import { VisualiseWidget } from "./VisualiseWidget";
import { GraphProvider, useGraph } from "./lib/graphContext";
import "./App.css";

const AGENT_ID = "default";

const QR_IMAGE_URL = "https://quickchart.io/qr?text=https://neo4gen.fly.dev";

function App() {
  const [themeColor, setThemeColor] = useState("#ffffff");
  const [presenting, setPresenting] = useState(false);

  // 🪁 Frontend action: the agent can change the app theme color.
  useFrontendTool({
    name: "setThemeColor",
    description: "Set the theme color of the application",
    parameters: z.object({
      themeColor: z
        .string()
        .describe("The theme color to set. Make sure to pick nice colors."),
    }),
    handler: async ({ themeColor }) => {
      setThemeColor(themeColor);
      return `Changing background to ${themeColor}`;
    },
  });

  return (
    // One CopilotChatConfigurationProvider owns the active thread for the surface.
    // The main content and the chat both read this same agent/thread.
    <CopilotChatConfigurationProvider agentId={AGENT_ID}>
      {/* GraphProvider shares one graph between the main panel and the chat's
          "Visualise" button, which runs Cypher an agent answer suggested. */}
      <GraphProvider>
        <div className="layout">
          <main className="content" style={{ backgroundColor: themeColor }}>
            <MainContent themeColor={themeColor} />
          </main>
          <aside className="chat-panel">
            <ChatPanel onPresent={() => setPresenting(true)} />
          </aside>
        </div>
        {presenting && (
          <div
            className="presentation-overlay"
            onClick={() => setPresenting(false)}
          >
            <div className="presentation-content">
              <h1 className="presentation-title">Thanks!</h1>
              <img
                className="presentation-qr"
                src={QR_IMAGE_URL}
                alt="QR code linking to https://neo4gen.fly.dev"
              />
            </div>
          </div>
        )}
      </GraphProvider>
    </CopilotChatConfigurationProvider>
  );
}

function ChatPanel({ onPresent }: { onPresent: () => void }) {
  const chatConfig = useCopilotChatConfiguration();
  const { reloadDefault } = useGraph();

  // Starting a fresh chat also resets the graph: back to the default view and Cypher.
  const handleNewChat = () => {
    chatConfig?.startNewThread();
    reloadDefault();
  };

  return (
    <div className="chat-panel-inner">
      <div className="chat-panel-header">
        <a
          className="github-link"
          href="https://github.com/okolobaxa/neo4j-genealogical-demo"
          target="_blank"
          rel="noopener noreferrer"
          title="View on GitHub"
          aria-label="View on GitHub"
        >
          <svg viewBox="0 0 16 16" width="18" height="18" aria-hidden="true">
            <path
              fill="currentColor"
              d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0016 8c0-4.42-3.58-8-8-8z"
            />
          </svg>
        </a>
        <button
          type="button"
          className="present-toggle"
          onClick={onPresent}
          title="Presentation mode"
        >
          ▶ Present
        </button>
        <button
          type="button"
          className="new-chat-button"
          onClick={handleNewChat}
        >
          New chat
        </button>
      </div>
      <CopilotChat
        labels={{ chatInputPlaceholder: "Ask me anything..." }}
        input={ChatInputWithPrompts as unknown as typeof CopilotChatInput}
      />
    </div>
  );
}

function MainContent({ themeColor }: { themeColor: string }) {
  // 🪁 Generative UI: rendered when the agent calls get_weather.
  useFrontendTool(
    {
      name: "get_weather",
      description: "Get the weather for a given location.",
      available: false,
      parameters: z.object({ location: z.string() }),
      render: ({ args }) => (
        <WeatherCard location={args.location} themeColor={themeColor} />
      ),
    },
    [themeColor],
  );

  // 🪁 Human-in-the-loop: agent requests approval to "go to the moon".
  useHumanInTheLoop(
    {
      name: "go_to_moon",
      description: "Go to the moon on request.",
      render: ({ respond, status }) => (
        <MoonCard themeColor={themeColor} status={status} respond={respond} />
      ),
    },
    [themeColor],
  );

  // 🪁 Generative UI: the C# server emits a `visualise_cypher` tool call whenever an
  // answer contains a ```cypher block; render it inline in the chat as a "Visualise"
  // button that runs the query into the graph panel.
  useRenderTool(
    {
      name: "visualise_cypher",
      parameters: z.object({ cypher: z.string() }),
      render: ({ parameters }) =>
        parameters.cypher ? <VisualiseWidget cypher={parameters.cypher} /> : <></>,
    },
    [],
  );

  return <GraphCard />;
}

export default App;
