using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Samples.StructuredOutput;

internal static class AIAgentBuilderExtensions
{
    public static AIAgentBuilder UseStructuredOutput(
        this AIAgentBuilder builder,
        IChatClient? chatClient = null,
        Func<StructuredOutputAgentOptions>? optionsFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Use((innerAgent, services) =>
        {
            chatClient ??= services?.GetService<IChatClient>()
                ?? throw new InvalidOperationException($"No {nameof(IChatClient)} was provided and none could be resolved from the service provider.");

            return new StructuredOutputAgent(innerAgent, chatClient, optionsFactory?.Invoke());
        });
    }
}
