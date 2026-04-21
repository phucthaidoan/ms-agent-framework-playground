#pragma warning disable MAAI001  // Compaction API is experimental

// V4: CompactionProvider — Bounded Context for Long Incident Sessions
//
// NEW CONCEPT: CompactionProvider + AsBuilder().UseAIContextProviders()
//
//   Real incidents involve 10–20 turns of log pasting and hypothesis testing.
//   Each turn appends the engineer's full log dump to history — context window fills fast.
//   CompactionProvider trims what the LLM SEES per call without deleting stored history.
//
// CRITICAL CONSTRUCTION ORDER:
//   Correct:   AsBuilder().UseAIContextProviders(compactionProvider, runbookProvider, incidentProvider)
//   Incorrect: ChatClientAgentOptions.AIContextProviders = [compactionProvider, ...]
//
//   CompactionProvider MUST be registered via AsBuilder().UseAIContextProviders() to run
//   INSIDE the tool-calling loop. If registered via AIContextProviders, it runs outside
//   the loop and does not compact tool-call message groups.
//
//   Provider execution order: compaction runs first → trimmed list → runbookProvider searches
//   trimmed External messages → incidentProvider injects confirmed facts.
//
// WHAT SURVIVES COMPACTION:
//   - IncidentLog in ProviderSessionState<T> — injected every turn by ProvideAIContextAsync.
//     The agent always knows confirmed root causes even when early turns are compacted away.
//   - Runbook archive — lives in the shared RunbookContextProvider instance (not in messages).
//     All archived entries remain searchable regardless of compaction.

using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.ItIncidentResponse.V4_Compaction;

