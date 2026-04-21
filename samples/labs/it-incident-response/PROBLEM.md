# IT Incident Response Assistant — Microsoft Agent Framework Lab

## Problem Statement

A mid-size SaaS company runs a platform engineering team that handles 30–60 production incidents per week. Engineers diagnose issues across five services: API Gateway, Auth Service, Payment Processor, Notification Worker, and Data Sync. Incidents are reported through a console tool where the on-call engineer describes symptoms, pastes log excerpts, and asks the agent for diagnosis and remediation steps.

The company has accumulated 18 months of incident history — resolved tickets with root causes, remediation steps, and post-mortem notes. This knowledge lives in a shared runbook, but engineers rarely search it under pressure. An LLM agent that can surface the three most relevant past incidents *before* answering a diagnosis question would cut mean time to resolution (MTTR) from 45 minutes to under 10. The challenge: the agent must retrieve relevant past incidents based on what the engineer is describing right now, inject them as context, and archive the current session's resolution so future engineers benefit from it.

Without a context provider that intercepts the assembled message list, the agent cannot distinguish between what the engineer just typed and what the provider injected in a prior turn. It re-searches its own injected runbook entries as if they were new symptoms, retrieves the same incidents repeatedly, and produces circular reasoning. The context provider must filter the message stream, control what gets retrieved and what gets injected, and prevent its own output from polluting subsequent retrievals.

---

## Hardcoded Runbook (in-memory — no infrastructure required)

The lab starts with 6 seeded past incidents. V3+ adds new entries as the agent resolves them.

```
RB-001: Service=AuthService, Symptoms="JWT validation failing, 401 on all requests"
        Root cause: expired signing certificate
        Resolution: rotate cert via `auth-admin rotate-cert`, redeploy AuthService
        Tags: auth, jwt, certificate

RB-002: Service=PaymentProcessor, Symptoms="Stripe webhook timeout, payment stuck pending"
        Root cause: outbound HTTP client pool exhausted
        Resolution: increase HttpClientFactory pool size in appsettings, restart service
        Tags: payment, http, timeout

RB-003: Service=APIGateway, Symptoms="OOM crash, heap dump shows large request buffer"
        Root cause: unbounded request body buffering on /upload route
        Resolution: add MaxRequestBodySize limit in Startup.cs, deploy hotfix
        Tags: oom, memory, gateway

RB-004: Service=NotificationWorker, Symptoms="Emails queued but not sent, queue depth rising"
        Root cause: SMTP credentials rotated but env var not updated
        Resolution: update NOTIFICATION__SmtpPassword secret, restart worker
        Tags: notification, smtp, credentials

RB-005: Service=DataSync, Symptoms="Sync jobs timing out after 30s, partial data in DB"
        Root cause: missing index on foreign key causing full table scan
        Resolution: run migration 0047_add_sync_fk_index.sql, no restart needed
        Tags: datasync, database, timeout, index

RB-006: Service=AuthService, Symptoms="High latency on /token endpoint, CPU spike"
        Root cause: Redis cache eviction causing bcrypt rehash on every request
        Resolution: increase Redis maxmemory, flush expired keys with `redis-cli FLUSHDB`
        Tags: auth, redis, latency, cache
```

---

## Concept Mapping

