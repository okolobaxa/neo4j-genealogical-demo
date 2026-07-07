import {
  CopilotChatInput,
  useCopilotChatConfiguration,
  type CopilotChatInputProps,
} from "@copilotkit/react-core/v2";

const BUILT_IN_PROMPTS = [
  "Summarize the current graph",
  "List top 10 persons by mentions",
  "Summarize mentions of Semyon Trofimov Vyskubov",
  "How many people took part in the elections in different roles? List top 10 names. For each person, analyze documents and give me a short overview of their role in the process",
  "Prepare a timeline of Semyon Trofimov Vyskubov case"
];

// CopilotChat mounts a separate `input` slot instance for its welcome screen
// vs. its regular chat view, swapping between them right after the first
// message is sent. A `useState` flag here would reset on that remount, so
// "which threads have had a message sent" lives at module scope instead,
// keyed by threadId so starting a new chat (a fresh threadId) shows the
// chips again.
const threadsWithMessages = new Set<string>();

// Custom `input` slot for CopilotChat: renders prompt chips above the default
// textarea. Clicking a chip fills the shared `value` state via `onChange`
// instead of calling `onSubmitMessage`, so it copies into the box rather than sending.
// Chips disappear for good once the user submits the first message in a thread.
export function ChatInputWithPrompts(props: CopilotChatInputProps) {
  const { value, onChange, onSubmitMessage } = props;
  const config = useCopilotChatConfiguration();
  const threadId = config?.threadId;
  const hasSentFirstMessage = threadId ? threadsWithMessages.has(threadId) : false;

  const handleSubmitMessage = (text: string) => {
    if (threadId) threadsWithMessages.add(threadId);
    onSubmitMessage?.(text);
  };

  return (
    <div>
      {!hasSentFirstMessage && !value && (
        <div className="prompt-chips">
          {BUILT_IN_PROMPTS.map((prompt) => (
            <button
              key={prompt}
              type="button"
              className="prompt-chip"
              onClick={() => onChange?.(prompt)}
            >
              {prompt}
            </button>
          ))}
        </div>
      )}
      <CopilotChatInput {...props} onSubmitMessage={handleSubmitMessage} />
    </div>
  );
}
