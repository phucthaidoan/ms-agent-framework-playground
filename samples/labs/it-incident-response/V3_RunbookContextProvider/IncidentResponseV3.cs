// V3: Advanced AIContextProvider — RunbookContextProvider + Source Stamping
//
// NEW CONCEPTS:
//   - InvokingCoreAsync        — override to intercept the full assembled message list BEFORE the LLM call.
//                                Filter to External-sourced messages only, run runbook search, inject top-3
//                                past incidents as stamped context messages.
//   - InvokedCoreAsync         — override to archive a confirmed resolution AFTER the LLM call.
//                                Only fires on successful calls (InvokeException is null).
//   - WithAgentRequestMessageSource / GetAgentRequestMessageSourceType
//                              — stamp injected messages as AIContextProvider so the NEXT turn's
//                                InvokingCoreAsync skips them (prevents retrieval feedback loops).
//
// WHAT THIS FIXES (vs V2):
//   The manual SearchRunbook tool call is REMOVED. RunbookContextProvider now intercepts every turn,
//   searches the runbook automatically using the engineer's symptom text (External messages only),
//   and injects the top-3 hits as context. The engineer never has to remember to call the tool.
//
// WHAT'S STILL MISSING:
//   Long incident sessions (15+ turns) exhaust the context window. V4 adds CompactionProvider.

using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.ItIncidentResponse.V3_RunbookContextProvider;

public static class IncidentResponseV3
{
    // ── Shared data ──────────────────────────────────────────────────────────
    private record ServiceStatus(string Name, string Status, string LastError);

    private static readonly Dictionary<string, ServiceStatus> Services = new()
    {
        ["APIGateway"]        = new("APIGateway",        "degraded",  "OOM crash, heap dump at 03:14 UTC"),
        ["AuthService"]       = new("AuthService",       "healthy",   "none"),
        ["PaymentProcessor"]  = new("PaymentProcessor",  "healthy",   "none"),
        ["NotificationWorker"]= new("NotificationWorker","degraded",  "queue depth 500, no emails sent since 02:30 UTC"),
        ["DataSync"]          = new("DataSync",          "healthy",   "none"),
    };

    // Runbook is a SHARED mutable list — new entries from ArchiveResolution/InvokedCoreAsync are
    // immediately visible to all sessions (shared institutional knowledge).
    internal static readonly List<RunbookEntry> Runbook =
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

    // ── Tools — SearchRunbook is REMOVED; provider handles retrieval automatically ──

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

