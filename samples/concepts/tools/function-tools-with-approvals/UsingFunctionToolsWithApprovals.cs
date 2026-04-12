#pragma warning disable MEAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.ComponentModel;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.Tools.UsingFunctionToolsWithApprovals;

public static class UsingFunctionToolsWithApprovals
{
    public static async Task RunSample()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        ChatClientAgent agent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a helpful assistant that can get weather information.",
                tools: [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather))]);

        AgentSession session = await agent.CreateSessionAsync();

        AgentResponse response = await agent.RunAsync("What is the weather like in Amsterdam?", session);

        List<FunctionApprovalRequestContent> approvalRequests = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionApprovalRequestContent>()
            .ToList();

        while (approvalRequests.Count > 0)
        {
            List<ChatMessage> userInputResponses = approvalRequests.ConvertAll(request =>
            {
                Output.Yellow($"Agent wants to call: '{request.FunctionCall.Name}'. Approve? (Y/N)");
                Console.Write("> ");
                bool approved = Console.ReadLine()?.Equals("Y", StringComparison.OrdinalIgnoreCase) ?? false;
                return new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);
            });

            response = await agent.RunAsync(userInputResponses, session);

            approvalRequests = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionApprovalRequestContent>()
                .ToList();
        }

        Console.WriteLine($"\nAgent: {response}");
    }

    [Description("Get the weather for a given location.")]
    private static string GetWeather([Description("The location to get the weather for.")] string location)
        => $"The weather in {location} is cloudy with a high of 15°C.";
}
