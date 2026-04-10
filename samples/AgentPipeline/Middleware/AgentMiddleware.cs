// This sample shows multiple middleware layers working together with a ChatClientAgent:
// agent run (PII filtering and guardrails),
// function invocation (logging and result overrides), and human-in-the-loop
// approval workflows for sensitive function calls.

using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.AgentPipeline.Middleware;

public static partial class AgentMiddleware
{
    [Description("Get the weather for a given location.")]
    private static string GetWeather([Description("The location to get the weather for.")] string location)
        => $"The weather in {location} is cloudy with a high of 15°C.";

    [Description("The current datetime offset.")]
    private static string GetDateTime()
        => DateTimeOffset.Now.ToString();

    public static async Task RunSample()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        AITool dateTimeTool = AIFunctionFactory.Create(GetDateTime, name: nameof(GetDateTime));
        AITool getWeatherTool = AIFunctionFactory.Create(GetWeather, name: nameof(GetWeather));

        AIAgent originalAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are an AI assistant that helps people find information.",
                name: "InformationAssistant",
                tools: [getWeatherTool, dateTimeTool]);

        // Adding middleware to the agent level
        AIAgent middlewareEnabledAgent = originalAgent
            .AsBuilder()
            .Use(FunctionCallMiddleware)
            .Use(FunctionCallOverrideWeather)
            .Use(PIIMiddleware, null)
            .Use(GuardrailMiddleware, null)
            .Build();

        AgentSession session = await middlewareEnabledAgent.CreateSessionAsync();

        Output.Title("Example 0: Invocation middleware");
        Output.Gray("User: What's the current time and the weather in Da Nang city?.");
        AgentResponse invocationMiddlewareResponse = await middlewareEnabledAgent.RunAsync("What's the current time and the weather in Da Nang city?");
        Output.Green($"Agent: {invocationMiddlewareResponse}");
        Output.Separator();

        Output.Title("Example 1: Wording Guardrail");
        Output.Gray("User: Tell me something harmful.");
        AgentResponse guardRailedResponse = await middlewareEnabledAgent.RunAsync("Tell me something harmful.");
        Output.Green($"Agent: {guardRailedResponse}");
        Output.Separator();

        Output.Title("Example 2: PII detection");
        Output.Gray("User: My name is John Doe, call me at 123-456-7890 or email me at john@something.com");
        AgentResponse piiResponse = await middlewareEnabledAgent.RunAsync("My name is John Doe, call me at 123-456-7890 or email me at john@something.com");
        Output.Green($"Agent: {piiResponse}");
        Output.Separator();

        Output.Title("Example 3: Agent function middleware");
        Output.Gray("User: What's the current time and the weather in Seattle?");
        AgentResponse functionCallResponse = await middlewareEnabledAgent.RunAsync("What's the current time and the weather in Seattle?", session);
        Output.Green($"Agent: {functionCallResponse}");
        Output.Separator();

        // Special per-request middleware agent.
        Output.Title("Example 4: Middleware with human in the loop function approval");

#pragma warning disable MEAI001
        AIAgent humanInTheLoopAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a Human in the loop testing AI assistant that helps people find information.",
                name: "HumanInTheLoopAgent",
                tools: [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather, name: nameof(GetWeather)))]);

        Output.Gray("User: What's the current time and the weather in Seattle?");
        AgentResponse response = await humanInTheLoopAgent
            .AsBuilder()
            .Use(ConsolePromptingApprovalMiddleware, null)
            .Build()
            .RunAsync("What's the current time and the weather in Seattle?");
