// V6: Stacked IChatClient Middleware (Rate Limiter + Token Budget)
//
// NEW CONCEPT: DelegatingChatClient — class-based IChatClient middleware for stateful concerns
//
// What's added over V5:
//   - RateLimitingChatClient wraps the raw IChatClient (innermost layer, closest to wire)
//   - Uses System.Threading.RateLimiting.FixedWindowRateLimiter: 2 permits per 10-second window
//   - If a permit is not acquired: throws InvalidOperationException — OpenAI NOT called
//   - TokenBudgetMiddleware (from V5) wraps RateLimitingChatClient — it is now the OUTER IChatClient layer
//   - AuditMiddleware updated with try/catch so exceptions (rate limit) appear in the audit log
//
// Why DelegatingChatClient instead of the inline lambda pattern from V5:
//   - The rate limiter is stateful — it holds a RateLimiter instance that must be disposed
//   - DelegatingChatClient provides a Dispose(bool) override for proper resource cleanup
//   - Use the inline lambda (AsBuilder().Use()) for stateless, one-off middleware
//   - Use DelegatingChatClient for middleware that owns resources (timers, semaphores, counters)
//
// Why TokenBudget is OUTER and RateLimiter is INNER:
//   - Construction order: rateLimitedClient.AsBuilder().Use(TokenBudgetMiddleware).Build()
//     wraps RateLimitingChatClient from the outside, making TokenBudget fire first
//   - Consequence: oversized claims are rejected by the budget before reaching the rate limiter —
//     no permit is consumed, protecting the quota from wasted requests
//   - This mirrors the agent-run middleware rule: first .Use() call is outermost
//
// Construction order:
//   1. Build FixedWindowRateLimiter (2 permits / 10 seconds, no queue)
//   2. Wrap raw IChatClient with RateLimitingChatClient (innermost)
//   3. Wrap RateLimitingChatClient with TokenBudgetMiddleware via .AsBuilder().Use() (outer)
//   4. Build AIAgent from double-wrapped IChatClient: budgetedClient.AsAIAgent(...)
//   5. Wrap AIAgent with agent-level middleware: .AsBuilder().Use(...).Build()
//
// Full pipeline (outer → inner):
//   AuditMiddleware → ValueGuardrailMiddleware → ClaimsTriageAgent → ApprovalGateMiddleware
//     → TokenBudgetMiddleware → RateLimitingChatClient → OpenAI
//
// Scenario run order (designed to tell a story):
//   D: Guardrail fires  → IChatClient layer never reached  (no permit consumed)
//   A: Normal claim     → Both permits consumed            (window now exhausted)
//   E: Oversized claim  → TokenBudget rejects              (no permit consumed — rate limiter not reached)
//   B: Normal claim     → Rate limit fires                 (window exhausted by Scenario A)
//   C: Normal claim     → Rate limit fires again           (same exhausted window)

using System.ComponentModel;
using System.Threading.RateLimiting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.FreightClaims.V6_RateLimiter;

public static class FreightClaimsV6
{
    private const int HighValueThreshold = 10_000;
    private const int TokenBudget        = 400;   // normal 4-line claims ≈ 37 tokens (pass); ScenarioE user message ≈ 512 tokens (blocked)
                                                   // Note: only m.Text is summed — system prompt/tool schemas arrive as structured content
                                                   // with null .Text, so the estimate reflects user message text only, not the full wire payload
    private const int PermitLimit        = 2;     // 2 LLM calls per 10-second window; one agent turn uses 2 (lookup + approve)

    // ── Shipment data ────────────────────────────────────────────────────────
    private record ShipmentRecord(string ShipmentId, string CargoType, int DeclaredValue, string Origin, string Destination);

    private static readonly Dictionary<string, ShipmentRecord> Shipments = new()
    {
        ["SHP-1001"] = new("SHP-1001", "General goods",  450,    "Chicago",  "Detroit"),
        ["SHP-2002"] = new("SHP-2002", "Electronics",    15_000, "San Jose", "Austin"),
        ["SHP-3003"] = new("SHP-3003", "Perishables",    2_500,  "Seattle",  "Portland"),
        ["SHP-4004"] = new("SHP-4004", "Industrial",     3_800,  "Houston",  "Dallas"),
    };

