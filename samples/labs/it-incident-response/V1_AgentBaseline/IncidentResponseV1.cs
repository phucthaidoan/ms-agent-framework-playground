// V1: Bare Agent with Function Tools
//
// NEW CONCEPT: Function Tools (AIFunctionFactory.Create)
//
// The agent has three tools:
//   - GetServiceStatus:  returns current health of each service (hardcoded)
//   - SearchRunbook:     keyword search across 6 seeded past incidents
//   - ArchiveResolution: records a resolved incident (prints to console — not persisted in V1)
//
// Without tools the agent would guess service health and invent remediation steps.
//
// WHAT'S MISSING: No context provider. Each turn starts cold — the agent has no memory
// of what was confirmed in prior turns. A 3-turn incident session demonstrating this
// amnesia is the teaching moment that makes V2's ProvideAIContextAsync feel inevitable.

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.ItIncidentResponse.V1_AgentBaseline;

public static class IncidentResponseV1
{
    // ── Hardcoded service status ─────────────────────────────────────────────
    private record ServiceStatus(string Name, string Status, string LastError);

    private static readonly Dictionary<string, ServiceStatus> Services = new()
    {
        ["APIGateway"]        = new("APIGateway",        "degraded",  "OOM crash, heap dump at 03:14 UTC"),
        ["AuthService"]       = new("AuthService",       "healthy",   "none"),
        ["PaymentProcessor"]  = new("PaymentProcessor",  "healthy",   "none"),
        ["NotificationWorker"]= new("NotificationWorker","degraded",  "queue depth 500, no emails sent since 02:30 UTC"),
        ["DataSync"]          = new("DataSync",          "healthy",   "none"),
    };

    // ── Hardcoded runbook (6 seeded past incidents) ──────────────────────────
    private record RunbookEntry(string Id, string Service, string Symptoms, string RootCause, string Resolution, string[] Tags);

    private static readonly List<RunbookEntry> Runbook =
    [
        new("RB-001", "AuthService",        "JWT validation failing, 401 on all requests",
            "expired signing certificate",
            "rotate cert via `auth-admin rotate-cert`, redeploy AuthService",
            ["auth", "jwt", "certificate"]),

        new("RB-002", "PaymentProcessor",   "Stripe webhook timeout, payment stuck pending",
            "outbound HTTP client pool exhausted",
            "increase HttpClientFactory pool size in appsettings, restart service",
            ["payment", "http", "timeout"]),

        new("RB-003", "APIGateway",         "OOM crash, heap dump shows large request buffer",
            "unbounded request body buffering on /upload route",
            "add MaxRequestBodySize limit in Startup.cs, deploy hotfix",
            ["oom", "memory", "gateway"]),

        new("RB-004", "NotificationWorker", "Emails queued but not sent, queue depth rising",
            "SMTP credentials rotated but env var not updated",
            "update NOTIFICATION__SmtpPassword secret, restart worker",
            ["notification", "smtp", "credentials"]),

        new("RB-005", "DataSync",           "Sync jobs timing out after 30s, partial data in DB",
            "missing index on foreign key causing full table scan",
            "run migration 0047_add_sync_fk_index.sql, no restart needed",
            ["datasync", "database", "timeout", "index"]),

        new("RB-006", "AuthService",        "High latency on /token endpoint, CPU spike",
            "Redis cache eviction causing bcrypt rehash on every request",
            "increase Redis maxmemory, flush expired keys with `redis-cli FLUSHDB`",
            ["auth", "redis", "latency", "cache"]),
    ];

    // ── Tools ────────────────────────────────────────────────────────────────

    [Description("Get the current health status of a named service.")]
    private static string GetServiceStatus(
        [Description("Service name: APIGateway, AuthService, PaymentProcessor, NotificationWorker, DataSync")]
        string serviceName)
    {
        if (Services.TryGetValue(serviceName, out ServiceStatus? svc))
        {
            Output.Gray($"  [TOOL] GetServiceStatus({serviceName}) → {svc.Status}, lastError='{svc.LastError}'");
            return $"Service={svc.Name}, Status={svc.Status}, LastError={svc.LastError}";
        }
        Output.Gray($"  [TOOL] GetServiceStatus({serviceName}) → not found");
        return $"Service '{serviceName}' not found. Known services: {string.Join(", ", Services.Keys)}";
    }