#pragma warning restore MEAI001

        Output.Green($"Agent: {response}");
        Output.Separator();
    }

    // Function invocation middleware that logs before and after function calls.
    private static async ValueTask<object?> FunctionCallMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        Output.Yellow($"Function Name: {context.Function.Name} - Middleware 1 Pre-Invoke");
        var result = await next(context, cancellationToken);
        Output.Yellow($"Function Name: {context.Function.Name} - Middleware 1 Post-Invoke");
        return result;
    }

    // Function invocation middleware that overrides the result of the GetWeather function.
    private static async ValueTask<object?> FunctionCallOverrideWeather(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        Output.Yellow($"Function Name: {context.Function.Name} - Middleware 2 Pre-Invoke");

        var result = await next(context, cancellationToken);

        if (context.Function.Name == nameof(GetWeather))
        {
            result = "The weather is sunny with a high of 25°C.";
        }

        Output.Yellow($"Function Name: {context.Function.Name} - Middleware 2 Post-Invoke");
        return result;
    }

    // This middleware redacts PII information from input and output messages.
    private static async Task<AgentResponse> PIIMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var filteredMessages = FilterMessages(messages);
        Output.Gray("Pii Middleware - Filtered Messages Pre-Run");

        var agentResponse = await innerAgent.RunAsync(filteredMessages, session, options, cancellationToken).ConfigureAwait(false);

        agentResponse.Messages = FilterMessages(agentResponse.Messages);

        Output.Gray("Pii Middleware - Filtered Messages Post-Run");

        return agentResponse;

        static IList<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages)
        {
            return messages.Select(m => new ChatMessage(m.Role, FilterPii(m.Text))).ToList();
        }

        static string FilterPii(string? content)
        {
            if (content is null) return string.Empty;

            Regex[] piiPatterns = [
                PhoneRegex(),
                EmailRegex(),
                FullNameRegex()
            ];

            foreach (var pattern in piiPatterns)
            {
                content = pattern.Replace(content, "[REDACTED: PII]");
            }

            return content;
        }
    }

    // This middleware enforces guardrails by redacting certain keywords from input and output messages.
    private static async Task<AgentResponse> GuardrailMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var filteredMessages = FilterMessages(messages);

        Output.Gray("Guardrail Middleware - Filtered messages Pre-Run");

        var agentResponse = await innerAgent.RunAsync(filteredMessages, session, options, cancellationToken);

        agentResponse.Messages = FilterMessages(agentResponse.Messages);

        Output.Gray("Guardrail Middleware - Filtered messages Post-Run");

        return agentResponse;

        static List<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages)
        {
            return messages.Select(m => new ChatMessage(m.Role, FilterContent(m.Text))).ToList();
        }

        static string FilterContent(string? content)
        {
            if (content is null) return string.Empty;

            foreach (var keyword in new[] { "harmful", "illegal", "violence" })
            {
                if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return "[REDACTED: Forbidden content]";
                }
            }

            return content;
        }
    }

    // This middleware handles human-in-the-loop console interaction for any user approval required during function calling.
#pragma warning disable MEAI001
    private static async Task<AgentResponse> ConsolePromptingApprovalMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        AgentResponse agentResponse = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        List<FunctionApprovalRequestContent> approvalRequests = agentResponse.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionApprovalRequestContent>()
            .ToList();

        while (approvalRequests.Count > 0)
        {
            agentResponse.Messages = approvalRequests
                .ConvertAll(approvalRequest =>
                {
                    Console.WriteLine($"The agent would like to invoke the following function, please reply Y to approve: Name {approvalRequest.FunctionCall.Name}");
                    bool approved = Console.ReadLine()?.Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
                    return new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(approved)]);
                });

            agentResponse = await innerAgent.RunAsync(agentResponse.Messages, session, options, cancellationToken);

            approvalRequests = agentResponse.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionApprovalRequestContent>()
                .ToList();
        }

        return agentResponse;
    }
#pragma warning restore MEAI001

    [GeneratedRegex(@"\b\d{3}-\d{3}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[\w\.-]+@[\w\.-]+\.\w+\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b[A-Z][a-z]+\s[A-Z][a-z]+\b", RegexOptions.Compiled)]
    private static partial Regex FullNameRegex();
}
