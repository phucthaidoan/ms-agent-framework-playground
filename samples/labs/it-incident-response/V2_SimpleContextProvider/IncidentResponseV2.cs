// V2: Simple AIContextProvider — Per-Session Incident Log
//
// NEW CONCEPTS:
//   - ProvideAIContextAsync   — inject confirmed facts as instructions BEFORE each LLM call
//   - StoreAIContextAsync     — extract confirmed root cause from agent response AFTER each call
//   - ProviderSessionState<T> — typed IncidentLog stored per-session; provider instance is shared
//
// WHAT THIS FIXES (vs V1):
//   Turn 3 no longer asks "what were the original symptoms?" — the confirmed root cause from
//   turn 2 is injected as an instruction every subsequent turn. The agent never forgets.
//
// WHAT'S STILL MISSING:
//   RunbookSearchProvider is not yet context-aware. SearchRunbook is still a manual tool call.
//   V3 adds InvokingCoreAsync to automatically retrieve runbook entries from the message stream.

using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.ItIncidentResponse.V2_SimpleContextProvider;

public static class IncidentResponseV2
{
    // ── Shared data (identical to V1) ────────────────────────────────────────
    private record ServiceStatus(string Name, string Status, string LastError);

    private static readonly Dictionary<string, ServiceStatus> Services = new()
    {
        ["APIGateway"]        = new("APIGateway",        "degraded",  "OOM crash, heap dump at 03:14 UTC"),
        ["AuthService"]       = new("AuthService",       "healthy",   "none"),
        ["PaymentProcessor"]  = new("PaymentProcessor",  "healthy",   "none"),
        ["NotificationWorker"]= new("NotificationWorker","degraded",  "queue depth 500, no emails sent since 02:30 UTC"),
        ["DataSync"]          = new("DataSync",          "healthy",   "none"),
    };

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

    // ── Tools (identical to V1) ──────────────────────────────────────────────

    [Description("Get the current health status of a named service.")]
    private static string GetServiceStatus(
        [Description("Service name: APIGateway, AuthService, PaymentProcessor, NotificationWorker, DataSync")]
        string serviceName)
    {
        if (Services.TryGetValue(serviceName, out ServiceStatus? svc))
        {
            Output.Gray($"  [TOOL] GetServiceStatus({serviceName}) → {svc.Status}");
            return $"Service={svc.Name}, Status={svc.Status}, LastError={svc.LastError}";
        }
        return $"Service '{serviceName}' not found.";
    }

    [Description("Search the runbook for past incidents matching the given symptom keywords.")]
    private static string SearchRunbook(
        [Description("Keywords describing the symptoms")]
        string keywords)
    {
        string[] terms = keywords.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<RunbookEntry> hits = Runbook
            .Where(e =>
            {
                string h = $"{e.Service} {e.Symptoms} {e.RootCause} {string.Join(" ", e.Tags)}".ToLowerInvariant();
                return terms.Any(t => h.Contains(t));
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
            $"[{h.Id}] Service: {h.Service}\nSymptoms: {h.Symptoms}\nRoot cause: {h.RootCause}\nResolution: {h.Resolution}"));
    }

    [Description("Archive a resolved incident to the runbook for future reference.")]
    private static string ArchiveResolution(
        [Description("Service name")] string serviceName,
        [Description("Symptom description")] string symptoms,
        [Description("Confirmed root cause")] string rootCause,
        [Description("Resolution steps")] string resolution)
    {
        string id = $"RB-NEW-{DateTime.UtcNow:HHmmss}";
        Output.Gray($"  [TOOL] ArchiveResolution({id}) for {serviceName}");
        Output.Yellow($"  [V2 NOTE] Still not persisted — V3 adds permanent storage via InvokedCoreAsync.");
        return $"Resolution archived as {id} (in-memory only).";
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("IT Incident Response V2 — Simple AIContextProvider (Per-Session Incident Log)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // KEY: Single provider INSTANCE shared across all sessions.
        // Session-specific state lives in ProviderSessionState<IncidentLog> — NOT in this instance.
        IncidentContextProvider incidentProvider = new();

        AIAgent agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsIChatClient()
            .AsAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new()
                {
                    Instructions =
                        "You are an IT incident response agent. " +
                        "For every incident: call GetServiceStatus, call SearchRunbook with symptom keywords, " +
                        "diagnose the root cause, provide remediation steps. " +
                        "When the engineer confirms a root cause, acknowledge it clearly with the phrase " +
                        "'ROOT CAUSE CONFIRMED:' followed by the cause. " +
                        "When a resolution is complete, call ArchiveResolution. Be concise.",
                    Tools =
                    [
                        AIFunctionFactory.Create(GetServiceStatus,  name: nameof(GetServiceStatus)),
                        AIFunctionFactory.Create(SearchRunbook,     name: nameof(SearchRunbook)),
                        AIFunctionFactory.Create(ArchiveResolution, name: nameof(ArchiveResolution)),
                    ]
                },
                AIContextProviders = [incidentProvider]
            });

