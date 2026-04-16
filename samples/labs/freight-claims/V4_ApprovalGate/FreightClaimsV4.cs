// V4: Function Calling Middleware (HITL Approval Gate)
//
// NEW CONCEPT: Function Calling Middleware
//
// What's added over V3:
//   - ApprovalGateMiddleware intercepts individual tool invocations (not the whole agent turn)
//   - Fires ONLY for the ApproveClaim tool — LookupShipment passes through uninterrupted
//   - When ApproveClaim is about to be called: pauses and asks for human Y/N confirmation
//   - If "N": returns "REJECTED_BY_REVIEWER" string instead of calling the real function
//
// Two distinct middleware signatures in one pipeline:
//   Agent Run Middleware:      (messages, session, options, innerAgent, ct) → Task<AgentResponse>
//   Function Invocation Middleware: (agent, context, next, ct) → ValueTask<object?>
//
// Both registered in the same .AsBuilder() chain — they intercept at different depths:
//   AuditMiddleware         — outermost, wraps the entire RunAsync call
//   ValueGuardrailMiddleware — intercepts before LLM if value > $10k
//   ApprovalGateMiddleware  — innermost decision layer, fires per tool invocation mid-turn
//
// Pipeline depth (outer → inner):
//   AuditMiddleware → ValueGuardrailMiddleware → ClaimsTriageAgent (LLM) → ApprovalGateMiddleware → ApproveClaim tool
//
// WHAT'S MISSING: No protection against oversized claim submissions. V5 adds the token budget
// enforcer at the IChatClient layer — the innermost layer of all.

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.Labs.FreightClaims.V4_ApprovalGate;

public static class FreightClaimsV4
{
    private const int HighValueThreshold = 10_000;

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

    // ── Agent Run Middleware ──────────────────────────────────────────────────

    private static async Task<AgentResponse> AuditMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        string claimText = messages.LastOrDefault()?.Text ?? "(no claim text)";
        Output.Gray($"[AUDIT PRE]  {DateTimeOffset.Now:HH:mm:ss} — {claimText[..Math.Min(80, claimText.Length)]}...");

        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        Output.Gray($"[AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} — {response.Text[..Math.Min(120, response.Text.Length)]}");
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
            Output.Yellow($"[GUARDRAIL] Declared value ${declaredValue:N0} exceeds ${HighValueThreshold:N0} threshold. Escalating — LLM not called.");
            return new AgentResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    $"Claim escalated to senior adjuster: declared value ${declaredValue:N0} exceeds auto-approval threshold of ${HighValueThreshold:N0}.")
            ]);
        }
        return await innerAgent.RunAsync(messages, session, options, cancellationToken);
    }

    // ── Function Invocation Middleware ───────────────────────────────────────
    //
    // Different signature from Agent Run Middleware:
    //   (AIAgent agent, FunctionInvocationContext context, next, CancellationToken ct) → ValueTask<object?>
    //
    // This fires INSIDE the agent's tool-calling loop — after the LLM has decided to call a tool
    // but BEFORE the tool function executes. It sees individual function calls, not full messages.

    private static async ValueTask<object?> ApprovalGateMiddleware(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        // Only intercept ApproveClaim — LookupShipment and any others pass straight through
        if (context.Function.Name != nameof(ApproveClaim))
            return await next(context, cancellationToken);

        string decision   = context.Arguments.TryGetValue("decision",   out var d) ? d?.ToString() ?? "?" : "?";
        string shipmentId = context.Arguments.TryGetValue("shipmentId", out var s) ? s?.ToString() ?? "?" : "?";
        string reason     = context.Arguments.TryGetValue("reason",     out var r) ? r?.ToString() ?? "(no reason given)" : "(no reason given)";

        Output.Yellow($"[APPROVAL GATE] Agent wants to {decision.ToUpperInvariant()} claim {shipmentId} — reason: {reason}");
        Console.Write("  Proceed with this decision? (Y/N): ");
        string input = Console.ReadLine() ?? "N";

        if (!input.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            Output.Red($"  [APPROVAL GATE] Decision REJECTED by reviewer.");
            return "REJECTED_BY_REVIEWER. Do not retry ApproveClaim. " +
                   "Inform the user the claim requires human escalation and stop.";
        }

        Output.Green("  [APPROVAL GATE] Decision approved by reviewer. Calling ApproveClaim...");
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

    // ── Entry point ──────────────────────────────────────────────────────────
    public static async Task RunSample()
    {
        Output.Title("Freight Claims V4 — Function Calling Middleware (HITL Approval Gate)");
        Output.Separator();

        Output.Gray("Shipment database (what LookupShipment will return):");
        foreach (var (id, rec) in Shipments)
            Output.Gray($"  {id}  {rec.CargoType,-16} ${rec.DeclaredValue,6:N0}  {rec.Origin} → {rec.Destination}");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        AIAgent baseAgent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
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

        // Pipeline:
        //   AuditMiddleware (agent run) — outermost
        //   ValueGuardrailMiddleware (agent run)
        //   ApprovalGateMiddleware (function invocation) — fires per tool call
        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(AuditMiddleware, null)
            .Use(ValueGuardrailMiddleware, null)
            .Use(ApprovalGateMiddleware)
            .Build();

        // Scenario A — low-value claim (approval gate prompts before ApproveClaim)
        Output.Yellow("SCENARIO A — Normal low-value claim ($450) — approval gate will prompt");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent summary: {responseA.Text}");
        Output.Separator();

        // Scenario B — high-value claim (guardrail intercepts before LLM — no approval prompt)
        Output.Yellow("SCENARIO B — High-value claim ($15,000) — guardrail fires, no approval prompt");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Yellow($"Result: {responseB.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING: Two middleware layers intercepting at different depths.");
        Output.Yellow("Agent run middleware wraps entire RunAsync; function middleware wraps individual tool calls.");
        Output.Yellow("Both registered in one .AsBuilder() chain — the framework routes them to the right layer.");
    }
}
