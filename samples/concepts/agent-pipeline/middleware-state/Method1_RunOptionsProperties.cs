// Method 1: AgentRunOptions.AdditionalProperties
//
// STATE DIRECTION: DOWN — caller → middleware layers (the LLM never sees it)
// KEY API: AgentRunOptions.AdditionalProperties (AdditionalPropertiesDictionary?)
//
// The caller injects per-request metadata (requestId, userId, userTier) into
// AgentRunOptions.AdditionalProperties before calling RunAsync. Two middleware layers
// read those values without any changes to the agent or its tools.
//
// Pipeline (outer → inner):
//   CorrelationAuditMiddleware  ← reads requestId/userId, logs pre/post timestamps
//   TierCheckMiddleware         ← reads userTier; "free" short-circuits, "pro" passes through
//   ModerationAgent (LLM)
//
// KEY INSIGHT: AdditionalProperties is null by default — always initialize with
//   options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
// before writing, and null-check defensively in middleware.
//
// TWO RUNS:
//   Run A — userTier=pro  → both middleware fire, LLM is called
//   Run B — userTier=free → TierCheckMiddleware short-circuits, LLM NOT called

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.AgentPipeline.MiddlewareState;

public static class RunOptionsProperties
{
    private const string Ticket =
        "Ticket #T-8821\n" +
        "User: customer@example.com\n" +
        "Subject: Refund request for duplicate charge\n" +
        "Message: I was charged twice for the same order. Please escalate this to billing immediately.";

    // ── Middleware ───────────────────────────────────────────────────────────

    private static async Task<AgentResponse> CorrelationAuditMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        string requestId = options?.AdditionalProperties?.TryGetValue("requestId", out object? rid) == true
            ? rid?.ToString() ?? "(none)" : "(none)";
        string userId = options?.AdditionalProperties?.TryGetValue("userId", out object? uid) == true
            ? uid?.ToString() ?? "(none)" : "(none)";

        Output.Gray($"[AUDIT PRE]  {DateTimeOffset.Now:HH:mm:ss} requestId={requestId} userId={userId}");

        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        Output.Gray($"[AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} requestId={requestId} — {response.Text[..Math.Min(80, response.Text.Length)]}...");
        return response;
    }

    private static async Task<AgentResponse> TierCheckMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        string userTier = options?.AdditionalProperties?.TryGetValue("userTier", out object? tier) == true
            ? tier?.ToString() ?? "free" : "free";

        if (userTier.Equals("free", StringComparison.OrdinalIgnoreCase))
        {
            Output.Yellow($"[TIER CHECK] userTier=free — short-circuiting. LLM NOT called.");
            return new AgentResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    "Automated moderation is available on Pro and above. Please upgrade to process this ticket.")
            ]);
        }

        Output.Gray($"[TIER CHECK] userTier={userTier} — forwarding to LLM.");
        return await innerAgent.RunAsync(messages, session, options, cancellationToken);
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("Method 1 — AgentRunOptions.AdditionalProperties (DOWN the pipeline)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        AIAgent baseAgent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a content moderation agent. Review the support ticket and state whether it should be escalated to a human agent, and give a one-sentence reason.",
                name: "ModerationAgent");

        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(CorrelationAuditMiddleware, null)  // outermost — always fires
            .Use(TierCheckMiddleware, null)          // may short-circuit
            .Build();

        // Run A — Pro tier: both middleware fire, LLM is called
        Output.Yellow("RUN A — userTier=pro (passes through to LLM)");
        Output.Gray(Ticket);
        Console.WriteLine();

        AgentRunOptions proOptions = new()
        {
            AdditionalProperties = new()
            {
                ["requestId"] = "req-a1b2c3",
                ["userId"]    = "u-4242",
                ["userTier"]  = "pro"
            }
        };
        AgentResponse responseA = await agent.RunAsync([new ChatMessage(ChatRole.User, Ticket)], null, proOptions, CancellationToken.None);
        Output.Green($"Agent: {responseA.Text}");
        Output.Separator();

        // Run B — Free tier: TierCheckMiddleware short-circuits, LLM NOT called
        Output.Yellow("RUN B — userTier=free (short-circuited — LLM NOT called)");
        Output.Gray(Ticket);
        Console.WriteLine();

        AgentRunOptions freeOptions = new()
        {
            AdditionalProperties = new()
            {
                ["requestId"] = "req-d4e5f6",
                ["userId"]    = "u-1001",
                ["userTier"]  = "free"
            }
        };
        AgentResponse responseB = await agent.RunAsync([new ChatMessage(ChatRole.User, Ticket)], null, freeOptions, CancellationToken.None);
        Output.Yellow($"Result: {responseB.Text}");
        Output.Separator();

        Output.Gray("KEY LEARNING: AgentRunOptions.AdditionalProperties flows DOWN.");
        Output.Gray("Middleware reads it; the LLM never sees it.");
        Output.Gray("Notice [AUDIT PRE/POST] fires for both runs — it is outermost.");
        Output.Gray("Notice the LLM is not called in Run B — TierCheckMiddleware short-circuits.");
        Output.Separator();
    }
}