        // ── Scenario A — Confirmed fact injection ────────────────────────────
        Output.Yellow("SCENARIO A — Confirmed fact injection (3-turn session)");
        Output.Separator(false);

        AgentSession sessionA = await agent.CreateSessionAsync();

        const string aTurn1 = "APIGateway is crashing with OOM errors. Heap dump shows a large request buffer on the /upload route.";
        Output.Gray($"Turn 1: {aTurn1}");
        AgentResponse aR1 = await agent.RunAsync(aTurn1, sessionA);
        Output.Green($"Agent: {aR1.Text}");
        PrintIncidentLog(incidentProvider, sessionA, "after turn 1");
        Console.WriteLine();

        const string aTurn2 = "Yes, that was it — MaxRequestBodySize was the fix. We deployed the hotfix and the OOM stopped.";
        Output.Gray($"Turn 2: {aTurn2}");
        AgentResponse aR2 = await agent.RunAsync(aTurn2, sessionA);
        Output.Green($"Agent: {aR2.Text}");
        PrintIncidentLog(incidentProvider, sessionA, "after turn 2");
        Console.WriteLine();

        // Turn 3: agent should recall the confirmed root cause from injected instructions — NOT re-diagnose
        const string aTurn3 = "What was the root cause we confirmed for this incident?";
        Output.Gray($"Turn 3: {aTurn3}");
        AgentResponse aR3 = await agent.RunAsync(aTurn3, sessionA);
        Output.Green($"Agent: {aR3.Text}");
        Output.Gray("(Agent answered from ProvideAIContextAsync-injected instructions — not from LLM memory)");
        Output.Separator();

        // ── Scenario B — Session isolation ──────────────────────────────────
        Output.Yellow("SCENARIO B — Session isolation (two parallel sessions, same provider instance)");
        Output.Separator(false);
        Output.Gray("Same IncidentContextProvider instance — different AgentSession objects.");

        AgentSession sessionAlice = await agent.CreateSessionAsync();
        AgentSession sessionBob   = await agent.CreateSessionAsync();

        // Alice's session
        const string aliceTurn1 = "AuthService is showing high CPU on /token endpoint. Redis cache might be evicting entries.";
        Output.Gray($"[Alice] Turn 1: {aliceTurn1}");
        await agent.RunAsync(aliceTurn1, sessionAlice);
        const string aliceTurn2 = "ROOT CAUSE CONFIRMED: Redis cache eviction forcing bcrypt rehash on every request.";
        Output.Gray($"[Alice] Turn 2: {aliceTurn2}");
        await agent.RunAsync(aliceTurn2, sessionAlice);

        // Bob's concurrent session
        const string bobTurn1 = "NotificationWorker has 500 emails queued since 02:30 UTC. No SMTP errors in logs.";
        Output.Gray($"[Bob]   Turn 1: {bobTurn1}");
        await agent.RunAsync(bobTurn1, sessionBob);
        const string bobTurn2 = "ROOT CAUSE CONFIRMED: SMTP credentials were rotated but env var NOTIFICATION__SmtpPassword was not updated.";
        Output.Gray($"[Bob]   Turn 2: {bobTurn2}");
        await agent.RunAsync(bobTurn2, sessionBob);

        Console.WriteLine();
        Output.Blue("Session isolation check:");
        PrintIncidentLog(incidentProvider, sessionAlice, "Alice's session");
        PrintIncidentLog(incidentProvider, sessionBob,   "Bob's session");
        Output.Gray("(Each session has independent IncidentLog — provider instance is shared, state is not)");
        Output.Separator();

        // ── Scenario C — Audit log grows turn-by-turn ───────────────────────
        Output.Yellow("SCENARIO C — Audit log grows turn-by-turn (3-turn session)");
        Output.Separator(false);

        AgentSession sessionC = await agent.CreateSessionAsync();

        string[] cTurns =
        [
            "DataSync jobs are timing out at 30s. Partial data in DB.",
            "ROOT CAUSE CONFIRMED: missing index on foreign key causing full table scan.",
            "Migration 0047_add_sync_fk_index.sql applied. Jobs completing now. Incident resolved.",
        ];

        for (int i = 0; i < cTurns.Length; i++)
        {
            Output.Gray($"Turn {i + 1}: {cTurns[i]}");
            await agent.RunAsync(cTurns[i], sessionC);
            IncidentLog log = incidentProvider.GetLog(sessionC);
            Output.Blue($"  IncidentLog size after turn {i + 1}: {log.Entries.Count} entries");
        }

        Output.Gray("(StoreAIContextAsync appends one entry per turn that contains a confirmed fact)");
        Output.Separator();

        // ── Scenario D — Failed call: StoreAIContextAsync skips ─────────────
        Output.Yellow("SCENARIO D — Cancelled call: StoreAIContextAsync is skipped on failure");
        Output.Separator(false);

        AgentSession sessionD = await agent.CreateSessionAsync();

        // Use a CancellationToken that cancels immediately to simulate a failed RunAsync
        using CancellationTokenSource cts = new();
        cts.Cancel();

