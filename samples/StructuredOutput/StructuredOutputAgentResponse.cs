using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Samples.StructuredOutput;

internal sealed class StructuredOutputAgentResponse : AgentResponse
{
    public StructuredOutputAgentResponse(ChatResponse chatResponse, AgentResponse agentResponse)
        : base(chatResponse)
    {
        OriginalResponse = agentResponse;
    }

    public AgentResponse OriginalResponse { get; }
}