    [Description("Search the runbook for past incidents matching the given symptom keywords.")]
    private static string SearchRunbook(
        [Description("Keywords describing the symptoms, e.g. 'OOM gateway large request buffer'")]
        string keywords)
    {
        string[] terms = keywords.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<RunbookEntry> hits = Runbook
            .Where(e =>
            {
                string haystack = $"{e.Service} {e.Symptoms} {e.RootCause} {string.Join(" ", e.Tags)}".ToLowerInvariant();
                return terms.Any(t => haystack.Contains(t));
            })
            .Take(3)
            .ToList();

        if (hits.Count == 0)
        {
            Output.Gray($"  [TOOL] SearchRunbook(\"{keywords}\") → no matches");
            return "No matching runbook entries found.";
        }

        Output.Gray($"  [TOOL] SearchRunbook(\"{keywords}\") → {string.Join(", ", hits.Select(h => h.Id))}");
        return string.Join("\n\n", hits.Select(h =>
            $"[{h.Id}] Service: {h.Service}\n" +
            $"Symptoms: {h.Symptoms}\n" +
            $"Root cause: {h.RootCause}\n" +
            $"Resolution: {h.Resolution}"));
    }

    [Description("Archive a resolved incident to the runbook for future reference.")]
    private static string ArchiveResolution(
        [Description("Service name that was affected")] string serviceName,
        [Description("Symptom description")] string symptoms,
        [Description("Confirmed root cause")] string rootCause,
        [Description("Resolution steps taken")] string resolution)
    {
        // V1: prints but does NOT persist — this is the teaching gap for V3
        string id = $"RB-NEW-{DateTime.UtcNow:HHmmss}";
        Output.Gray($"  [TOOL] ArchiveResolution({id}) → {serviceName}: {rootCause}");
        Output.Yellow($"  [V1 NOTE] Archive called but NOT persisted — V3 adds permanent storage via InvokedCoreAsync.");
        return $"Resolution archived as {id} (in-memory only in V1 — not queryable in SearchRunbook).";
    }

    // ── Test scenarios ───────────────────────────────────────────────────────

    private const string ScenarioA =
        "Service: APIGateway\n" +
        "Symptom: The gateway is crashing with OOM errors. Engineers found a heap dump showing " +
        "a large request buffer on the /upload route. Service is currently degraded.";

    private const string ScenarioB =
        "Service: NotificationWorker\n" +
        "Symptom: Notification emails are stuck in the queue. Queue depth is at 500 and rising. " +
        "No emails have been sent since 02:30 UTC.";

    private const string ScenarioC =
        "Service: DataSync\n" +
        "Symptom: Sync jobs are hanging silently at exactly 30 seconds with no error messages, " +
        "then timing out. Partial data is appearing in the database.";

    // For Scenario D we simulate multi-turn with session
    private const string ScenarioD_Turn1 =
        "Service: AuthService\n" +
        "Symptom: We're seeing very high latency on the /token endpoint with a CPU spike. " +
        "Looks like every request is doing expensive computation.";
    private const string ScenarioD_Turn2 = "That didn't work. We tried restarting the service but latency is still high. What else should we check?";
    private const string ScenarioD_Turn3 = "What were the original symptoms we described at the start of this incident?";

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("IT Incident Response V1 — Function Tools (No Context Provider)");
        Output.Separator();

        Output.Gray("Runbook entries (what SearchRunbook will search):");
        foreach (RunbookEntry e in Runbook)
            Output.Gray($"  {e.Id}  [{e.Service,-20}] {e.Symptoms[..Math.Min(60, e.Symptoms.Length)]}...");
        Output.Separator();

