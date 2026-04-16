// V2: Agent Pipeline + Audit Middleware (Agent Run Middleware)
//
// NEW CONCEPTS: Agent Pipeline structure + Agent Run Middleware
//
// What's added over V1:
//   - AuditMiddleware wraps the agent using .AsBuilder().Use(AuditMiddleware, null).Build()
//   - Logs [AUDIT PRE]  with timestamp before calling innerAgent.RunAsync
//   - Logs [AUDIT POST] with timestamp after getting the response
//
// The pipeline is now: AuditMiddleware → ClaimsTriageAgent (innermost)
//
// The pre/post split around innerAgent.RunAsync makes the pipeline execution model concrete:
// your code runs BEFORE and AFTER the entire agent turn, including all tool calls.
//
// WHAT'S MISSING: All claims still reach the LLM. V3 adds a value guardrail that
// intercepts high-value claims before the agent (and LLM) ever sees them.

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.Labs.FreightClaims.V2_AuditMiddleware;

public static class FreightClaimsV2
{
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

    // Agent Run Middleware signature:
    //   (messages, session, options, innerAgent, cancellationToken) → Task<AgentResponse>
    //
    // This is the outermost pipeline layer. It wraps the ENTIRE agent turn:
    // all tool calls happen inside innerAgent.RunAsync, between PRE and POST.
    private static async Task<AgentResponse> AuditMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        string claimText = messages.LastOrDefault()?.Text ?? "(no claim text)";
        string timestamp = DateTimeOffset.Now.ToString("HH:mm:ss");

        Output.Gray($"[AUDIT PRE]  {timestamp} — Claim received: {claimText[..Math.Min(80, claimText.Length)]}...");

        AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        Output.Gray($"[AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} — Decision recorded: {response.Text[..Math.Min(120, response.Text.Length)]}");

        return response;
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
        Output.Title("Freight Claims V2 — Agent Pipeline + Audit Middleware");
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

        // Wrap with audit middleware — outermost layer in the pipeline
        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(AuditMiddleware, null)
            .Build();

        // Scenario A — normal claim (audit brackets the run)
        Output.Yellow("SCENARIO A — Normal low-value claim ($450, general goods)");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent summary: {responseA.Text}");
        Output.Separator();

        // Scenario B — high-value claim (still auto-approved in V2 — audit records it)
        Output.Yellow("SCENARIO B — High-value claim ($15,000 electronics) — AUDIT LOGS, NO GUARDRAIL YET");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Green($"Agent summary: {responseB.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING: [AUDIT PRE] and [AUDIT POST] bracket every claim run.");
        Output.Yellow("The post-run log fires AFTER all tool calls complete — that's the full agent turn.");
        Output.Yellow("High-value claims still reach the LLM. V3 adds the guardrail.");
    }
}
