using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.StructuredOutput.OpenAIChatClient;

// This sample shows how to configure ChatClientAgent to produce structured output.
// It uses OpenAI with an API key (see SecretManager); the upstream Agent Framework sample uses Azure OpenAI.
public static class StructuredOutputOpenAI
{
    private const string CityPrompt = "Return JSON with exactly one field named 'name' containing only the capital city name for France.";

    public static async Task RunSample()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);
        ChatClient chatClient = client.GetChatClient("gpt-4.1-nano");

        // Demonstrates how to work with structured output via ResponseFormat with the non-generic RunAsync method.
        // This approach is useful when:
        // a. Structured output is used for inter-agent communication, where one agent produces structured output
        //    and passes it as text to another agent as input, without the need for the caller to directly work with the structured output.
        // b. The type of the structured output is not known at compile time, so the generic RunAsync<T> method cannot be used.
        // c. The type of the structured output is represented by JSON schema only, without a corresponding class or type in the code.
        await UseStructuredOutputWithResponseFormatAsync(chatClient);

        // Demonstrates how to work with structured output via the generic RunAsync<T> method.
        // This approach is useful when the caller needs to directly work with the structured output in the code
        // via an instance of the corresponding class or type and the type is known at compile time.
        await UseStructuredOutputWithRunAsync(chatClient);

        // Demonstrates how to work with structured output when streaming using the RunStreamingAsync method.
        // Assemble streamed updates with ToAgentResponseAsync(), then deserialize JSON from the Text property when you need a typed object.
        await UseStructuredOutputWithRunStreamingAsync(chatClient);

        // Demonstrates how to add structured output support to agents that don't natively support it using the structured output middleware.
        // This approach is useful when working with agents that don't support structured output natively, or agents using models
        // that don't have the capability to produce structured output, allowing you to still leverage structured output features by transforming
        // the text output from the agent into structured data using a chat client.
        await UseStructuredOutputWithMiddlewareAsync(chatClient);
    }

    private static async Task UseStructuredOutputWithResponseFormatAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with ResponseFormat");

        // Create the agent with ResponseFormat set to JSON schema for CityInfo.
        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HelpfulAssistant",
            ChatOptions = new()
            {
                Instructions = "You are a helpful assistant.",
                // Specify CityInfo as the type parameter of ForJsonSchema to indicate the expected structured output from the agent.
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<CityInfo>()
            }
        });

        // Invoke the agent with input from which to extract structured information.
        AgentResponse response = await agent.RunAsync(CityPrompt);

        // Access the structured output via the Text property as JSON when JSON as text is required
        // and no object instance is needed (e.g., logging, forwarding, or storage).
        Console.WriteLine("Assistant Output (JSON):");
        Console.WriteLine(response.Text);
        Console.WriteLine();

        // Deserialize the JSON text when you need properties, operations, or a typed instance for other APIs.
        CityInfo cityInfo = JsonSerializer.Deserialize<CityInfo>(response.Text)!;
        Console.WriteLine("Assistant Output (Deserialized):");
        Console.WriteLine($"Name: {cityInfo.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithRunAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with RunAsync<T>");

        // Create the agent (no ResponseFormat on ChatOptions here; RunAsync<T> supplies the expected type).
        AIAgent agent = chatClient.AsAIAgent(name: "HelpfulAssistant", instructions: "You are a helpful assistant.");

        // Set CityInfo as the type parameter of RunAsync to specify the expected structured output and invoke with input.
        AgentResponse<CityInfo> response = await agent.RunAsync<CityInfo>(CityPrompt);

        // Access the structured output via the Result property of the agent response.
        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {response.Result.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithRunStreamingAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with RunStreamingAsync");

        // Create the agent with ResponseFormat set to JSON schema for CityInfo.
        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "HelpfulAssistant",
            ChatOptions = new()
            {
                Instructions = "You are a helpful assistant.",
                // Specify CityInfo as the type parameter of ForJsonSchema to indicate the expected structured output from the agent.
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<CityInfo>()
            }
        });

        // Invoke the agent with streaming to extract structured information.
        IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(CityPrompt);

        // Assemble all parts of the streamed output.
        AgentResponse nonGenericResponse = await updates.ToAgentResponseAsync();

        // Access the structured output by deserializing JSON in the Text property.
        CityInfo cityInfo = JsonSerializer.Deserialize<CityInfo>(nonGenericResponse.Text)!;

        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {cityInfo.Name}");
        Output.Separator();
    }

    private static async Task UseStructuredOutputWithMiddlewareAsync(ChatClient chatClient)
    {
        Output.Title("Structured Output with UseStructuredOutput Middleware");

        // Create chat client that will transform the agent text response into structured output.
        IChatClient meaiChatClient = chatClient.AsIChatClient();

        // Create the agent
        AIAgent agent = meaiChatClient.AsAIAgent(name: "HelpfulAssistant", instructions: "You are a helpful assistant.");

        // Add structured output middleware via UseStructuredOutput to convert text responses into structured data using a chat client.
        // Since this agent could support structured output natively, we add middleware that removes ResponseFormat from AgentRunOptions
        // to emulate an agent that does not support structured output natively.
        agent = agent
            .AsBuilder()
            .UseStructuredOutput(meaiChatClient)
            .Use(ResponseFormatRemovalMiddleware, null)
            .Build();

        // Set CityInfo as the type parameter of RunAsync to specify the expected structured output and invoke with input.
        AgentResponse<CityInfo> response = await agent.RunAsync<CityInfo>(CityPrompt);

        // Access the structured output via the Result property of the agent response.
        Console.WriteLine("Assistant Output:");
        Console.WriteLine($"Name: {response.Result.Name}");
        Output.Separator();
    }

    private static Task<AgentResponse> ResponseFormatRemovalMiddleware(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent innerAgent, CancellationToken cancellationToken)
    {
        // Remove any ResponseFormat from the options to emulate an agent that doesn't support structured output natively.
        options = options?.Clone();
        if (options is not null)
        {
            options.ResponseFormat = null;
        }

        return innerAgent.RunAsync(messages, session, options, cancellationToken);
    }
}
