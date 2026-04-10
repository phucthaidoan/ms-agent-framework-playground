using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.StructuredOutput;

public static class StructuredOutputOpenAI
{
    private const string CityPrompt = "Return JSON with exactly one field named 'name' containing only the capital city name for France.";

    public static async Task RunSample()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);
        ChatClient chatClient = client.GetChatClient("gpt-4.1-nano");

        await UseStructuredOutputWithResponseFormatAsync(chatClient);
        await UseStructuredOutputWithRunAsync(chatClient);
        await UseStructuredOutputWithRunStreamingAsync(chatClient);
        await UseStructuredOutputWithMiddlewareAsync(chatClient);
    }

    private static async Task UseStructuredOutputWithResponseFormatAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with ResponseFormat");

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HelpfulAssistant",
            ChatOptions = new()
            {
                Instructions = "You are a helpful assistant.",
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<CityInfo>()
            }
        });

        AgentResponse response = await agent.RunAsync(CityPrompt);
        Console.WriteLine("Assistant Output (JSON):");
        Console.WriteLine(response.Text);
        Console.WriteLine();

        CityInfo cityInfo = JsonSerializer.Deserialize<CityInfo>(response.Text)!;
        Console.WriteLine("Assistant Output (Deserialized):");
        Console.WriteLine($"Name: {cityInfo.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithRunAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with RunAsync<T>");

        AIAgent agent = chatClient.AsAIAgent(name: "HelpfulAssistant", instructions: "You are a helpful assistant.");
        AgentResponse<CityInfo> response = await agent.RunAsync<CityInfo>(CityPrompt);

        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {response.Result.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithRunStreamingAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with RunStreamingAsync");

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HelpfulAssistant",
            ChatOptions = new()
            {
                Instructions = "You are a helpful assistant.",
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<CityInfo>()
            }
        });

        IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(CityPrompt);
        AgentResponse nonGenericResponse = await updates.ToAgentResponseAsync();
        CityInfo cityInfo = JsonSerializer.Deserialize<CityInfo>(nonGenericResponse.Text)!;

        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {cityInfo.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithMiddlewareAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with UseStructuredOutput Middleware");

        IChatClient meaiChatClient = chatClient.AsIChatClient();
        AIAgent agent = meaiChatClient.AsAIAgent(name: "HelpfulAssistant", instructions: "You are a helpful assistant.");

        agent = agent
            .AsBuilder()
            .UseStructuredOutput(meaiChatClient)
            .Use(ResponseFormatRemovalMiddleware, null)
            .Build();

        AgentResponse<CityInfo> response = await agent.RunAsync<CityInfo>(CityPrompt);
        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {response.Result.Name}");
        Output.Separator();
    }

    private static Task<AgentResponse> ResponseFormatRemovalMiddleware(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent innerAgent, CancellationToken cancellationToken)
    {
        options = options?.Clone();
        if (options is not null)
        {
            options.ResponseFormat = null;
        }
        return innerAgent.RunAsync(messages, session, options, cancellationToken);
    }
}
