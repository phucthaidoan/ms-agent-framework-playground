// V5: IChatClient Middleware (Token Budget Enforcer)
//
// NEW CONCEPT: IChatClient Middleware — the innermost pipeline layer
//
// What's added over V4:
//   - TokenBudgetMiddleware wraps the IChatClient (not the AIAgent)
//   - Counts approximate tokens in all messages before sending to OpenAI (chars / 4)
//   - If estimated tokens > 150: does NOT call innerClient.GetResponseAsync — returns a rejection response
//   - The agent-level pipeline (audit + guardrail + approval gate) still wraps the outside
//
// Why IChatClient middleware is different from Agent Run Middleware:
//   - It intercepts at the TRANSPORT level — the raw IList<ChatMessage> going to the API
//   - It sees the FULL message list: system prompt + tool definitions + conversation history
//   - Agent Run Middleware only sees the user-supplied messages; the system prompt and tool
//     definitions are added BELOW it by the framework's context layer
//   - This is the only layer where you can inspect what actually goes over the wire
//
// Construction order:
//   1. Get OpenAI ChatClient
//   2. Convert to IChatClient and wrap with TokenBudgetMiddleware: .AsIChatClient().AsBuilder().Use(...).Build()
//   3. Build AIAgent from the wrapped IChatClient: ichatClient.AsAIAgent(...)
//   4. Wrap the AIAgent with agent-level middleware: agent.AsBuilder().Use(...).Build()
//
// Full pipeline (outer → inner):
//   AuditMiddleware → ValueGuardrailMiddleware → ClaimsTriageAgent → ApprovalGateMiddleware → TokenBudgetMiddleware → OpenAI

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.FreightClaims.V5_TokenBudget;

public static class FreightClaimsV5
{
    private const int HighValueThreshold = 10_000;
    private const int TokenBudget = 150;

    // ── Shipment data ────────────────────────────────────────────────────────
    private record ShipmentRecord(string ShipmentId, string CargoType, int DeclaredValue, string Origin, string Destination);