public static class IncidentResponseV4
{
    // Low token threshold so compaction fires within a few turns in the demo
    private const int TruncationTokenThreshold = 800;
    private const int SlidingWindowTurns       = 4;

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
            Output.Gray($"  [TOOL] GetServiceStatus({serviceName}) → {svc.Status}");
            return $"Service={svc.Name}, Status={svc.Status}, LastError={svc.LastError}";
        }
        return $"Service '{serviceName}' not found.";
    }

    [Description("Archive a resolved incident to the shared runbook.")]
    private static string ArchiveResolution(
        [Description("Service name")] string serviceName,
        [Description("Symptom description")] string symptoms,
        [Description("Confirmed root cause")] string rootCause,
        [Description("Resolution steps")] string resolution)
    {
        string id = $"RB-{(Runbook.Count + 1):D3}";
        Runbook.Add(new RunbookEntry(id, serviceName, symptoms, rootCause, resolution,
            [serviceName.ToLowerInvariant(), .. rootCause.ToLowerInvariant().Split(' ').Take(3)]));
        Output.Green($"  [TOOL] ArchiveResolution({id}) — runbook now {Runbook.Count} entries");
        return $"Archived as {id}.";
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("IT Incident Response V4 — CompactionProvider (Bounded Context)");
        Output.Separator();

        Output.Yellow("Compaction config for this demo:");
        Output.Gray($"  Sliding window: keep last {SlidingWindowTurns} user turns");
        Output.Gray($"  Truncation backstop: {TruncationTokenThreshold} tokens");
        Output.Gray("(Low thresholds so compaction fires within a few turns in demo)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);
        IChatClient chatClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        // KEY: CompactionProvider registered via AsBuilder().UseAIContextProviders().
        // It runs INSIDE the tool-calling loop, before runbookProvider and incidentProvider.
        PipelineCompactionStrategy compactionStrategy = new(
        [
            new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(SlidingWindowTurns),
                minimumPreservedTurns: 2,
                target: null),
            new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed(TruncationTokenThreshold),
                minimumPreservedGroups: 2,
                target: null),
        ]);

        CompactionProvider  compactionProvider = new(compactionStrategy);
        RunbookContextProvider runbookProvider  = new(Runbook);
        IncidentContextProvider incidentProvider = new();

        // KEY: AsBuilder().UseAIContextProviders() — compaction FIRST, then retrieval, then session facts.
        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(compactionProvider, runbookProvider, incidentProvider)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "IncidentResponseAgent",
                ChatOptions = new()
                {
                    Instructions =
                        "You are an IT incident response agent. " +
                        "Call GetServiceStatus to verify service health. " +
                        "Relevant runbook entries are injected automatically — use them. " +
                        "When a root cause is confirmed, say 'ROOT CAUSE CONFIRMED:' followed by the cause. " +
                        "When resolved, call ArchiveResolution. Be very concise (one sentence max per response).",
                    Tools =
                    [
                        AIFunctionFactory.Create(GetServiceStatus,  name: nameof(GetServiceStatus)),
                        AIFunctionFactory.Create(ArchiveResolution, name: nameof(ArchiveResolution)),
                    ]
                }
            });

        InMemoryChatHistoryProvider? historyProvider = agent.GetService<InMemoryChatHistoryProvider>();

        // ── Scenario A — Message count bounded across 8 turns ───────────────
        Output.Yellow("SCENARIO A — Message count bounded over 8 turns (verbose log pasting)");
        Output.Separator(false);

        AgentSession sessionA = await agent.CreateSessionAsync();

        // Simulate an engineer pasting large log excerpts each turn
        string[] aTurns =
        [
            "APIGateway OOM crash. Log excerpt: [2025-01-10 03:14:01] FATAL OutOfMemoryException at RequestBufferingMiddleware. Heap dump path: /tmp/heapdump-031401.hprof. Stack: at System.IO.MemoryStream.set_Capacity at Microsoft.AspNetCore.Http.Features.RequestBodyPipeFeature.get_Reader",
            "Checked the heap dump. Top allocations: System.Byte[] 2.1GB, Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpParser 120MB. Looks like the buffer is the problem.",
            "GetServiceStatus shows APIGateway degraded. Other services healthy. The OOM correlates with large file uploads on the /upload route.",
            "ROOT CAUSE CONFIRMED: unbounded request body buffering on /upload route. Adding MaxRequestBodySize = 50MB in Startup.cs.",
            "Deployed hotfix to staging. No OOM in staging after 200 test uploads. Promoting to production.",
            "Production deployment complete. OOM rate dropped to zero. Monitoring for 30 minutes.",
            "30-minute monitoring window passed. APIGateway stable. Incident resolved.",
            "What was the root cause we confirmed at the start of this incident?",
        ];

        for (int i = 0; i < aTurns.Length; i++)
        {
            Output.Gray($"Turn {i + 1}: {aTurns[i][..Math.Min(80, aTurns[i].Length)]}...");
            AgentResponse resp = await agent.RunAsync(aTurns[i], sessionA);
            Output.Green($"Agent: {resp.Text}");

            int stored = historyProvider?.GetMessages(sessionA)?.Count ?? 0;
            IEnumerable<ChatMessage> compacted = await CompactionProvider.CompactAsync(compactionStrategy,
                historyProvider?.GetMessages(sessionA) ?? []);
            int llmVisible = compacted.Count();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  Turn {i + 1} → stored: {stored} msgs | LLM-visible after compaction: {llmVisible} msgs");
            Console.ResetColor();
        }

        Output.Separator();

        // ── Scenario B — Incident log survives compaction ────────────────────
        Output.Yellow("SCENARIO B — IncidentLog survives compaction (confirmed facts always present)");
        Output.Separator(false);
        Output.Gray("After 8 turns (early turns compacted away), asking 'what have we confirmed so far?'");
        Output.Gray("The agent should answer correctly from IncidentLog — not from compacted message history.");

        const string bQuery = "List everything we have confirmed about this incident so far.";
        Output.Gray($"Engineer: {bQuery}");
        AgentResponse bResp = await agent.RunAsync(bQuery, sessionA);
        Output.Green($"Agent: {bResp.Text}");
        Output.Gray("(Answered from ProvideAIContextAsync-injected IncidentLog — not from compacted history)");
        Output.Separator();

        // ── Scenario C — Runbook archive survives compaction ─────────────────
        Output.Yellow("SCENARIO C — Runbook archive survives compaction");
        Output.Separator(false);

        int runbookSize = Runbook.Count;
        Output.Blue($"Runbook entries after session A: {runbookSize}");
        Output.Gray("(Runbook lives in RunbookContextProvider instance — not in message history. " +
                    "Compaction cannot affect it.)");
        Output.Separator();

        // ── Scenario D — Construction order demonstration ────────────────────
        Output.Yellow("SCENARIO D — Construction order (compaction MUST be outermost)");
        Output.Separator(false);

        Output.Gray("Building WRONG order: [runbookProvider, compactionProvider, incidentProvider]");
        Output.Gray("(runbookProvider.InvokingCoreAsync runs BEFORE compaction — sees un-compacted message list)");

        RunbookContextProvider wrongRunbook   = new(Runbook);
        IncidentContextProvider wrongIncident = new();
        CompactionProvider wrongCompaction    = new(new SlidingWindowCompactionStrategy(
            CompactionTriggers.TurnsExceed(SlidingWindowTurns), 2, null));

        // Wrong: runbook provider runs before compaction
        AIAgent wrongAgent = chatClient
            .AsBuilder()
            .UseAIContextProviders(wrongRunbook, wrongCompaction, wrongIncident)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "IncidentResponseAgent_WrongOrder",
                ChatOptions = new()
                {
                    Instructions = "You are an IT incident response agent. Be concise.",
                    Tools = [AIFunctionFactory.Create(GetServiceStatus, name: nameof(GetServiceStatus))]
                }
            });

        InMemoryChatHistoryProvider? wrongHistory = wrongAgent.GetService<InMemoryChatHistoryProvider>();
        AgentSession wrongSession = await wrongAgent.CreateSessionAsync();

        string[] wrongTurns = aTurns[..5]; // 5 turns to accumulate enough history
        int wrongMsgCount = 0;
        foreach ((string turn, int idx) in wrongTurns.Select((t, i) => (t, i)))
        {
            await wrongAgent.RunAsync(turn, wrongSession);
            wrongMsgCount = wrongHistory?.GetMessages(wrongSession)?.Count ?? 0;
        }

        IEnumerable<ChatMessage> wrongMsgs = wrongHistory?.GetMessages(wrongSession) ?? [];
        int wrongRunbookSearchCount = wrongMsgs.Count(m =>
            m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        Output.Red($"  WRONG order — messages RunbookContextProvider searches: {wrongRunbookSearchCount} External msgs " +
                   $"(out of {wrongMsgCount} stored). Compaction hasn't trimmed them yet.");

        // Correct order
        RunbookContextProvider  correctRunbook   = new(Runbook);
        IncidentContextProvider correctIncident  = new();
        CompactionProvider      correctCompaction = new(new SlidingWindowCompactionStrategy(
            CompactionTriggers.TurnsExceed(SlidingWindowTurns), 2, null));

        AIAgent correctAgent = chatClient
            .AsBuilder()
            .UseAIContextProviders(correctCompaction, correctRunbook, correctIncident)
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "IncidentResponseAgent_CorrectOrder",
                ChatOptions = new()
                {
                    Instructions = "You are an IT incident response agent. Be concise.",
                    Tools = [AIFunctionFactory.Create(GetServiceStatus, name: nameof(GetServiceStatus))]
                }
            });

        InMemoryChatHistoryProvider? correctHistory = correctAgent.GetService<InMemoryChatHistoryProvider>();
        AgentSession correctSession = await correctAgent.CreateSessionAsync();

        foreach (string turn in wrongTurns)
            await correctAgent.RunAsync(turn, correctSession);

        IList<ChatMessage> correctStored = correctHistory?.GetMessages(correctSession) ?? [];
        IEnumerable<ChatMessage> correctCompacted = await CompactionProvider.CompactAsync(
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(SlidingWindowTurns), 2, null),
            correctStored);
        int correctRunbookSearchCount = correctCompacted.Count(m =>
            m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        Output.Green($"  CORRECT order — messages RunbookContextProvider searches: {correctRunbookSearchCount} External msgs " +
                     $"(out of {correctStored.Count} stored). Compaction already trimmed the list.");

        Console.WriteLine();
        Output.Blue("Correct registration:");
        Output.Blue("  chatClient.AsBuilder().UseAIContextProviders(compactionProvider, runbookProvider, incidentProvider)");
        Output.Blue("  — Compaction outermost. Runbook searches only recent, already-trimmed messages.");
        Output.Separator();

        Output.Yellow("KEY LEARNING:");
        Output.Gray("  CompactionProvider must be registered via AsBuilder().UseAIContextProviders().");
        Output.Gray("  Order: compaction first → runbook → session facts.");
        Output.Gray("  IncidentLog (ProviderSessionState<T>) survives compaction — facts never lost.");
        Output.Gray("  Runbook (shared List<>) survives compaction — not in message history.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RunbookContextProvider (identical logic to V3)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RunbookContextProvider : AIContextProvider
{
    private readonly List<RunbookEntry> _runbook;

    private readonly ProviderSessionState<InjectionState> _sessionState = new(
        stateInitializer: _ => new InjectionState(),
        stateKey: nameof(RunbookContextProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    public RunbookContextProvider(List<RunbookEntry> runbook) : base(null, null)
    {
        _runbook = runbook;
    }

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        InjectionState state = _sessionState.GetOrInitializeState(context.Session);

        IEnumerable<ChatMessage> externalMessages = (context.AIContext.Messages ?? [])
            .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        string searchQuery = string.Join(" ", externalMessages.Select(m => m.Text ?? string.Empty)).Trim();

        List<ChatMessage> newInjections = [];

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
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
                if (state.AlreadyInjectedIds.Contains(hit.Id))
                {
                    Output.Gray($"  [RunbookContextProvider] ({hit.Id} already injected — skipped)");
                    continue;
                }

                string entryText =
                    $"[RUNBOOK {hit.Id}] Service: {hit.Service}\n" +
                    $"Symptoms: {hit.Symptoms}\n" +
                    $"Root cause: {hit.RootCause}\n" +
                    $"Resolution: {hit.Resolution}";

                ChatMessage stamped = new ChatMessage(ChatRole.User, entryText)
                    .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!);

                newInjections.Add(stamped);
                state.AlreadyInjectedIds.Add(hit.Id);
                Output.Blue($"  [RunbookContextProvider] Injecting {hit.Id} (stamped AIContextProvider)");
            }

            if (newInjections.Count > 0)
                _sessionState.SaveState(context.Session, state);
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages     = (context.AIContext.Messages ?? []).Concat(newInjections),
            Tools        = context.AIContext.Tools
        });
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
            return default;

        string? confirmedCause = null;
        string? symptomsHint   = null;
        string? serviceHint    = null;

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

        string resolution = string.Join(" ", (context.ResponseMessages ?? [])
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text ?? string.Empty));

        if (string.IsNullOrWhiteSpace(resolution)) return default;

        string id = $"RB-{(_runbook.Count + 1):D3}";
        _runbook.Add(new RunbookEntry(id,
            serviceHint ?? "Unknown",
            symptomsHint ?? confirmedCause,
            confirmedCause,
            resolution.Length > 200 ? resolution[..200] : resolution,
            confirmedCause.ToLowerInvariant().Split(' ').Take(4).ToArray()));

        Output.Green($"  [InvokedCoreAsync] Archived {id} — runbook now {_runbook.Count} entries");
        return default;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IncidentContextProvider (identical to V3)
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
            "INCIDENT LOG — confirmed facts this session (injected every turn, survives compaction):\n" +
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
                    Output.Blue($"  [IncidentContextProvider] Stored: \"{confirmed}\"");
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
                    Output.Blue($"  [IncidentContextProvider] Stored (from engineer): \"{confirmed}\"");
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