        int logSizeBefore = incidentProvider.GetLog(sessionD).Entries.Count;
        Output.Gray($"Log size before cancelled call: {logSizeBefore}");

        try
        {
            await agent.RunAsync("This call will be cancelled immediately.", sessionD, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            Output.Gray("  RunAsync was cancelled (as expected).");
        }

        int logSizeAfter = incidentProvider.GetLog(sessionD).Entries.Count;
        Output.Blue($"Log size after cancelled call: {logSizeAfter} (unchanged — StoreAIContextAsync was skipped)");
        Output.Separator();

        Output.Yellow("KEY LEARNING: ProvideAIContextAsync injects confirmed facts every turn (agent never forgets).");
        Output.Gray("StoreAIContextAsync extracts and persists facts after each turn.");
        Output.Gray("ProviderSessionState<IncidentLog> isolates each engineer's session.");
        Output.Gray("V3 adds InvokingCoreAsync to automatically retrieve runbook entries.");
    }

    private static void PrintIncidentLog(IncidentContextProvider provider, AgentSession session, string label)
    {
        IncidentLog log = provider.GetLog(session);
        if (log.Entries.Count == 0)
        {
            Output.Gray($"  [IncidentLog — {label}] (empty — no confirmed facts yet)");
            return;
        }
        Output.Blue($"  [IncidentLog — {label}] {log.Entries.Count} entry/entries:");
        foreach (string entry in log.Entries)
            Output.Blue($"    • {entry}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IncidentContextProvider
//
// KEY CONCEPTS:
//   ProvideAIContextAsync — called BEFORE each LLM call. Injects all confirmed facts
//     from this session as additional Instructions so the agent always knows what
//     has been diagnosed, even if early turns were compacted away.
//
//   StoreAIContextAsync — called AFTER each successful LLM call. Scans the agent's
//     response for "ROOT CAUSE CONFIRMED:" phrases and stores them in IncidentLog.
//
//   ProviderSessionState<IncidentLog> — the log is stored inside the AgentSession,
//     NOT in this instance. This means the same provider instance can safely serve
//     concurrent on-call sessions simultaneously.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class IncidentContextProvider : AIContextProvider
{
    // KEY: ProviderSessionState stores IncidentLog inside the AgentSession.
    // The provider instance itself holds no session-specific data.
    private readonly ProviderSessionState<IncidentLog> _sessionState = new(
        stateInitializer: _ => new IncidentLog(),
        stateKey: nameof(IncidentContextProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // BEFORE each LLM call: inject all confirmed facts as agent instructions
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        IncidentLog log = _sessionState.GetOrInitializeState(context.Session);

        if (log.Entries.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        string injected =
            "INCIDENT LOG — confirmed facts from this session (do not re-diagnose these):\n" +
            string.Join("\n", log.Entries.Select((e, i) => $"  {i + 1}. {e}"));

        Output.Blue($"  [ProvideAIContextAsync] Injecting {log.Entries.Count} confirmed fact(s).");

        return new ValueTask<AIContext>(new AIContext { Instructions = injected });
    }

    // AFTER each successful LLM call: extract confirmed root causes from the response
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        IncidentLog log = _sessionState.GetOrInitializeState(context.Session);

        // Scan agent response for explicit confirmations
        foreach (ChatMessage msg in context.ResponseMessages ?? [])
        {
            string text = msg.Text ?? string.Empty;
            int idx = text.IndexOf("ROOT CAUSE CONFIRMED:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string confirmed = text[(idx + "ROOT CAUSE CONFIRMED:".Length)..].Trim();
                // Take only the first sentence/line
                int end = confirmed.IndexOfAny(['.', '\n']);
                if (end > 0) confirmed = confirmed[..end].Trim();
                if (!string.IsNullOrWhiteSpace(confirmed) && !log.Entries.Contains(confirmed))
                {
                    log.Entries.Add(confirmed);
                    Output.Blue($"  [StoreAIContextAsync] Stored confirmed fact: \"{confirmed}\"");
                }
            }
        }

        // Also scan user input for explicit "ROOT CAUSE CONFIRMED:" statements
        foreach (ChatMessage msg in context.RequestMessages)
        {
            string text = msg.Text ?? string.Empty;
            int idx = text.IndexOf("ROOT CAUSE CONFIRMED:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string confirmed = text[(idx + "ROOT CAUSE CONFIRMED:".Length)..].Trim();
                int end = confirmed.IndexOfAny(['.', '\n']);
                if (end > 0) confirmed = confirmed[..end].Trim();
                if (!string.IsNullOrWhiteSpace(confirmed) && !log.Entries.Contains(confirmed))
                {
                    log.Entries.Add(confirmed);
                    Output.Blue($"  [StoreAIContextAsync] Stored confirmed fact from engineer: \"{confirmed}\"");
                }
            }
        }

        _sessionState.SaveState(context.Session, log);
        return default;
    }

    // Helper for display — not part of the provider contract
    public IncidentLog GetLog(AgentSession session) => _sessionState.GetOrInitializeState(session);
}

// ── Session state type ────────────────────────────────────────────────────────
public sealed class IncidentLog
{
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = [];
}