    private static readonly Dictionary<string, ShipmentRecord> Shipments = new()
    {
        ["SHP-1001"] = new("SHP-1001", "General goods",  450,    "Chicago",  "Detroit"),
        ["SHP-2002"] = new("SHP-2002", "Electronics",    15_000, "San Jose", "Austin"),
        ["SHP-3003"] = new("SHP-3003", "Perishables",    2_500,  "Seattle",  "Portland"),
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

    // ── IChatClient Middleware ────────────────────────────────────────────────
    //
    // Signature for IChatClient.AsBuilder().Use(getResponseFunc: ...):
    //   (IList<ChatMessage> messages, ChatOptions? options, IChatClient innerClient, CancellationToken ct)
    //   → Task<ChatResponse>
    //
    // This is the innermost pipeline layer. It fires closest to the wire and sees the COMPLETE
    // message list including the system prompt and all tool definitions injected by the framework.

    private static async Task<ChatResponse> TokenBudgetMiddleware(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient innerClient,
        CancellationToken cancellationToken)
    {
        // Rough heuristic: 1 token ≈ 4 characters
        IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();
        int totalChars = messageList.Sum(m => m.Text?.Length ?? 0);
        int estimatedTokens = totalChars / 4;

        if (estimatedTokens > TokenBudget)
        {
            Output.Red($"[ChatMW] [TOKEN BUDGET] Estimated {estimatedTokens} tokens exceeds budget of {TokenBudget}. Rejecting — OpenAI not called.");
            return new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    $"Claim rejected: submission too long (~{estimatedTokens} tokens estimated). " +
                    $"Resubmit with a description under {TokenBudget} tokens.")
            ]);
        }

        Output.Gray($"[ChatMW] [TOKEN BUDGET] Estimated {estimatedTokens} tokens — within budget. Forwarding to OpenAI.");
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

        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        Output.Gray($"[AgentMW][AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} — {response.Text[..Math.Min(120, response.Text.Length)]}");
        return response;
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
            Output.Yellow($"[AgentMW][GUARDRAIL] Declared value ${declaredValue:N0} exceeds ${HighValueThreshold:N0} threshold. Escalating — LLM not called.");
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
    private const string ScenarioA =
        "Shipment ID: SHP-1001\n" +
        "Cargo type: General goods\n" +
        "Declared value: $450\n" +
        "Damage description: Box arrived with a crushed corner. Contents appear intact.";

    private const string ScenarioB =
        "Shipment ID: SHP-2002\n" +
        "Cargo type: Electronics\n" +
        "Declared value: $15,000\n" +
        "Damage description: Pallet of laptops dropped during unloading. 6 units have cracked screens.";

    private const string ScenarioD =
        "Shipment ID: SHP-3003\n" +
        "Cargo type: Perishables\n" +
        "Declared value: $2,500\n" +
        "Damage description: Refrigerated truck broke down. 200kg of salmon spoiled.";

    // Scenario E: medium-length claim (~700 chars ≈ 175 tokens) — triggers token budget at the 150-token limit
    private const string ScenarioE =
        "Shipment ID: SHP-4004\n" +
        "Cargo type: Industrial equipment\n" +
        "Declared value: $3,800\n" +
        "Damage description: " +
        "The shipment arrived with the outer crate visibly split along two edges. " +
        "The receiving team noted that the securing bolts on the internal frame had sheared, " +
        "likely due to impact during loading or transit. The equipment housing sustained a " +
        "deep gouge approximately 30cm long on the front panel. Internal components appear " +
        "undamaged based on visual inspection, but functional testing has not yet been completed. " +
        "The carrier's driver acknowledged the damage at the time of delivery and signed the " +
        "damage notation on the delivery receipt. Photos and a written statement from the " +
        "receiving supervisor are available. We are requesting reimbursement for crate repair " +
        "and panel replacement, estimated at $1,200 based on supplier quotes.";

    // Scenario C: oversized claim (~4000 chars ≈ 1000 tokens) — triggers token budget middleware
    private const string ScenarioC =
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
        "timeframe and all notification obligations have been met. We request expedited processing given " +
        "the operational impact of the missing inventory on our production schedule. Please advise on the " +
        "next steps required to finalise this claim. Additional context: this is the third consecutive " +
        "shipment from this carrier that has arrived in a damaged state over the past 45 days. A pattern " +
        "of mishandling has been observed and documented across multiple claim filings. The carrier's own " +
        "internal review team has been engaged and an escalation has been logged with their regional " +
        "operations manager. A copy of the escalation ticket is available upon request. We are requesting " +
        "that this claim be flagged for pattern review in addition to the standard individual claim process. " +
        "All prior claim numbers are referenced in the attached documentation for cross-referencing purposes.";

    // ── Entry point ──────────────────────────────────────────────────────────
    public static async Task RunSample()
    {
        Output.Title("Freight Claims V5 — IChatClient Middleware (Token Budget Enforcer)");
        Output.Separator();

        Output.Gray("Shipment database (what LookupShipment will return):");
        foreach (var (id, rec) in Shipments)
            Output.Gray($"  {id}  {rec.CargoType,-16} ${rec.DeclaredValue,6:N0}  {rec.Origin} → {rec.Destination}");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // Step 1: Build IChatClient with token budget middleware (innermost layer)
        IChatClient chatClientWithBudget = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsIChatClient()
            .AsBuilder()
            .Use(getResponseFunc: TokenBudgetMiddleware, getStreamingResponseFunc: null)
            .Build();

        // Step 2: Build AIAgent from the wrapped IChatClient
        AIAgent baseAgent = chatClientWithBudget.AsAIAgent(
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

        // Step 3: Wrap AIAgent with agent-level middleware (outer layers)
        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(AuditMiddleware, null)
            .Use(ValueGuardrailMiddleware, null)
            .Use(ApprovalGateMiddleware)
            .Build();

        // Scenario A — normal claim (token budget: within limit → OpenAI called)
        Output.Yellow("SCENARIO A — Normal low-value claim ($450) — within token budget");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent summary: {responseA.Text}");
        Output.Separator();

        // Scenario B — high-value claim (guardrail intercepts before token budget layer)
        Output.Yellow("SCENARIO B — High-value claim ($15,000) — guardrail fires first");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Yellow($"Result: {responseB.Text}");
        Output.Separator();

        // Scenario C — oversized claim (token budget fires at IChatClient layer)
        Output.Yellow("SCENARIO C — Oversized claim (~1000 tokens) — TOKEN BUDGET FIRES, OpenAI NOT called");
        Output.Gray(ScenarioC[..100] + "...[truncated for display]");
        Console.WriteLine();
        AgentResponse responseC = await agent.RunAsync(ScenarioC);
        Output.Red($"Result: {responseC.Text}");
        Output.Separator();

        // Scenario E — medium claim that hits the lower token budget
        Output.Yellow("SCENARIO E — Medium claim (~175 tokens) — TOKEN BUDGET FIRES at 150-token limit");
        Output.Gray(ScenarioE);
        Console.WriteLine();
        AgentResponse responseE = await agent.RunAsync(ScenarioE);
        Output.Red($"Result: {responseE.Text}");
        Output.Separator();

        // Scenario D — HITL approval demo
        Output.Yellow("SCENARIO D — Perishables claim ($2,500) — HITL approval demo (enter Y or N)");
        Output.Gray(ScenarioD);
        Console.WriteLine();
        AgentResponse responseD = await agent.RunAsync(ScenarioD);
        Output.Green($"Agent summary: {responseD.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING: IChatClient middleware is the innermost layer.");
        Output.Yellow("It fires closest to the wire and sees the FULL message list:");
        Output.Yellow("system prompt + tool definitions + conversation history + user message.");
        Output.Yellow("Agent Run Middleware only sees the user-supplied messages.");
    }
}