| # | Concept | Official Doc | Where it fits | Why needed | What breaks without it |
|---|---------|-------------|--------------|------------|------------------------|
| 1 | `ProvideAIContextAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `IncidentContextProvider` — injects the current session's active service scope and any previously confirmed root causes as instructions before each LLM call | Agent needs a stable anchor of what has already been diagnosed this session; without it each turn starts cold | Engineer confirms "root cause is Redis cache eviction" in turn 3 — in turn 4, agent forgets and proposes unrelated fixes |
| 2 | `StoreAIContextAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `IncidentContextProvider` — after each turn, extracts confirmed root cause and resolution steps from the agent's response, appends to `ProviderSessionState<IncidentLog>` | Builds a running per-session incident log that feeds the runbook archive at session end | No log — resolved incident is never archived; the next engineer with identical symptoms gets no runbook hits |
| 3 | `ProviderSessionState<T>` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Inside `IncidentContextProvider` — typed `IncidentLog` tracks confirmed facts, retrieved runbook IDs, and resolution steps per session | Isolates each engineer's session; provider instance is shared across all on-call sessions simultaneously | Two engineers handling simultaneous incidents share one log — engineer B's confirmed root cause overwrites engineer A's |
| 4 | `InvokingCoreAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `RunbookContextProvider` — intercepts the assembled message list, filters to user-sourced messages only, searches the runbook using the engineer's symptom description, injects top-3 similar past incidents as stamped context | `ProvideAIContextAsync` cannot access `InvokingContext.Messages` — only `InvokingCoreAsync` can filter the live message stream by source type to extract the engineer's actual input | Without filtering, the provider searches using ALL messages — including its own injected runbook entries from prior turns, retrieving the same 3 incidents every turn regardless of what the engineer says |
| 5 | `InvokedCoreAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `RunbookContextProvider` — after the agent responds, parses the response for a confirmed resolution and archives it as a new runbook entry | The post-call hook is the only place both the request (what the engineer described) and the response (what the agent resolved) are simultaneously available for archival | Resolution steps are never archived — the runbook stays at 6 entries forever; future engineers with same symptoms get no hits |
| 6 | Message source stamping (`WithAgentRequestMessageSource` / `GetAgentRequestMessageSourceType`) | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Inside `InvokingCoreAsync` — injected runbook entries are stamped `AIContextProvider` so turn 2's retrieval scan skips them | Without stamping, injected runbook text ("Root cause: Redis cache eviction") is treated as new engineer input in turn 2 — the retrieval finds RB-006 again and re-injects it | Turn 2 search finds "Redis cache eviction" in the prior injected context, not from the engineer — retrieval loops on the same entry every turn |

---

## Additional Concepts (mechanically required)

| # | Concept | Why added | What it enables |
|---|---------|-----------|----------------|
| 1 | Function Tools (`AIFunctionFactory.Create`) | V1 baseline needs real tool calls to establish the domain | Agent can call `GetServiceStatus`, `SearchRunbook`, `ArchiveResolution` against hardcoded service state — without tools it guesses service health |
| 2 | Context Compaction (`CompactionProvider`) | Real incidents involve 10–20 turns of log pasting and hypothesis testing; each turn appends large log excerpts — context window fills fast | Bounds LLM-visible message count; `IncidentLog` in `ProviderSessionState<T>` survives compaction, so the agent always knows what was confirmed |

---

## RAG Integration Path

V4 closes with the runbook as an `IList<RunbookEntry>` searched by keyword matching. The natural V5 extension is:

- Replace `IList<RunbookEntry>` with `SqliteVectorStore` (already used in `samples/labs/recipe/V5_RAG`)
- Replace keyword search in `InvokingCoreAsync` with `collection.SearchAsync(symptomText, topK: 3)`
- No changes to `InvokedCoreAsync`, `ProviderSessionState<T>`, or source stamping — the provider contract is identical

This makes V5_RAG a clean one-file delta: swap the storage backend, keep the context provider logic.

---

## Failure Scenarios

