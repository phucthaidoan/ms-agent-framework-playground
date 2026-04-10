using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.StructuredOutput;

internal sealed class StructuredOutputAgent : DelegatingAIAgent
{
    private readonly IChatClient _chatClient;
    private readonly StructuredOutputAgentOptions? _agentOptions;

    public StructuredOutputAgent(AIAgent innerAgent, IChatClient chatClient, StructuredOutputAgentOptions? options = null)
        : base(innerAgent)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _agentOptions = options;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentResponse textResponse = await InnerAgent.RunAsync(messages, session, options, cancellationToken);
        ChatResponse soResponse = await _chatClient.GetResponseAsync(
            messages: GetChatMessages(textResponse.Text),
            options: GetChatOptions(options),
            cancellationToken: cancellationToken);

        return new StructuredOutputAgentResponse(soResponse, textResponse);
    }

    private List<ChatMessage> GetChatMessages(string? textResponseText)
    {
        List<ChatMessage> chatMessages = [];

        if (_agentOptions?.ChatClientSystemMessage is not null)
        {
            chatMessages.Add(new ChatMessage(ChatRole.System, _agentOptions.ChatClientSystemMessage));
        }

        chatMessages.Add(new ChatMessage(ChatRole.User, textResponseText));
        return chatMessages;
    }

    private ChatOptions GetChatOptions(AgentRunOptions? options)
    {
        ChatResponseFormat responseFormat = options?.ResponseFormat
            ?? _agentOptions?.ChatOptions?.ResponseFormat
            ?? throw new InvalidOperationException($"A response format of type '{nameof(ChatResponseFormatJson)}' must be specified, but none was specified.");

        if (responseFormat is not ChatResponseFormatJson jsonResponseFormat)
        {
            throw new NotSupportedException($"A response format of type '{nameof(ChatResponseFormatJson)}' must be specified, but was '{responseFormat.GetType().Name}'.");
        }

        ChatOptions chatOptions = _agentOptions?.ChatOptions?.Clone() ?? new ChatOptions();
        chatOptions.ResponseFormat = jsonResponseFormat;
        return chatOptions;
    }
}
