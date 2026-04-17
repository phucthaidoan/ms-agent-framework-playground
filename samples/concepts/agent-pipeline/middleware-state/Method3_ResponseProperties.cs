// Method 3: AgentResponse.AdditionalProperties
//
// STATE DIRECTION: UP — inner middleware → outer middleware (invisible to the caller)
// KEY API: AgentResponse.AdditionalProperties (AdditionalPropertiesDictionary?)
//
// FlagExtractionMiddleware (inner) calls the LLM, inspects the response text for trigger
// words, and if found attaches a ModerationFlag to response.AdditionalProperties.
// RoutingMiddleware (outer) reads that flag after the inner returns and decides how to
// route: GREEN for approved, RED for flagged — all without the caller knowing any of this.
//
// Pipeline (outer → inner):
//   RoutingMiddleware       ← reads response.AdditionalProperties["moderationFlag"] post-run
//   FlagExtractionMiddleware ← calls LLM, scans response, writes moderationFlag if triggered
//   ModerationAgent (LLM)
//
// KEY INSIGHT: response.AdditionalProperties is null by default. FlagExtractionMiddleware
// initialises it only when a flag is needed. RoutingMiddleware always null-checks before reading.
//
// TWO RUNS:
//   Run A — benign ticket → no flag set → GREEN routing
//   Run B — escalation ticket → flag set → RED routing

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.AgentPipeline.MiddlewareState;

public static class ResponseProperties
{
    private sealed record ModerationFlag(bool IsFlagged, string Reason);

    private const string FlagKey = "moderationFlag";

    private static readonly string[] TriggerWords = ["escalate", "escalation", "violation", "illegal", "harmful", "abuse"];

    // ── Middleware ───────────────────────────────────────────────────────────

    private static async Task<AgentResponse> FlagExtractionMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        string responseText = response.Text ?? string.Empty;
        string? triggeredBy = TriggerWords.FirstOrDefault(w =>
            responseText.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (triggeredBy is not null)
        {
            Output.Gray($"[FLAG EXTRACT] Trigger word \"{triggeredBy}\" found in response — setting moderationFlag.");
            response.AdditionalProperties ??= new();
            response.AdditionalProperties[FlagKey] = new ModerationFlag(true, $"trigger word: \"{triggeredBy}\"");
        }
        else
        {
            Output.Gray("[FLAG EXTRACT] No trigger words found — moderationFlag not set.");
        }

        return response;
    }

    private static async Task<AgentResponse> RoutingMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (response.AdditionalProperties?.TryGetValue(FlagKey, out object? flagObj) == true
            && flagObj is ModerationFlag flag && flag.IsFlagged)
        {
            Output.Red($"[ROUTING] Flag detected — IsFlagged=True, Reason={flag.Reason} → manual review queue.");
        }
        else
        {
            Output.Green("[ROUTING] No flag — routing to standard resolution queue.");
        }

        return response;
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("Method 3 — AgentResponse.AdditionalProperties (UP the pipeline)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        AIAgent baseAgent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
                    "You are a content moderation agent. Review the support ticket. " +
                    "State whether it should be escalated to a human agent, and give a one-sentence reason.",
                name: "ModerationAgent");

        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(RoutingMiddleware, null)        // outermost — reads flag after inner returns
            .Use(FlagExtractionMiddleware, null) // calls LLM, writes flag if triggered
            .Build();

        // Run A — benign ticket: no trigger words expected in LLM response
        string benignTicket =
            "Ticket #T-1100\n" +
            "Subject: Password reset not working\n" +
            "Message: I clicked 'Forgot password' but the email never arrived. Can you resend it?";

        Output.Yellow("RUN A — Benign ticket (no flag expected)");
        Output.Gray(benignTicket);
        Console.WriteLine();

        AgentResponse responseA = await agent.RunAsync(benignTicket);
        Output.Green($"Agent: {responseA.Text}");
        Output.Separator();

        // Run B — escalation ticket: LLM should mention escalation in its response
        string escalationTicket =
            "Ticket #T-2200\n" +
            "Subject: Billing fraud — unauthorised charges\n" +
            "Message: Someone has used my payment details without consent. This is a serious violation. " +
            "I need this escalated to your fraud team immediately.";

        Output.Yellow("RUN B — Escalation ticket (flag expected)");
        Output.Gray(escalationTicket);
        Console.WriteLine();

        AgentResponse responseB = await agent.RunAsync(escalationTicket);
        Output.Yellow($"Agent: {responseB.Text}");
        Output.Separator();

        Output.Gray("KEY LEARNING: AgentResponse.AdditionalProperties flows UP.");
        Output.Gray("Inner middleware attaches out-of-band metadata; outer reads it for routing.");
        Output.Gray("The caller receives the same AgentResponse — the flag is invisible to it.");
        Output.Separator();
    }
}