| Scenario | What fails | Which concept prevents it |
|----------|-----------|--------------------------|
| No context provider: engineer confirms Redis root cause in turn 3, asks for fix in turn 4 | Agent has no memory of the confirmation — proposes certificate rotation (wrong fix) | V2: `ProvideAIContextAsync` injects confirmed root cause each turn |
| No session isolation: two engineers handling simultaneous incidents | Engineer B's log overwrites Engineer A's confirmed root cause | V2: `ProviderSessionState<IncidentLog>` isolates per-session |
| No `InvokingCoreAsync`: provider cannot filter message list by source | Retrieval runs on all messages including its own injected runbook entries — same 3 entries returned every turn | V3: source type filtering in `InvokingCoreAsync` restricts search to `External` messages only |
| No source stamping: injected runbook entry contains "Redis cache eviction" | Turn 2 search matches that phrase in the injected message, re-retrieves RB-006 — retrieval loops | V3: `GetAgentRequestMessageSourceType()` skips `AIContextProvider`-stamped messages |
| No `InvokedCoreAsync`: agent resolves OOM crash with a hotfix | Incident not archived — next engineer with same OOM symptom gets no runbook hit | V3: `InvokedCoreAsync` parses resolution and calls `ArchiveResolution` |
| 15-turn incident (log pasting + hypothesis testing) | Token budget exceeded — early context lost, agent forgets initial symptoms | V4: `CompactionProvider` trims message history; `IncidentLog` in session state preserves all confirmed facts |

---

## Deep Dive Q&A

**Q1: `InvokingCoreAsync` receives `InvokingContext.Messages`. Why must the runbook search use only messages where `GetAgentRequestMessageSourceType() == External`, and what goes wrong if you search all messages?**

Answer: The assembled message list in `InvokingContext.Messages` contains messages from multiple sources: the engineer's actual input (`External`), the agent's prior responses (`ChatHistory`), and anything the provider injected in previous turns (`AIContextProvider`). If you search all messages, the retrieval query includes the text of runbook entries that were injected last turn. Those entries contain symptoms like "OOM crash, heap dump shows large request buffer" — which matches RB-003 perfectly. So the search finds RB-003 again, injects it again, and the cycle continues. By turn 5, the context is dominated by the same 3 entries repeating. Filtering to `External` messages ensures the search query is only what the engineer actually typed this turn — their real symptom description. `GetAgentRequestMessageSourceType()` returns `External` for messages the engineer submitted, `AIContextProvider` for messages this provider injected, and `ChatHistory` for messages that came from the session history provider.

**Q2: `InvokedCoreAsync` has access to both `RequestMessages` and `ResponseMessages`. Why is the cross-referencing of both needed to archive a resolved incident correctly?**

Answer: Archiving a resolved incident requires two pieces: the symptom description (what was the problem) and the resolution (what fixed it). The symptom comes from `RequestMessages` — specifically the `External`-sourced messages that describe what the engineer reported. The resolution comes from `ResponseMessages` — the agent's confirmed fix. Neither alone is sufficient. If you archive only the response, the runbook entry has a resolution but no symptom — future keyword search finds nothing. If you archive only the request, the entry has symptoms but no fix — the runbook is a list of unsolved problems. `InvokedCoreAsync` is the only hook where both are simultaneously available. `StoreAIContextAsync` also receives both, but its signature does not expose `InvokedContext.InvokeException` — if the LLM call failed, you do not want to archive a partial or hallucinated resolution. `InvokedCoreAsync` lets you check `context.InvokeException is not null` and skip archival on failure.

**Q3: Two `IncidentContextProvider` instances are registered in `AIContextProviders = [runbookProvider, incidentProvider]`. Both override `InvokingCoreAsync`. In what order do they fire, and can one provider's injection affect the other's source-type filtering?**

Answer: Providers fire in registration order. `runbookProvider.InvokingCoreAsync` runs first. It reads `InvokingContext.Messages`, filters to `External` messages, searches the runbook, and returns an `AIContext` with injected runbook entries stamped as `AIContextProvider`. The framework merges this returned `AIContext` into the context being built. Then `incidentProvider.InvokingCoreAsync` runs and receives a `InvokingContext.Messages` list that now includes the runbook entries just injected by `runbookProvider`. If `incidentProvider` also filters by source type, it correctly skips the runbook entries (they are stamped `AIContextProvider`) and only processes the engineer's original `External` messages. This is why stamping matters even when providers compose: each provider's injected messages must be stamped so that subsequent providers in the chain can identify and skip them. Providers that do not stamp their injected messages create invisible interference for every other provider registered after them.