        Output.Gray("Current service health:");
        foreach (ServiceStatus s in Services.Values)
        {
            ConsoleColor c = s.Status == "healthy" ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.ForegroundColor = c;
            Console.WriteLine($"  {s.Name,-22} {s.Status}");
            Console.ResetColor();
        }
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        AIAgent agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsIChatClient()
            .AsAIAgent(
                instructions:
                    "You are an IT incident response agent for a SaaS platform engineering team. " +
                    "For every incident: " +
                    "1. Call GetServiceStatus to verify current health. " +
                    "2. Call SearchRunbook with keywords from the symptom description to find similar past incidents. " +
                    "3. Diagnose the root cause based on the symptoms and runbook hits. " +
                    "4. Provide clear remediation steps. " +
                    "5. If a root cause is confirmed, call ArchiveResolution. " +
                    "Be concise — engineers are under pressure.",
                name: "IncidentResponseAgent",
                tools:
                [
                    AIFunctionFactory.Create(GetServiceStatus,    name: nameof(GetServiceStatus)),
                    AIFunctionFactory.Create(SearchRunbook,       name: nameof(SearchRunbook)),
                    AIFunctionFactory.Create(ArchiveResolution,   name: nameof(ArchiveResolution)),
                ]);

        // ── Scenario A — OOM crash (exact runbook match: RB-003) ────────────
        Output.Yellow("SCENARIO A — APIGateway OOM crash (expect: RB-003 match, MaxRequestBodySize fix)");
        Output.Gray(ScenarioA);
        Console.WriteLine();
        AgentResponse responseA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent: {responseA.Text}");
        Output.Separator();

        // ── Scenario B — Notification queue stuck (expect: RB-004 match) ───
        Output.Yellow("SCENARIO B — NotificationWorker queue stuck (expect: RB-004, SMTP credentials fix)");
        Output.Gray(ScenarioB);
        Console.WriteLine();
        AgentResponse responseB = await agent.RunAsync(ScenarioB);
        Output.Green($"Agent: {responseB.Text}");
        Output.Separator();

        // ── Scenario C — DataSync timeout (expect: RB-005 match) ────────────
        Output.Yellow("SCENARIO C — DataSync silent timeout (expect: RB-005, index migration fix)");
        Output.Gray(ScenarioC);
        Console.WriteLine();
        AgentResponse responseC = await agent.RunAsync(ScenarioC);
        Output.Green($"Agent: {responseC.Text}");
        Output.Separator();

        // ── Scenario D — Multi-turn WITHOUT session memory ───────────────────
        // KEY TEACHING MOMENT: agent is called with NO session — each turn is stateless.
        // Turn 3 asks "what were the original symptoms?" — agent cannot answer.
        // This is exactly what V2's ProvideAIContextAsync fixes.
        Output.Yellow("SCENARIO D — Multi-turn WITHOUT session (demonstrates V1 amnesia)");
        Output.Gray("Note: no AgentSession used — each RunAsync call is independent.");
        Output.Separator(false);

        Output.Gray($"Turn 1: {ScenarioD_Turn1}");
        Console.WriteLine();
        AgentResponse dTurn1 = await agent.RunAsync(ScenarioD_Turn1);
        Output.Green($"Agent Turn 1: {dTurn1.Text}");
        Console.WriteLine();

        Output.Gray($"Turn 2: {ScenarioD_Turn2}");
        Console.WriteLine();
        AgentResponse dTurn2 = await agent.RunAsync(ScenarioD_Turn2);
        Output.Green($"Agent Turn 2: {dTurn2.Text}");
        Console.WriteLine();

        Output.Gray($"Turn 3: {ScenarioD_Turn3}");
        Console.WriteLine();
        AgentResponse dTurn3 = await agent.RunAsync(ScenarioD_Turn3);
        Output.Green($"Agent Turn 3: {dTurn3.Text}");
        Console.WriteLine();

        Output.Gray("(Agent has no memory of turn 1 — this is the V1 gap that V2 fixes.)");
        Output.Separator();

        Output.Yellow("KEY LEARNING: Tools give the agent real data, but no session = no memory across turns.");
        Output.Gray("V2 adds AgentSession + ProvideAIContextAsync to inject confirmed facts every turn.");
    }
}
