using Microsoft.Extensions.AI;

namespace Samples.StructuredOutput;

#pragma warning disable CA1812
internal sealed class StructuredOutputAgentOptions
#pragma warning restore CA1812
{
    public string? ChatClientSystemMessage { get; set; }

    public ChatOptions? ChatOptions { get; set; }
}