**Q4: The runbook is searched in `InvokingCoreAsync` using the engineer's current message. But across a 10-turn incident session, the engineer says different things each turn. How do you avoid injecting duplicate runbook entries across turns?**

Answer: `ProviderSessionState<IncidentLog>` tracks which runbook entry IDs have already been injected this session (e.g., `AlreadyInjectedIds = ["RB-003", "RB-006"]`). In `InvokingCoreAsync`, after the retrieval finds top-3 candidates, the provider filters out any entry whose ID is already in the session's `AlreadyInjectedIds` set, injects only the new ones, and updates `AlreadyInjectedIds`. This ensures that even if turn 5's symptom description would retrieve RB-003 again, it is not re-injected — the engineer already has it in their context from turn 1. The `ProviderSessionState<IncidentLog>` is the right place for this because it is per-session (no cross-session contamination) and persists across all turns (it was designed for exactly this kind of cross-turn deduplication state).

**Q5: At the end of the incident session, the agent archives the resolution as a new runbook entry. If the same agent instance is then used for a different engineer's session (different `AgentSession`), can that new session immediately retrieve the archived entry?**

Answer: It depends on where the runbook lives. In V3, the runbook is a shared `IList<RunbookEntry>` — a reference type held by the `RunbookContextProvider` instance. Since the provider instance is shared across all sessions (as required by the framework's design), a new entry archived by engineer A's session is immediately visible to engineer B's session. This is intentional: the runbook is shared institutional knowledge. `ProviderSessionState<T>` is per-session by design — it is the wrong place for the runbook. The runbook itself must live in the provider instance (as a shared `List<RunbookEntry>`) or in an external store. In V5_RAG, the shared `List` becomes a `SqliteVectorStore` — still shared, now persistent across process restarts.

**Q6: `CompactionProvider` must be registered via `AsBuilder().UseAIContextProviders()` rather than `ChatClientAgentOptions.AIContextProviders`. Given that `RunbookContextProvider.InvokingCoreAsync` searches the message list for `External`-sourced messages, what happens to that search if compaction runs first and removes early-turn user messages?**

Answer: When compaction runs first (correct order: `UseAIContextProviders([compactionProvider, runbookProvider, incidentProvider])`), the message list that `RunbookContextProvider.InvokingCoreAsync` receives is already trimmed. If compaction removed turn 1's user message (the initial symptom description), the retrieval search in turn 10 only sees recent `External` messages — turns 8, 9, 10. The runbook search query does not include "OOM crash" from turn 1 unless the engineer re-stated it recently. This is the correct behavior: by turn 10 the conversation has evolved, and the most relevant runbook entries are those matching the current state of the investigation. The confirmed root cause from turn 1 is preserved in `ProviderSessionState<IncidentLog>` (injected via `ProvideAIContextAsync` every turn), so the agent still knows what was confirmed — it just does not re-search for runbook entries matching old, already-resolved hypotheses.

**Q7: The `RunbookContextProvider` overrides `InvokingCoreAsync` instead of the simpler `ProvideAIContextAsync`. The base class already filters `External` messages before calling `ProvideAIContextAsync`. Why isn't the base filter sufficient here, and what capability does `InvokingCoreAsync` provide that `ProvideAIContextAsync` does not?**

Answer: `ProvideAIContextAsync` receives a pre-filtered view of `External` messages — the base `InvokingCoreAsync` has already removed `AIContextProvider`-stamped and `ChatHistory` messages before calling it. That filtering is correct for most providers. But `RunbookContextProvider` needs two things `ProvideAIContextAsync` cannot do: (1) inspect the full assembled `context.AIContext.Messages` list — including prior injections from other providers — to check `AlreadyInjectedIds` before deciding what to inject; and (2) return a merged `AIContext` that contains the complete message list with runbook entries inserted at a controlled position. `ProvideAIContextAsync` only returns an additive delta; the base class appends it blindly. `InvokingCoreAsync` returns the complete merged `AIContext`, giving the provider full control over message ordering and composition. By contrast, `IncidentContextProvider` only injects a system instruction (`ProvideAIContextAsync` returns `Instructions = ...`) — the base class append behavior is exactly right for that case, so overriding `InvokingCoreAsync` there would be unnecessary complexity.

---

## Lab Versions

| Version | Folder | New Concept | What it adds vs previous |
|---------|--------|-------------|--------------------------|
| V1 | `V1_AgentBaseline` | Function Tools (`AIFunctionFactory.Create`) | Runnable domain baseline: `GetServiceStatus`, `SearchRunbook` (keyword-only, no provider), `ArchiveResolution`. Engineer describes OOM crash — agent looks up runbook manually via tool, resolves it, but never archives the outcome. Duplicate incident next week gets no help. |
| V2 | `V2_SimpleContextProvider` | `ProvideAIContextAsync` + `StoreAIContextAsync` + `ProviderSessionState<IncidentLog>` | Per-session confirmed-fact injection (agent always knows what was diagnosed this session). Running incident log built turn-by-turn. Session isolation: two parallel on-call sessions have independent logs. |
| V3 | `V3_RunbookContextProvider` | `InvokingCoreAsync` + `InvokedCoreAsync` + `WithAgentRequestMessageSource` / `GetAgentRequestMessageSourceType` | Automatic runbook retrieval keyed to the engineer's actual symptom text (not all messages). Post-call archival of new resolutions. Deduplication of injected entries across turns. Feedback-loop prevention via source stamping. |
| V4 | `V4_Compaction` | `CompactionProvider` + `AsBuilder().UseAIContextProviders()` | Bounded LLM token window for 15-turn incident sessions. Incident log and runbook archive survive compaction via `ProviderSessionState<T>`. Construction order teaching point: compaction outermost, then runbook provider, then incident provider. |

---

## Test Scenarios

### V1 — AgentBaseline

**Scenario A — OOM crash (exact runbook match):**
Engineer: "API Gateway crashing with OOM, heap dump attached. Getting large request buffer errors."
Expected: Agent calls `SearchRunbook("OOM gateway large request buffer")` → finds RB-003 → proposes MaxRequestBodySize fix. Does NOT call `ArchiveResolution` — no archival in V1.

**Scenario B — Auth failure (retrieves RB-001 or RB-006):**
Engineer: "All auth requests returning 401. JWT validation failing."
Expected: Agent calls `SearchRunbook`, finds RB-001. Proposes cert rotation. No archival.

**Scenario C — Novel incident (no runbook match):**
Engineer: "DataSync jobs hanging at exactly 30s. No errors in logs, just silence then timeout."
Expected: Agent calls `SearchRunbook`, finds RB-005 (partial match on timeout). Proposes index migration. Print that archival would have captured this — but V1 doesn't do it.

**Scenario D — Multi-turn without memory:**
Turn 1: Engineer describes symptoms. Agent proposes fix A.
Turn 2: Engineer says "that didn't work, trying fix B."
Turn 3: Agent asks "what were the original symptoms?" — it has forgotten turn 1's content.
Expected: Demonstrate the forgetting. This is what V2's `ProvideAIContextAsync` addresses.

---

### V2 — SimpleContextProvider

**Scenario A — Confirmed fact injection:**
Turn 1: Engineer describes OOM crash. Agent diagnoses MaxRequestBodySize issue.
Turn 2: Engineer confirms "yes that was it, deploying now."
`StoreAIContextAsync` extracts confirmation → stored in `IncidentLog`.
Turn 3: Engineer asks "what was the fix again?" → `ProvideAIContextAsync` injects confirmed root cause → agent answers without re-diagnosing.
Expected: Print `ProvideAIContextAsync` injected instructions on turn 3 showing the confirmed fact.

**Scenario B — Session isolation:**
Two `AgentSession` instances on the same `IncidentContextProvider` instance.
Session A: Engineer Alice confirms "root cause: Redis cache eviction."
Session B: Engineer Bob confirms "root cause: SMTP credentials."
Expected: Print both sessions' `IncidentLog` — A contains only Alice's facts, B contains only Bob's. Provider instance is shared; state is not.

**Scenario C — Audit log grows turn-by-turn:**
3-turn incident session. After each turn, print the `IncidentLog` size.
Expected: Log grows 0 → 1 → 2. Each `StoreAIContextAsync` call adds one entry.

**Scenario D — Failed call: StoreAIContextAsync skips:**
Simulate a failed `RunAsync` (use a cancellation token or test a bad prompt).
Expected: `StoreAIContextAsync` is NOT called on failure — demonstrate that the base class skips it on exception, keeping the audit log clean.

---

### V3 — RunbookContextProvider

**Scenario A — Automatic retrieval keyed to engineer input:**
Engineer: "Notification emails stuck in queue, queue depth at 500 and rising."
Expected: `InvokingCoreAsync` filters to `External` messages, searches runbook → finds RB-004 (SMTP credentials). Injects it as stamped message. Agent cites RB-004 in response. Print the injected message with its `AIContextProvider` source stamp.

**Scenario B — Feedback-loop prevention:**
Turn 1: Engineer asks about OOM. `InvokingCoreAsync` injects RB-003 (stamped).
Turn 2: Engineer asks "is there anything else to check?" (no new symptoms).
Expected: Print message source types processed by `InvokingCoreAsync` in turn 2. RB-003's injected text is skipped (`AIContextProvider` source). Search runs only on "is there anything else to check?" — finds no new runbook match. No duplicate injection.

**Scenario C — New entry archived and retrieved:**
Turn 1: Engineer describes novel incident "DataSync crashing on null FK reference, no migration exists."
Turn 2: Agent proposes fix. `InvokedCoreAsync` parses resolution and calls `ArchiveResolution` → new entry RB-007 added.
Turn 3 (new session): Different engineer describes "DataSync null foreign key crash."
Expected: `InvokingCoreAsync` retrieves RB-007 (just archived) and injects it. Agent cites the new entry. Print runbook size before (6) and after (7).

**Scenario D — Deduplication across turns:**
Turn 1: OOM symptoms → RB-003 injected, logged in `ProviderSessionState`.
Turn 2: Engineer pastes more OOM logs (same symptom phrasing).
Expected: `InvokingCoreAsync` finds RB-003 again in retrieval but checks `AlreadyInjectedIds` → skips it. Print "(RB-003 already injected this session — skipped)."

---

### V4 — Compaction

**Scenario A — Message count bounded:**
15-turn incident session with verbose log pasting per turn. After each turn, print: stored message count vs LLM-visible message count.
Expected: Stored count grows to 30+. LLM-visible count stays bounded (≤10 messages).

**Scenario B — Incident log survives compaction:**
After 15 turns (early turns compacted away), ask "what have we confirmed so far?"
Expected: Agent answers correctly from `IncidentLog` injected by `ProvideAIContextAsync` — all confirmed facts present despite early messages being compacted.

**Scenario C — Runbook archive survives compaction:**
After 15 turns, print runbook size.
Expected: All archived entries present — runbook lives in provider instance (shared reference), not in message history.

**Scenario D — Construction order demonstration:**
Build agent with wrong order: `[runbookProvider, compactionProvider, incidentProvider]`.
Turn 3: print message count that `runbookProvider.InvokingCoreAsync` sees — it's un-compacted (large).
Rebuild with correct order: `[compactionProvider, runbookProvider, incidentProvider]`.
Turn 3: print message count — it's already trimmed (small).
Expected: Side-by-side counts showing the difference. State which order is correct for this lab and why.
