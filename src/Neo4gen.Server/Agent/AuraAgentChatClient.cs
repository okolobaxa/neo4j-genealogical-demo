using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Neo4gen.Server.Agent;

/// <summary>
/// Adapts <see cref="AuraAgentClient"/> to <see cref="IChatClient"/> so it can be hosted by a
/// <c>ChatClientAgent</c> and exposed over AG-UI. Each turn it forwards the latest user question
/// (received on the backend from CopilotKit via the AG-UI endpoint) to the Aura Agent and returns
/// the agent's answer as a sequence of separate messages — reasoning as an assistant message
/// carrying <see cref="TextReasoningContent"/>, the tool call as an assistant message carrying
/// <see cref="FunctionCallContent"/>, its result as a tool message carrying
/// <see cref="FunctionResultContent"/>, and the final answer as an assistant message carrying
/// <see cref="TextContent"/> — each with its own <see cref="ChatMessage.MessageId"/>, so the AG-UI
/// layer emits distinct THINKING_*/TOOL_CALL_*/TEXT_MESSAGE_* events instead of collapsing
/// everything into one message (which made CopilotKit merge unrelated messages that shared an ID).
/// The Aura Agent owns its own instructions/tools, so system messages and tool options from the
/// framework are ignored.
/// </summary>
public sealed class AuraAgentChatClient : IChatClient
{
    // Name of the synthesized tool call the frontend renders as a "Visualise" widget.
    // Must match the useRenderTool({ name }) registration in the React app.
    private const string VisualiseToolName = "visualise_cypher";

    // Captures the query inside a ```cypher fenced code block in the agent's answer.
    private static readonly Regex CypherFence = new(
        "```cypher\\s*\\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AuraAgentClient _client;

    public AuraAgentChatClient(AuraAgentClient client) => _client = client;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var question = GetLatestUserText(messages);
        var blocks = await _client.AskAsync(question, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(ToChatMessages(blocks));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var question = GetLatestUserText(messages);
        var blocks = await _client.AskAsync(question, cancellationToken).ConfigureAwait(false);
        foreach (var message in ToChatMessages(blocks))
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents) { MessageId = message.MessageId };
        }
    }

    private static List<ChatMessage> ToChatMessages(IReadOnlyList<AuraContentBlock> blocks)
    {
        var messages = blocks.Select(ToChatMessage).ToList();
        if (messages.Count == 0)
        {
            messages.Add(ToChatMessage(new AuraTextBlock(string.Empty)));
        }

        // If the answer contains a ```cypher block, append a tool call the frontend renders as a
        // "Visualise" widget (generative UI). Emitted as a distinct assistant message so AG-UI
        // sends TOOL_CALL_* events for it, just like the Aura Agent's own tool calls.
        if (TryExtractCypher(blocks, out var cypher))
        {
            var call = new FunctionCallContent(
                Guid.NewGuid().ToString("N"),
                VisualiseToolName,
                new Dictionary<string, object?> { ["cypher"] = cypher });
            messages.Add(new ChatMessage(ChatRole.Assistant, [call]) { MessageId = Guid.NewGuid().ToString("N") });
        }

        return messages;
    }

    // Pulls the first ```cypher block out of the agent's text answer(s), if any.
    private static bool TryExtractCypher(IReadOnlyList<AuraContentBlock> blocks, out string cypher)
    {
        foreach (var text in blocks.OfType<AuraTextBlock>())
        {
            var match = CypherFence.Match(text.Text);
            if (match.Success)
            {
                cypher = match.Groups[1].Value.Trim();
                if (cypher.Length > 0)
                {
                    return true;
                }
            }
        }

        cypher = string.Empty;
        return false;
    }

    private static ChatMessage ToChatMessage(AuraContentBlock block)
    {
        (ChatRole role, AIContent content) = block switch
        {
            AuraThinkingBlock thinking => (ChatRole.Assistant, (AIContent)new TextReasoningContent(thinking.Thinking)),
            AuraToolUseBlock toolUse => (ChatRole.Assistant, new FunctionCallContent(toolUse.Id, toolUse.Name, toolUse.Input)),
            AuraToolResultBlock toolResult => (ChatRole.Tool, new FunctionResultContent(toolResult.ToolUseId, toolResult.OutputJson)),
            AuraTextBlock text => (ChatRole.Assistant, new TextContent(text.Text)),
            _ => throw new NotSupportedException($"Unknown Aura content block type: {block.GetType()}"),
        };

        return new ChatMessage(role, [content]) { MessageId = Guid.NewGuid().ToString("N") };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(providerName: "neo4j-aura-agent");
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Nothing to dispose: the HttpClient is owned by IHttpClientFactory.
    }

    private static string GetLatestUserText(IEnumerable<ChatMessage> messages)
    {
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User);
        return lastUserMessage?.Text ?? string.Empty;
    }
}