    // ArchiveResolution is kept as a tool for V3 but now also backed by InvokedCoreAsync
    [Description("Archive a resolved incident to the shared runbook for future reference.")]
    private static string ArchiveResolution(
        [Description("Service name")] string serviceName,
        [Description("Symptom description")] string symptoms,
        [Description("Confirmed root cause")] string rootCause,
        [Description("Resolution steps")] string resolution)
    {
        string id = $"RB-{(Runbook.Count + 1):D3}";
        RunbookEntry entry = new(id, serviceName, symptoms, rootCause, resolution,
            [serviceName.ToLowerInvariant(), .. rootCause.ToLowerInvariant().Split(' ').Take(3)]);
        Runbook.Add(entry);
        Output.Green($"  [TOOL] ArchiveResolution({id}) → added to shared runbook (now {Runbook.Count} entries)");
        return $"Resolution archived as {id} and added to shared runbook.";
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("IT Incident Response V3 — Advanced AIContextProvider (InvokingCoreAsync + InvokedCoreAsync)");
        Output.Separator();

        Output.Gray($"Runbook size at start: {Runbook.Count} entries");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // KEY: Two providers — registered in order. RunbookContextProvider fires first (retrieval),
        // then IncidentContextProvider (session fact injection).
        // Order matters: each provider's injected messages are stamped AIContextProvider so
        // the NEXT turn's retrieval scan skips them.
        RunbookContextProvider runbookProvider  = new(Runbook);
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
                        "Runbook entries relevant to the current incident are injected automatically as context — use them. " +
                        "Call GetServiceStatus to verify current service health. " +
                        "Diagnose the root cause and provide remediation steps. " +
                        "When a root cause is confirmed, acknowledge with 'ROOT CAUSE CONFIRMED:' followed by the cause. " +
                        "When an incident is fully resolved, call ArchiveResolution. Be concise.",
                    Tools =
                    [
                        AIFunctionFactory.Create(GetServiceStatus,  name: nameof(GetServiceStatus)),
                        AIFunctionFactory.Create(ArchiveResolution, name: nameof(ArchiveResolution)),
                    ]
                },
                AIContextProviders = [runbookProvider, incidentProvider]
            });

        // ── Scenario A — Automatic retrieval keyed to engineer input ─────────
        Output.Yellow("SCENARIO A — Automatic runbook retrieval (no SearchRunbook tool call needed)");
        Output.Separator(false);

        AgentSession sessionA = await agent.CreateSessionAsync();

        const string aMsg = "Notification emails stuck in queue, queue depth at 500 and rising. No errors in SMTP logs.";
        Output.Gray($"Engineer: {aMsg}");
        Console.WriteLine();
        AgentResponse aResp = await agent.RunAsync(aMsg, sessionA);
        Output.Green($"Agent: {aResp.Text}");
        Output.Gray("(RunbookContextProvider injected RB-004 automatically — no SearchRunbook tool call)");
        Output.Separator();

        // ── Scenario B — Feedback-loop prevention ───────────────────────────
        Output.Yellow("SCENARIO B — Feedback-loop prevention (source stamping)");
        Output.Separator(false);
        Output.Gray("Turn 1: describe OOM symptoms → RB-003 injected and stamped AIContextProvider.");
        Output.Gray("Turn 2: vague follow-up → InvokingCoreAsync skips stamped messages, no duplicate injection.");

        AgentSession sessionB = await agent.CreateSessionAsync();

        const string bTurn1 = "APIGateway is crashing with OOM errors. Heap dump shows large request buffer.";
        Output.Gray($"Turn 1: {bTurn1}");
        await agent.RunAsync(bTurn1, sessionB);
        Console.WriteLine();

        const string bTurn2 = "Is there anything else we should check on the gateway?";
        Output.Gray($"Turn 2: {bTurn2}");
        Output.Gray("(Watch InvokingCoreAsync output — should show RB-003 skipped as already-injected)");
        AgentResponse bR2 = await agent.RunAsync(bTurn2, sessionB);
        Output.Green($"Agent: {bR2.Text}");
        Output.Separator();

        // ── Scenario C — New entry archived and retrieved in next session ────
        Output.Yellow("SCENARIO C — New runbook entry archived then retrieved by different session");
        Output.Separator(false);

        AgentSession sessionC1 = await agent.CreateSessionAsync();
        int runbookBefore = Runbook.Count;
        Output.Blue($"Runbook size before: {runbookBefore}");

        const string cNovel =
            "DataSync is crashing on null foreign key reference. No migration exists for this. " +
            "Adding a null check in the DataSync worker fixed the crash.";
        Output.Gray($"Engineer (session 1): {cNovel}");
        const string cConfirm = "ROOT CAUSE CONFIRMED: null FK reference in DataSync worker, missing null guard. Resolution: added null check before FK lookup.";
        Output.Gray($"Engineer (session 1): {cConfirm}");
        await agent.RunAsync(cNovel, sessionC1);
        await agent.RunAsync(cConfirm, sessionC1);

        int runbookAfter = Runbook.Count;
        Output.Blue($"Runbook size after session 1 resolved incident: {runbookAfter}");

        // New session — different engineer, same symptoms
        AgentSession sessionC2 = await agent.CreateSessionAsync();
        const string cSession2 = "DataSync crashing on null foreign key reference. No stack trace, just a NullReferenceException in the FK lookup.";
        Output.Gray($"Engineer (session 2): {cSession2}");
        AgentResponse cR2 = await agent.RunAsync(cSession2, sessionC2);
        Output.Green($"Agent: {cR2.Text}");
        Output.Gray("(RunbookContextProvider retrieved the just-archived entry for the new session)");
        Output.Separator();

        // ── Scenario D — Deduplication across turns ──────────────────────────
        Output.Yellow("SCENARIO D — Deduplication (same runbook entry not injected twice)");
        Output.Separator(false);

        AgentSession sessionD = await agent.CreateSessionAsync();

        const string dTurn1 = "APIGateway OOM crash again. Same heap dump pattern — large buffers on /upload.";
        Output.Gray($"Turn 1: {dTurn1}");
        Output.Gray("(RB-003 should be injected and logged in AlreadyInjectedIds)");
        await agent.RunAsync(dTurn1, sessionD);
        Console.WriteLine();

        const string dTurn2 = "Still seeing OOM. More heap dumps with large request buffers on /upload route.";
        Output.Gray($"Turn 2: {dTurn2} (same symptom phrasing — RB-003 should be skipped)");
        Output.Gray("(Watch: '(RB-xxx already injected this session — skipped)' in output)");
        AgentResponse dR2 = await agent.RunAsync(dTurn2, sessionD);
        Output.Green($"Agent: {dR2.Text}");
        Output.Separator();

        Output.Yellow("KEY LEARNING:");
        Output.Gray("  InvokingCoreAsync — full message list access, source filtering, merges complete AIContext.");
        Output.Gray("  InvokedCoreAsync  — post-call archival, skips on InvokeException.");
        Output.Gray("  Source stamping   — injected messages marked AIContextProvider; next turn skips them.");
        Output.Gray("  V4 adds CompactionProvider for long (15-turn) incident sessions.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RunbookContextProvider
//
// Overrides InvokingCoreAsync and InvokedCoreAsync (advanced pattern).
//
// InvokingCoreAsync:
//   1. Receives the FULL assembled message list (context.AIContext.Messages).
//   2. Filters to External-sourced messages only (engineer's actual input).
//   3. Searches runbook with the filtered text.
//   4. Filters results against AlreadyInjectedIds (deduplication).
//   5. Stamps new entries as AIContextProvider source.
//   6. Returns merged AIContext (full list + new stamped entries).
//
// InvokedCoreAsync:
//   1. Checks InvokeException — skips archival on failure.
//   2. Extracts External request messages (symptoms) + response (resolution).
//   3. If resolution contains "ROOT CAUSE CONFIRMED:", archives new runbook entry.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunbookContextProvider : AIContextProvider
{
    private readonly List<RunbookEntry> _runbook;

    // Per-session: tracks which runbook IDs have already been injected this session
    private readonly ProviderSessionState<InjectionState> _sessionState = new(
        stateInitializer: _ => new InjectionState(),
        stateKey: nameof(RunbookContextProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    public RunbookContextProvider(List<RunbookEntry> runbook) : base(null, null)
    {
        _runbook = runbook;
    }

    // KEY: Override InvokingCoreAsync — NOT ProvideAIContextAsync — because we need:
    //   1. The full context.AIContext.Messages list to check already-injected IDs
    //   2. To return a MERGED AIContext (not just an additive delta)
    // ProvideAIContextAsync only returns a delta; the base class appends it blindly.
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        InjectionState state = _sessionState.GetOrInitializeState(context.Session);

        // STEP 1: Filter to External messages only — skip AIContextProvider-stamped and ChatHistory messages.
        // KEY: This prevents the provider from searching its OWN injected runbook text from prior turns.
        IEnumerable<ChatMessage> externalMessages = (context.AIContext.Messages ?? [])
            .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        string searchQuery = string.Join(" ", externalMessages.Select(m => m.Text ?? string.Empty)).Trim();

        List<ChatMessage> newInjections = [];

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            // STEP 2: Search runbook with the engineer's actual symptom text
            string[] terms = searchQuery.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            List<RunbookEntry> hits = _runbook
                .Where(e =>
                {
                    string h = $"{e.Service} {e.Symptoms} {e.RootCause} {string.Join(" ", e.Tags)}".ToLowerInvariant();
                    return terms.Any(t => h.Contains(t));
                })
                .Take(3)
                .ToList();

            foreach (RunbookEntry hit in hits)
            {
                // STEP 3: Deduplication — skip entries already injected this session
                if (state.AlreadyInjectedIds.Contains(hit.Id))
                {
                    Output.Gray($"  [RunbookContextProvider] ({hit.Id} already injected this session — skipped)");
                    continue;
                }

                string entryText =
                    $"[RUNBOOK {hit.Id}] Service: {hit.Service}\n" +
                    $"Symptoms: {hit.Symptoms}\n" +
                    $"Root cause: {hit.RootCause}\n" +
                    $"Resolution: {hit.Resolution}";

                // STEP 4: Stamp the message as AIContextProvider so the NEXT turn's filter skips it
                ChatMessage stamped = new ChatMessage(ChatRole.User, entryText)
                    .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!);

                newInjections.Add(stamped);
                state.AlreadyInjectedIds.Add(hit.Id);

                Output.Blue($"  [RunbookContextProvider] Injecting {hit.Id} ({hit.Service}) — stamped AIContextProvider");
            }

            if (newInjections.Count > 0)
                _sessionState.SaveState(context.Session, state);
        }

        // STEP 5: Return the COMPLETE merged AIContext.
        // Unlike ProvideAIContextAsync (additive delta), InvokingCoreAsync returns the full list.
        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages     = (context.AIContext.Messages ?? []).Concat(newInjections),
            Tools        = context.AIContext.Tools
        });
    }

    // KEY: Override InvokedCoreAsync — fires after EACH successful LLM call.
    // If the agent confirmed a root cause, parse and archive a new runbook entry.
    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        // STEP 1: Skip archival if the LLM call failed
        if (context.InvokeException is not null)
            return default;

        // STEP 2: Look for ROOT CAUSE CONFIRMED in External request messages (engineer's words)
        string? confirmedCause = null;
        string? serviceHint = null;
        string? symptomsHint = null;

        foreach (ChatMessage msg in context.RequestMessages)
        {
            if (msg.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.External)
                continue;

            string text = msg.Text ?? string.Empty;
            int idx = text.IndexOf("ROOT CAUSE CONFIRMED:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                confirmedCause = text[(idx + "ROOT CAUSE CONFIRMED:".Length)..].Trim();
                int end = confirmedCause.IndexOfAny(['.', '\n']);
                if (end > 0) confirmedCause = confirmedCause[..end].Trim();
                symptomsHint = text[..idx].Trim();
            }
        }

        if (confirmedCause is null) return default;

        // STEP 3: Extract resolution from the agent's response
        string resolution = string.Join(" ", (context.ResponseMessages ?? [])
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text ?? string.Empty));

        if (string.IsNullOrWhiteSpace(resolution)) return default;

        // STEP 4: Archive the new entry into the shared runbook
        string id = $"RB-{(_runbook.Count + 1):D3}";
        RunbookEntry newEntry = new(
            id,
            serviceHint ?? "Unknown",
            symptomsHint ?? confirmedCause,
            confirmedCause,
            resolution.Length > 200 ? resolution[..200] : resolution,
            confirmedCause.ToLowerInvariant().Split(' ').Take(4).ToArray());

        _runbook.Add(newEntry);
        Output.Green($"  [InvokedCoreAsync] Archived new runbook entry: {id} — \"{confirmedCause}\"");
        Output.Green($"  [InvokedCoreAsync] Runbook now has {_runbook.Count} entries.");

        return default;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IncidentContextProvider (identical contract to V2 — no changes to API surface)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class IncidentContextProvider : AIContextProvider
{
    private readonly ProviderSessionState<IncidentLog> _sessionState = new(
        stateInitializer: _ => new IncidentLog(),
        stateKey: nameof(IncidentContextProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        IncidentLog log = _sessionState.GetOrInitializeState(context.Session);

        if (log.Entries.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        string injected =
            "INCIDENT LOG — confirmed facts this session (do not re-diagnose):\n" +
            string.Join("\n", log.Entries.Select((e, i) => $"  {i + 1}. {e}"));

        Output.Blue($"  [IncidentContextProvider] Injecting {log.Entries.Count} confirmed fact(s).");
        return new ValueTask<AIContext>(new AIContext { Instructions = injected });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        IncidentLog log = _sessionState.GetOrInitializeState(context.Session);

        foreach (ChatMessage msg in context.ResponseMessages ?? [])
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
                    Output.Blue($"  [IncidentContextProvider] Stored confirmed fact: \"{confirmed}\"");
                }
            }
        }

        foreach (ChatMessage msg in context.RequestMessages)
        {
            if (msg.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.External)
                continue;
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
                    Output.Blue($"  [IncidentContextProvider] Stored confirmed fact (from engineer): \"{confirmed}\"");
                }
            }
        }

        _sessionState.SaveState(context.Session, log);
        return default;
    }
}

// ── Shared types ─────────────────────────────────────────────────────────────

public record RunbookEntry(string Id, string Service, string Symptoms, string RootCause, string Resolution, string[] Tags);

public sealed class InjectionState
{
    [JsonPropertyName("alreadyInjectedIds")]
    public HashSet<string> AlreadyInjectedIds { get; set; } = [];
}

public sealed class IncidentLog
{
    [JsonPropertyName("entries")]
    public List<string> Entries { get; set; } = [];
}