    // ── Tools ────────────────────────────────────────────────────────────────
    [Description("Look up shipment details from the internal logistics system.")]
    private static string LookupShipment(
        [Description("The shipment ID, e.g. SHP-1001")] string shipmentId)
    {
        if (Shipments.TryGetValue(shipmentId, out var rec))
        {
            Output.Gray($"  [TOOL] LookupShipment({shipmentId}) → cargo={rec.CargoType}, value=${rec.DeclaredValue}");
            return $"ShipmentId={rec.ShipmentId}, CargoType={rec.CargoType}, DeclaredValue=${rec.DeclaredValue}, Origin={rec.Origin}, Destination={rec.Destination}";
        }
        Output.Gray($"  [TOOL] LookupShipment({shipmentId}) → not found");
        return $"Shipment {shipmentId} not found in system.";
    }

    [Description("Record the triage decision for a freight claim.")]
    private static string ApproveClaim(
        [Description("The shipment ID")] string shipmentId,
        [Description("Decision: approve, escalate, or reject")] string decision,
        [Description("Reason for the decision")] string reason)
    {
        Output.Gray($"  [TOOL] ApproveClaim({shipmentId}, {decision})");
        if (decision.Equals("approve", StringComparison.OrdinalIgnoreCase))
            Output.Green($"  DECISION [{shipmentId}]: APPROVED — {reason}");
        else if (decision.Equals("escalate", StringComparison.OrdinalIgnoreCase))
            Output.Yellow($"  DECISION [{shipmentId}]: ESCALATED — {reason}");
        else
            Output.Red($"  DECISION [{shipmentId}]: REJECTED — {reason}");

        return $"Decision '{decision}' recorded for {shipmentId}.";
    }

    // ── IChatClient Middleware: RateLimitingChatClient (DelegatingChatClient pattern) ──────
    //
    // DelegatingChatClient is the class-based middleware pattern for Microsoft.Extensions.AI.
    // Use it when your middleware is stateful — it holds resources that need disposal.
    //
    // Compare with V5's TokenBudgetMiddleware (inline lambda):
    //   - Inline lambda: stateless, one-off, no resources to clean up → use AsBuilder().Use(func)
    //   - DelegatingChatClient: stateful, owns a RateLimiter → needs Dispose(bool) override
    //
    // Permit acquisition:
    //   - AcquireAsync returns RateLimitLease (using-disposed automatically)
    //   - lease.IsAcquired == false means rate limit exceeded (no exception from the limiter itself)
    //   - We throw InvalidOperationException to signal an infrastructure failure (not a business decision)
    //   - This propagates up through the agent and is caught by the caller's try/catch
    //
    private sealed class RateLimitingChatClient(IChatClient innerClient, RateLimiter rateLimiter)
        : DelegatingChatClient(innerClient)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            using RateLimitLease lease = await rateLimiter.AcquireAsync(
                permitCount: 1, cancellationToken);

            if (!lease.IsAcquired)
            {
                Output.Red($"[ChatMW] [RATE LIMITER] Permit denied — {PermitLimit} permits/{10}s window exhausted. OpenAI NOT called.");
                throw new InvalidOperationException(
                    $"Rate limit exceeded: more than {PermitLimit} LLM calls within the 10-second window. Retry after the window resets.");
            }

