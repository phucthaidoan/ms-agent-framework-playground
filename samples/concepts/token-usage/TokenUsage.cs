using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.ClientModel;
using System.Diagnostics;

namespace Samples.TokenUsage;

public static class TokenUsage
{
    public static async Task RunSample()
    {
        //Create Raw Connection
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        await RunByModel(client, "gpt-4.1-nano");
        //await RunByModel(client, "o4-mini"); // reasoning model
        //await RunByModel(client, "gpt-5.2");
    }

    private static async Task RunByModel(OpenAIClient client, string model)
    {
        Output.Gray($"Testing Model: {model} on OpenAI");
        Console.WriteLine();
        
        //Create Agent
        /*ChatClientAgent agent = client.GetChatClient(model).AsAIAgent("You are an expert in the companies Internal Knowledge Base. " +
            "Here are some internal knowledge base: " +
            "- Question: What is the WI-FI Password at the Office?. Answer: The Password is 'Guest42'");

        string? message = "What is wifi password?";*/

        ChatClientAgent agent = client.GetChatClient(model).AsAIAgent();

        string? message = "What is the capital of VietNam? Provide short answer.";

        Output.Blue("Input:");
        Console.WriteLine(message);

        Stopwatch stopwatch = Stopwatch.StartNew();

        Console.WriteLine();

        AgentResponse response = await agent.RunAsync(message);
        long milliseconds = stopwatch.ElapsedMilliseconds;

        Output.Green("Output:");

        Console.WriteLine(response);
        Console.WriteLine();

        Output.Red("Usage:");

        if (response.Usage != null)
        {
            Console.WriteLine($"- Input Tokens: {response.Usage.InputTokenCount}");
            Console.WriteLine($"- Cached Tokens: {response.Usage.CachedInputTokenCount ?? 0}");
            Console.WriteLine($"- Output Tokens: {response.Usage.OutputTokenCount} " +
                              $"({response.Usage.ReasoningTokenCount ?? 0} being reasoning Tokens)");
        }

        Console.WriteLine();

        Output.Magenta("Time spent:");
        Console.WriteLine($"{milliseconds} milli-seconds");

        Output.Separator();
    }
}