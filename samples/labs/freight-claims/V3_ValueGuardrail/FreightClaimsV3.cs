// V3: Value Guardrail Middleware (Stacked Middleware + Early-Exit)
//
// NEW CONCEPTS: Stacked middleware, early-exit pattern
//
// What's added over V2:
//   - ValueGuardrailMiddleware sits BETWEEN AuditMiddleware and the agent
//   - If declared value > $10,000: short-circuits — does NOT call innerAgent.RunAsync
//     Returns a hardcoded "escalate" AgentResponse instead
//   - If value is normal: passes through to the agent as before
//
// Registration order matters:
//   .Use(AuditMiddleware, null)        ← outermost: always fires, captures even escalated cases
//   .Use(ValueGuardrailMiddleware, null) ← fires second: may short-circuit before LLM is called
//
// Pipeline (outer → inner):
//   AuditMiddleware → ValueGuardrailMiddleware → ClaimsTriageAgent (LLM)
//
// KEY INSIGHT: AuditMiddleware is outermost, so its [AUDIT POST] fires even when the guardrail
// short-circuits. The audit log proves the escalation happened — without calling the LLM.
//
// WHAT'S MISSING: ApproveClaim tool fires without any human review. V4 adds the approval gate.

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.Labs.FreightClaims.V3_ValueGuardrail;

public static class FreightClaimsV3
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

    // ── Middleware ───────────────────────────────────────────────────────────

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

    // Extracts the first integer that follows a "$" in the claim text.
    // Sufficient for hardcoded test prompts; not intended for production parsing.
    private static bool TryExtractDeclaredValue(IEnumerable<ChatMessage> messages, out int value)
    {
        value = 0;
        string claimText = messages.LastOrDefault()?.Text ?? string.Empty;
        int dollarIdx = claimText.IndexOf('$');
        if (dollarIdx < 0) return false;

        // Collect digits (and optional commas) after the '$'
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

            // Early-exit: return without calling innerAgent.RunAsync
            return new AgentResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                    $"Claim escalated to senior adjuster: declared value ${declaredValue:N0} exceeds auto-approval threshold of ${HighValueThreshold:N0}.")
            ]);
        }

        // Normal path: pass through to the agent
        return await innerAgent.RunAsync(messages, session, options, cancellationToken);
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
        Output.Title("Freight Claims V3 — Value Guardrail (Stacked Middleware + Early-Exit)");
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

        // Pipeline: AuditMiddleware (outer) → ValueGuardrailMiddleware → ClaimsTriageAgent (inner)
        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(AuditMiddleware, null)           // outermost — always fires
            .Use(ValueGuardrailMiddleware, null)  // intercepts high-value before LLM
            .Build();

        // Scenario A — normal claim (guardrail passes through)
        Output.Yellow("SCENARIO A — Normal low-value claim ($450) — passes through guardrail");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent summary: {responseA.Text}");
        Output.Separator();

        // Scenario B — high-value claim (guardrail intercepts, LLM never called)
        Output.Yellow("SCENARIO B — High-value claim ($15,000) — GUARDRAIL FIRES, LLM NOT CALLED");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Yellow($"Result: {responseB.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING: Middleware can short-circuit the pipeline.");
        Output.Yellow("Notice [AUDIT POST] still fires for Scenario B — audit is outermost,");
        Output.Yellow("so it captures the escalation even though the LLM was never called.");
    }
}
