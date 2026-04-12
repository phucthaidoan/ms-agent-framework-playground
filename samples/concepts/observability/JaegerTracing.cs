using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Samples.SampleUtilities;
using System.ClientModel;

namespace Samples.Observability;

public static class JaegerTracing
{
    public static async Task RunSample()
    {
        // Start Jaeger container for trace visualization
        Output.Gray("Starting Jaeger for trace visualization...");
        var jaeger = new ContainerBuilder("jaegertracing/all-in-one:latest")
            .WithPortBinding(16686, 16686)  // UI
            .WithPortBinding(4317, 4317)    // OTLP gRPC
            .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(16686)))
            .Build();

        await jaeger.StartAsync();
        Output.Green("Jaeger UI: http://localhost:16686");
        Output.Separator();

        // Configure OpenTelemetry
        var sourceName = "JaegerTracingDemo";
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(sourceName))
            .AddSource(sourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri("http://localhost:4317"))
            .Build();

        var activitySource = new ActivitySource(sourceName);

        try
        {
            using var rootSpan = activitySource.StartActivity("AgentRun");

            // Create the OpenAI client with OTel instrumentation
            string apiKey = SecretManager.GetOpenAIApiKey();
            var chatClient = new OpenAIClient(new ApiKeyCredential(apiKey))
                .GetChatClient("gpt-4.1-nano")
                .AsIChatClient()
                .AsBuilder()
                .UseOpenTelemetry(sourceName: sourceName, configure: c => c.EnableSensitiveData = true)
                .Build();

            ChatClientAgent agent = chatClient.AsAIAgent(
                instructions: "You are a helpful assistant. Keep responses brief.");

            Output.Green("Ask the agent a question (traces will appear in Jaeger):");
            Console.Write("> ");
            string input = Console.ReadLine() ?? "What is the capital of France?";

            AgentResponse response = await agent.RunAsync(input);
            Output.Separator();
            Console.WriteLine(response);
            Output.Separator();

            Output.Green($"View traces at: http://localhost:16686 (search for '{sourceName}')");
            Output.Gray("Press Enter to stop Jaeger and exit...");
            Console.ReadLine();
        }
        finally
        {
            await jaeger.StopAsync();
        }
    }
}