            Output.Gray($"[ChatMW] [RATE LIMITER] Permit acquired. Forwarding to OpenAI.");
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) rateLimiter.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── IChatClient Middleware: Token Budget (inline lambda, outer layer) ────
    //
    // Same as V5, but TokenBudget is raised to 800 so normal 4-line claims pass through.
    // This layer sits OUTSIDE RateLimitingChatClient in the pipeline:
    //   TokenBudgetMiddleware fires first → if within budget, RateLimitingChatClient fires next.
    // An oversized claim never reaches the rate limiter, so it doesn't consume a permit.
    //
    private static async Task<ChatResponse> TokenBudgetMiddleware(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient innerClient,
        CancellationToken cancellationToken)
    {
        IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();
        int totalChars = messageList.Sum(m => m.Text?.Length ?? 0);
        int estimatedTokens = totalChars / 4;

        if (estimatedTokens > TokenBudget)
        {
            Output.Red($"[ChatMW] [TOKEN BUDGET] Estimated {estimatedTokens} tokens exceeds budget of {TokenBudget}. Rejecting — rate limiter not reached (no permit consumed).");
            return new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    $"Claim rejected: submission too long (~{estimatedTokens} tokens estimated). " +
                    $"Resubmit with a description under {TokenBudget} tokens.")
            ]);
        }

        Output.Gray($"[ChatMW] [TOKEN BUDGET] Estimated {estimatedTokens} tokens — within budget. Forwarding to rate limiter.");
        return await innerClient.GetResponseAsync(messageList, options, cancellationToken);
    }

    // ── Agent Run Middleware ──────────────────────────────────────────────────

    private static async Task<AgentResponse> AuditMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        string claimText = messages.LastOrDefault()?.Text ?? "(no claim text)";
        Output.Gray($"[AgentMW][AUDIT PRE]  {DateTimeOffset.Now:HH:mm:ss} — {claimText[..Math.Min(80, claimText.Length)]}...");

        // V6 change: wrap in try/catch so infrastructure exceptions (e.g. rate limit) appear in audit log
        try
        {
            AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
            Output.Gray($"[AgentMW][AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} — {response.Text[..Math.Min(120, response.Text.Length)]}");
            return response;
        }
        catch (Exception ex)
        {
            Output.Red($"[AgentMW][AUDIT ERR]  {DateTimeOffset.Now:HH:mm:ss} — {ex.Message}");
            throw;
        }
    }

    private static bool TryExtractDeclaredValue(IEnumerable<ChatMessage> messages, out int value)
    {
        value = 0;
        string claimText = messages.LastOrDefault()?.Text ?? string.Empty;
        int dollarIdx = claimText.IndexOf('$');
        if (dollarIdx < 0) return false;
        string rest = claimText[(dollarIdx + 1)..].Replace(",", "");
        string digits = new(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out value);
    }

    private static async Task<AgentResponse> ValueGuardrailMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        if (TryExtractDeclaredValue(messages, out int declaredValue) && declaredValue > HighValueThreshold)
        {
            Output.Yellow($"[AgentMW][GUARDRAIL] Declared value ${declaredValue:N0} exceeds ${HighValueThreshold:N0} threshold. Escalating — IChatClient layer never reached (no permit consumed).");
            return new AgentResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    $"Claim escalated to senior adjuster: declared value ${declaredValue:N0} exceeds auto-approval threshold of ${HighValueThreshold:N0}.")
            ]);
        }
        return await innerAgent.RunAsync(messages, session, options, cancellationToken);
    }

    // ── Function Invocation Middleware ───────────────────────────────────────

    private static async ValueTask<object?> ApprovalGateMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        if (context.Function.Name != nameof(ApproveClaim))
            return await next(context, cancellationToken);

        string decision   = context.Arguments.TryGetValue("decision",   out var d) ? d?.ToString() ?? "?" : "?";
        string shipmentId = context.Arguments.TryGetValue("shipmentId", out var s) ? s?.ToString() ?? "?" : "?";
        string reason     = context.Arguments.TryGetValue("reason",     out var r) ? r?.ToString() ?? "(no reason given)" : "(no reason given)";

        Output.Yellow($"[FuncMW] [APPROVAL GATE] Agent wants to {decision.ToUpperInvariant()} claim {shipmentId} — reason: {reason}");
        Console.Write("  Proceed with this decision? (Y/N): ");
        string input = Console.ReadLine() ?? "N";

        if (!input.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            Output.Red("  [FuncMW] [APPROVAL GATE] Decision REJECTED by reviewer.");
            return "REJECTED_BY_REVIEWER — decision was not approved by the human reviewer.";
        }

        Output.Green("  [FuncMW] [APPROVAL GATE] Decision approved. Calling ApproveClaim...");
        return await next(context, cancellationToken);
    }

    // ── Test scenarios ───────────────────────────────────────────────────────

    // Scenario D: High-value — guardrail fires at agent-run layer (no permit consumed)
    private const string ScenarioD =
        "Shipment ID: SHP-2002\n" +
        "Cargo type: Electronics\n" +
        "Declared value: $15,000\n" +
        "Damage description: Pallet of laptops dropped during unloading. 6 units have cracked screens.";

    // Scenario A: Normal low-value — both permits consumed (exhausts the 10s window)
    private const string ScenarioA =
        "Shipment ID: SHP-1001\n" +
        "Cargo type: General goods\n" +
        "Declared value: $450\n" +
        "Damage description: Box arrived with a crushed corner. Contents appear intact.";

    // Scenario E: Oversized — token budget rejects before rate limiter (no permit consumed)
    private const string ScenarioE =
        "Shipment ID: SHP-9999\n" +
        "Cargo type: General goods\n" +
        "Declared value: $1,200\n" +
        "Damage description:\n" +
        "The shipment arrived at the destination warehouse on the morning of the scheduled delivery date. " +
        "Upon initial inspection by the receiving team, multiple external cartons showed visible signs of " +
        "impact damage including crushed corners, torn outer packaging, and moisture staining across the " +
        "lower third of the pallet. The pallet wrap was completely torn on two sides, suggesting the load " +
        "had shifted during transit. The receiving supervisor documented the damage before signing the " +
        "delivery receipt and noted the condition as 'damaged on arrival' in the carrier's delivery log. " +
        "Internal packaging inspection revealed that approximately 40% of the units inside had sustained " +
        "secondary damage from the compromised outer cartons. Items near the base of the pallet were the " +
        "most severely affected, with several units rendered completely non-functional. The remaining 60% " +
        "showed minor surface scuffs and dents but appeared operationally intact pending further testing. " +
        "Photographic evidence was captured at the time of receipt and has been attached to this claim. " +
        "The shipment ID matches the purchase order and the quantities match the manifest, confirming this " +
        "is the correct shipment. A formal inspection report was completed by the warehouse quality team " +
        "within two hours of receipt and has been submitted to the carrier representative on site. " +
        "The shipper was notified the same day via email and a formal claim notice was issued. " +
        "The total replacement cost of the damaged units has been calculated based on the invoice value " +
        "and is consistent with the declared value on the bill of lading. The company is requesting full " +
        "reimbursement for the damaged units and partial reimbursement for the units requiring further " +
        "assessment. Supporting documents including photos, inspection report, carrier delivery receipt, " +
        "and invoice copies are available upon request. The claim was filed within the carrier's required " +
        "timeframe and all notification obligations have been met.";

    // Scenario B: Normal — rate limit fires (window exhausted by Scenario A's 2 LLM round-trips)
    private const string ScenarioB =
        "Shipment ID: SHP-3003\n" +
        "Cargo type: Perishables\n" +
        "Declared value: $2,500\n" +
        "Damage description: Refrigerated truck broke down. 200kg of salmon spoiled.";

    // Scenario C: Normal — rate limit fires again (confirms window is still exhausted)
    private const string ScenarioC =
        "Shipment ID: SHP-4004\n" +
        "Cargo type: Industrial\n" +
        "Declared value: $3,800\n" +
        "Damage description: Equipment housing cracked during transit. Internal components undamaged per visual inspection.";

    // ── Entry point ──────────────────────────────────────────────────────────
    public static async Task RunSample()
    {
        Output.Title("Freight Claims V6 — Stacked IChatClient Middleware (Rate Limiter + Token Budget)");
        Output.Separator();

        Output.Gray("Shipment database (what LookupShipment will return):");
        foreach (var (id, rec) in Shipments)
            Output.Gray($"  {id}  {rec.CargoType,-16} ${rec.DeclaredValue,6:N0}  {rec.Origin} → {rec.Destination}");
        Output.Gray($"\nRate limit: {PermitLimit} LLM calls per 10-second window (no queuing — fail fast)");
        Output.Gray($"Token budget: {TokenBudget} estimated tokens max per request");
        Output.Gray("Note: one successful agent turn = 2 LLM calls (LookupShipment + ApproveClaim).");
        Output.Gray("      Scenario A will exhaust both permits, making B and C fail.");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // Step 1 — Rate limiter: 2 permits per 10-second fixed window, no queuing
        var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            Window            = TimeSpan.FromSeconds(10),
            PermitLimit       = PermitLimit,
            QueueLimit        = 0,                // fail fast — no waiting in queue
            AutoReplenishment = true
        });

        // Step 2 — Wrap raw IChatClient with rate limiter (innermost layer)
        IChatClient rateLimitedClient = new RateLimitingChatClient(
            new OpenAIClient(apiKey).GetChatClient("gpt-4.1").AsIChatClient(),
            limiter);

        // Step 3 — Wrap RateLimitingChatClient with token budget (outer IChatClient layer)
        // Construction order determines pipeline order: TokenBudget wraps from outside → fires first
        IChatClient budgetedClient = rateLimitedClient
            .AsBuilder()
            .Use(getResponseFunc: TokenBudgetMiddleware, getStreamingResponseFunc: null)
            .Build();

        // Step 4 — Build AIAgent from double-wrapped IChatClient
        AIAgent baseAgent = budgetedClient.AsAIAgent(
            instructions:
                "You are a freight claims triage agent. " +
                "For every claim: call LookupShipment to get cargo details, " +
                "then classify the damage (weather / handling / packaging / carrier liability), " +
                "then call ApproveClaim with decision approve, escalate, or reject and a concise reason.",
            name: "ClaimsTriageAgent",
            tools:
            [
                AIFunctionFactory.Create(LookupShipment, name: nameof(LookupShipment)),
                AIFunctionFactory.Create(ApproveClaim,   name: nameof(ApproveClaim)),
            ]);

        // Step 5 — Wrap AIAgent with agent-level middleware (same stack as V5)
        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(AuditMiddleware, null)
            .Use(ValueGuardrailMiddleware, null)
            .Use(ApprovalGateMiddleware)
            .Build();

        // ── Scenario D — High-value claim: guardrail fires, IChatClient never reached ──────────
        Output.Yellow("SCENARIO D — High-value claim ($15,000) — GUARDRAIL fires, IChatClient layer never reached");
        Output.Gray("Expected: ValueGuardrailMiddleware escalates. RateLimiter never reached — no permit consumed.");
        Output.Gray(ScenarioD);
        Console.WriteLine();
        AgentResponse responseD = await agent.RunAsync(ScenarioD);
        Output.Yellow($"Result: {responseD.Text}");
        Output.Separator();

        // ── Scenario A — Normal claim: both permits consumed ─────────────────────────────────
        Output.Yellow("SCENARIO A — Normal claim ($450) — 2 LLM round-trips consume BOTH permits");
        Output.Gray($"Expected: succeeds. Both {PermitLimit} permits consumed in this turn (lookup + approve).");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        try
        {
            AgentResponse responseA = await agent.RunAsync(ScenarioA);
            Output.Green($"Agent summary: {responseA.Text}");
        }
        catch (Exception ex)
        {
            Output.Red($"Exception: {ex.Message}");
        }
        Output.Separator();

        // ── Scenario E — Oversized claim: token budget fires, rate limiter not reached ─────────
        Output.Yellow("SCENARIO E — Oversized claim (~900 tokens) — TOKEN BUDGET fires, rate limiter not reached");
        Output.Gray("Expected: TokenBudgetMiddleware rejects. RateLimiter never reached — no permit consumed.");
        Output.Gray(ScenarioE[..100] + "...[truncated for display]");
        Console.WriteLine();
        try
        {
            AgentResponse responseE = await agent.RunAsync(ScenarioE);
            Output.Red($"Result: {responseE.Text}");
        }
        catch (Exception ex)
        {
            Output.Red($"Exception: {ex.Message}");
        }
        Output.Separator();

        // ── Scenario B — Normal claim: rate limit fires (window exhausted by Scenario A) ───────
        Output.Yellow("SCENARIO B — Normal claim ($2,500) — RATE LIMIT fires (window exhausted by Scenario A)");
        Output.Gray("Expected: within budget, but 0 permits left → InvalidOperationException from RateLimitingChatClient.");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        try
        {
            AgentResponse responseB = await agent.RunAsync(ScenarioB);
            Output.Green($"Agent summary: {responseB.Text}");
        }
        catch (Exception ex)
        {
            Output.Red($"Rate limit exception: {ex.Message}");
        }
        Output.Separator();

        // ── Scenario C — Normal claim: rate limit fires again ───────────────────────────────
        Output.Yellow("SCENARIO C — Normal claim ($3,800) — RATE LIMIT fires again (window still exhausted)");
        Output.Gray("Expected: same as B — rate limit exceeded. Confirms window resets are time-based, not request-based.");
        Output.Gray(ScenarioC);
        Console.WriteLine();
        try
        {
            AgentResponse responseC = await agent.RunAsync(ScenarioC);
            Output.Green($"Agent summary: {responseC.Text}");
        }
        catch (Exception ex)
        {
            Output.Red($"Rate limit exception: {ex.Message}");
        }
        Output.Separator();

        Output.Yellow("KEY LEARNING: Stacked IChatClient middleware — construction order = pipeline order.");
        Output.Yellow($"Pipeline: TokenBudgetMiddleware → RateLimitingChatClient → OpenAI");
        Output.Yellow("");
        Output.Yellow("  • Guardrail fires (agent-run layer) → IChatClient layer never reached → no permit consumed");
        Output.Yellow("  • Oversized claim → TokenBudget REJECTS → RateLimiter never reached → no permit consumed");
        Output.Yellow("  • Normal claim    → TokenBudget PASSES → RateLimiter acquires permit → OpenAI called");
        Output.Yellow("  • Rate limit hit  → RateLimiter REJECTS → OpenAI not called → exception propagates");
        Output.Yellow("");
        Output.Yellow("DelegatingChatClient is the class-based pattern for stateful IChatClient middleware.");
        Output.Yellow("Use it when middleware owns resources (RateLimiter, semaphores, counters) that need disposal.");
        Output.Yellow("Use the inline lambda (AsBuilder().Use()) for stateless, one-off middleware like token budget.");

        limiter.Dispose();
    }
}
