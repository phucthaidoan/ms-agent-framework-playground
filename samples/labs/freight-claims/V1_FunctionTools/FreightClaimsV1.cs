// V1: Bare Agent with Function Tools
//
// NEW CONCEPT: Function Tools (AIFunctionFactory.Create)
//
// The agent calls two tools automatically:
//   - LookupShipment: retrieves real cargo type and declared value from an internal system
//   - ApproveClaim:   records the triage decision (approve / escalate / reject)
//
// Without tools the agent would guess cargo type from the claim text and produce wrong classifications.
//
// WHAT'S MISSING: No middleware yet. A $15,000 claim gets auto-approved with zero oversight.
// V2 adds audit logging. V3 adds the high-value guardrail.

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Labs.FreightClaims.V1_FunctionTools;

public static class FreightClaimsV1
{
    // ── Shipment data ────────────────────────────────────────────────────────
    private record ShipmentRecord(string ShipmentId, string CargoType, int DeclaredValue, string Origin, string Destination);

    private static readonly Dictionary<string, ShipmentRecord> Shipments = new()
    {
        ["SHP-1001"] = new("SHP-1001", "General goods",  450,    "Chicago",     "Detroit"),
        ["SHP-2002"] = new("SHP-2002", "Electronics",    15_000, "San Jose",    "Austin"),
        ["SHP-3003"] = new("SHP-3003", "Perishables",    2_500,  "Seattle",     "Portland"),
    };

    // ── Tools ────────────────────────────────────────────────────────────────
    [Description("Look up shipment details from the internal logistics system.")]
    private static string LookupShipment(
        [Description("The shipment ID, e.g. SHP-1001")] string shipmentId)
    {
        if (Shipments.TryGetValue(shipmentId, out var rec))
        {
            Output.Gray($"  [TOOL] LookupShipment({shipmentId}) → cargo={rec.CargoType}, value=${rec.DeclaredValue}, {rec.Origin}→{rec.Destination}");
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
        Output.Title("Freight Claims V1 — Function Tools (No Middleware)");
        Output.Separator();

        Output.Gray("Shipment database (what LookupShipment will return):");
        foreach (var (id, rec) in Shipments)
            Output.Gray($"  {id}  {rec.CargoType,-16} ${rec.DeclaredValue,6:N0}  {rec.Origin} → {rec.Destination}");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        AIAgent agent = new OpenAIClient(apiKey)
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

        // Scenario A — normal low-value claim (expected: approved)
        Output.Yellow("SCENARIO A — Normal low-value claim ($450, general goods)");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent summary: {responseA.Text}");
        Output.Separator();

        // Scenario B — high-value claim (expected in V1: auto-approved — no guardrail yet)
        Output.Yellow("SCENARIO B — High-value claim ($15,000 electronics) — NO GUARDRAIL IN V1");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Green($"Agent summary: {responseB.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING: The agent uses real shipment data via tools, but a $15k claim");
        Output.Yellow("gets auto-approved with zero oversight. V3 adds the value guardrail.");
    }
}
