using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Samples.SampleUtilities;

/// <summary>
/// Chat history persistence must use the same JSON contracts as the agent stack.
/// <see cref="Microsoft.Extensions.AI.ChatMessage"/> contents are polymorphic (<c>$type</c> discriminators);
/// <c>AgentJsonUtilities.DefaultOptions</c> in Microsoft.Agents.AI carries the source-generated resolver,
/// but that type is internal — this helper exposes the singleton via reflection.
/// </summary>
public static class AgentChatMessageJson
{
    public static JsonSerializerOptions DefaultOptions { get; } = LoadDefaultOptions();

    private static JsonSerializerOptions LoadDefaultOptions()
    {
        const string utilitiesTypeName = "Microsoft.Agents.AI.AgentJsonUtilities";
        Type? utilities = typeof(ChatClientAgent).Assembly.GetType(utilitiesTypeName);
        if (utilities is null)
        {
            throw new InvalidOperationException(
                $"{utilitiesTypeName} was not found in assembly {typeof(ChatClientAgent).Assembly.FullName}.");
        }

        PropertyInfo? prop = utilities.GetProperty(
            "DefaultOptions",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (prop is null)
        {
            throw new InvalidOperationException($"{utilitiesTypeName}.DefaultOptions was not found.");
        }

        if (prop.GetValue(null) is not JsonSerializerOptions options)
        {
            throw new InvalidOperationException($"{utilitiesTypeName}.DefaultOptions returned null.");
        }

        return options;
    }
}
