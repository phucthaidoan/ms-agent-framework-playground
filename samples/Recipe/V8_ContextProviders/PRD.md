# V8: Context Providers — Product Requirements Document

## Overview

This document describes how the **Microsoft Agent Framework's Context Providers** API applies to the `MealPlannerV7` architecture. It evaluates fit per pipeline phase, lists pros and cons, and defines the key learnings a developer should gain from studying this progression.

V8 is not a ground-up rewrite. It is a targeted refactoring that replaces the most mechanical memory patterns in V7 with the framework's built-in extension point — `AIContextProvider`.

---

## 1. What Are Context Providers?

Context providers are pipeline hooks that execute **around every agent invocation**. They are registered on an agent via `ChatClientAgentOptions.AIContextProviders` and fire automatically — no manual calls required.

```
User message
     │
     ▼
[ProvideAIContextAsync]  ← inject instructions / messages / tools
     │
     ▼
  LLM call
     │
     ▼
[StoreAIContextAsync]    ← extract & persist from request + response
     │
     ▼
Agent response
```

### Core API

| Member | Purpose |
|--------|---------|
| `AIContextProvider` | Abstract base class for all providers |
| `ProvideAIContextAsync(InvokingContext)` | Returns `AIContext` (messages, instructions, tools) injected before the LLM call |
| `StoreAIContextAsync(InvokedContext)` | Receives full request + response messages after the LLM call |
| `InvokingCoreAsync` / `InvokedCoreAsync` | Advanced overrides — full control over pipeline merging and filtering |
| `AgentSessionStateBag` | Per-session state storage on `AgentSession.StateBag` (typed via `SetValue<T>` / `TryGetValue<T>`, reference types only) |
| `InMemoryHistoryProvider` | Built-in provider for local in-process chat history |

### Registration

```csharp
ChatClientAgent advisorAgent = chatClient.AsAIAgent(
    new ChatClientAgentOptions()
    {
        ChatOptions = new() { Instructions = "You are a meal plan advisor..." },
        AIContextProviders =
        [
            new UserProfileContextProvider(profileCollection),
            new ConversationTurnContextProvider(turnCollection, username, sessionId)
        ]
    });
```

### Session State

A provider instance is **shared across all sessions** of an agent. Per-session data (memory IDs, turn counters, etc.) must be stored in the `AgentSession` via `ProviderSessionState<T>`, not in provider fields.

```csharp
// Define state type
class TurnState { public int TurnNumber { get; set; } }

// In provider constructor
_sessionState = new ProviderSessionState<TurnState>(
    stateInitializer: _ => new TurnState(),
    stateKey: GetType().Name);

// Read
var state = _sessionState.GetOrInitializeState(context.Session);

// Write
_sessionState.SaveState(context.Session, state);
```

---

## 2. MealPlannerV7 Architecture Recap

V7 has 11 phases. Context-relevant phases are:

| Phase | Step | Current Mechanism |
|-------|------|-------------------|
| 0 | User profile lookup + episodic recall | `LookupUserProfileAsync()`, `PrintEpisodicRecallAsync()` |
| 3 | Semantic notes load → instruction strings | `LoadSemanticNotesAsync()` + `Build*Memory()` helpers |
| 4 | Narrative preview (streaming) | `narrativeAgent.RunStreamingAsync()` with `BuildNarrativeMemory()` |
| 5–6 | Planner/Critic refinement loop | `plannerAgent.RunAsync<MealPlan>()` + `BuildPlannerMemory()` / `BuildCriticMemory()` |
| 8 | Profile + plan history upsert | `profileCollection.UpsertAsync()`, `noteCollection.UpsertAsync()` manually after plan approval |
| 9 | Meal indexing for advisor RAG | `mealCollection.UpsertAsync()` per meal — session-scoped |
| 10 | Advisor Q&A + turn persistence | `advisorAgent.RunAsync()` + manual `turnCollection.UpsertAsync()` after each turn |
| 11 | Memory consolidation pipeline | `RunMemoryConsolidationAsync()` — two-agent extract + consolidate |

---

## 3. Fit Evaluation Per Phase

### Phase 0 — User Profile Lookup & Episodic Recall

**Current approach:** `LookupUserProfileAsync()` does a one-time vector search before any agent is created. `PrintEpisodicRecallAsync()` prints last session turns to the console. Neither is wired into an agent.

**Context Provider mapping:** A `UserProfileContextProvider` could hold a reference to `profileCollection`. Its `ProvideAIContextAsync` would load the profile and inject it as a `ChatMessage` (or appended instructions) on every invocation. For episodic recall, the same provider could inject the last session's turns as priming messages on the **first** advisor turn only (using `ProviderSessionState<T>` to track whether it has already injected).

**Fit: High for profile injection. Medium for episodic recall.**

| | Detail |
|-|--------|
| **Pro** | Profile lookup moves from startup-time wiring into a reusable, composable unit |
| **Pro** | Episodic recall primes the advisor automatically without a separate console print |
| **Con** | Episodic recall in V7 is a user-facing console feature, not an agent-context feature — injecting silently into messages changes the UX intent |
| **Con** | Profile lookup in V7 must complete before the agent is even created (score threshold check, new-user branching); a provider cannot substitute for this pre-session decision logic |

---

### Phase 3 — Semantic Notes → Instruction Strings

**Current approach:** `LoadSemanticNotesAsync()` fetches all user notes upfront. Four `Build*Memory()` helpers format them into instruction strings that are baked into each agent's `instructions` parameter at creation time. The context is static for the entire session.

**Context Provider mapping:** A `SemanticMemoryContextProvider` could replace the `Build*Memory()` helpers. Its `ProvideAIContextAsync` would perform a targeted vector search against `noteCollection` using the current user message as the query, returning only the most relevant notes per invocation.

**Fit: High for profile/restriction facts. Medium for preferences/dislikes.**

| | Detail |
|-|--------|
| **Pro** | Notes are retrieved on-demand per query rather than dumped wholesale into the system prompt |
| **Pro** | Each of the four agents (planner, critic, narrative, advisor) gets a tailored provider configuration rather than four bespoke string-building functions |
| **Pro** | Eliminates the session-startup bulk load — fewer tokens on the first invocation |
| **Con** | The `Build*Memory()` helpers produce **different subsets** per agent (critic only needs restrictions; planner needs everything). A single shared provider must replicate this filtering logic via constructor parameters or subclasses |
| **Con** | Hard food restrictions (allergy-level) should always be present — not retrieved via similarity search. They should be injected unconditionally, not semantically |

---

### Phase 4 — Narrative Preview (Streaming)

**Current approach:** `narrativeAgent` is created with `BuildNarrativeMemory()` baked into instructions, then `RunStreamingAsync()` is called once.

**Context Provider mapping:** Same `UserProfileContextProvider` and a light `SemanticMemoryContextProvider` could be attached. However, this is a **single-shot streaming invocation** — there is no session continuity and no need to store anything after.

**Fit: Low.**

| | Detail |
|-|--------|
| **Pro** | Consistent provider reuse — same profile provider attached to all agents |
| **Con** | `StoreAIContextAsync` is unnecessary here; wrapping a one-shot streaming call with a provider adds complexity for no benefit |
| **Con** | Streaming via `RunStreamingAsync()` — unclear if context provider hooks fire on streaming invocations (framework documentation does not confirm this) |

---

### Phase 5–6 — Planner / Critic Refinement Loop

**Current approach:** `plannerAgent` and `criticAgent` each get tailored instruction strings from `BuildPlannerMemory()` and `BuildCriticMemory()`. The loop reuses `plannerSession` across iterations. The critic receives only the plan JSON — it is intentionally stateless.

**Context Provider mapping:**
- `plannerAgent`: A `UserProfileContextProvider` and restriction-aware `SemanticMemoryContextProvider` could replace `BuildPlannerMemory()`.
- `criticAgent`: A minimal `CriticRestrictionProvider` that injects only food restriction data as validation rule #7 replaces `BuildCriticMemory()`.

**Fit: High for planner. High for critic (minimal provider).**

| | Detail |
|-|--------|
| **Pro** | Planner memory becomes dynamic — notes retrieved per refinement iteration, not only at session start |
| **Pro** | Critic restriction injection is trivially small — a one-field provider; eliminates `BuildCriticMemory()` entirely |
| **Con** | The planner uses **structured output** (`RunAsync<MealPlan>`) — provider-injected messages must not disrupt the structured output instruction ordering |
| **Con** | `StoreAIContextAsync` on the planner would capture every refinement iteration's output, potentially storing intermediate rejected plans as memories — unwanted unless explicitly filtered |

---

### Phase 8 — Profile & Plan History Upsert

**Current approach:** After the plan is approved, `profileCollection.UpsertAsync(profile)` and `noteCollection.UpsertAsync(planHistoryNote)` are called manually in the main flow.

**Context Provider mapping:** These writes are triggered by **business logic** (plan approval), not by an agent invocation completing. A `StoreAIContextAsync` hook on the planner would fire after each LLM call — too early and too frequent for profile persistence.

**Fit: Low.**

| | Detail |
|-|--------|
| **Con** | Profile upsert is conditional on plan approval — it belongs in business logic, not a generic provider hook |
| **Con** | Plan history note contains computed data (cost, days, date) assembled from the structured `MealPlan` object — not extractable from raw messages alone |

---

### Phase 9 — Meal Indexing for Advisor RAG

**Current approach:** `mealCollection` is cleared and rebuilt from the approved `MealPlan` object at the start of each session.

**Context Provider mapping:** Not applicable. This is a data pipeline step triggered by business logic (plan approval), not an agent invocation.

**Fit: Not applicable.**

---

### Phase 10 — Advisor Q&A + Turn Persistence

**Current approach:** After each `advisorAgent.RunAsync()`, `turnCollection.UpsertAsync(new ConversationTurnRecord {...})` is called manually in the while-loop. The advisor uses two tools: `search_meal_plan` and `search_user_memory`.

**Context Provider mapping:**
- A `ConversationTurnContextProvider` replaces the manual upsert. Its `StoreAIContextAsync` writes `ConversationTurnRecord` automatically after every invocation. `ProviderSessionState<T>` tracks `sessionId` and `turnNumber`.
- `search_user_memory` tool can remain — it is on-demand and targeted. Alternatively, a `SemanticMemoryContextProvider` on the advisor could replace it with automatic injection, but at the cost of agent discretion.

**Fit: High for turn persistence. Medium for memory tool replacement.**

| | Detail |
|-|--------|
| **Pro** | Turn persistence becomes zero-boilerplate — no manual upsert after every `RunAsync()` call |
| **Pro** | Session ID and turn counter are cleanly encapsulated in `ProviderSessionState<TurnState>` |
| **Pro** | Easier to add audit/diagnostics providers stacked alongside the persistence provider |
| **Con** | The tool-based `search_user_memory` pattern gives the agent discretion about when to pull memory — replacing it with always-on injection adds tokens on every turn regardless of relevance |
| **Con** | `StoreAIContextAsync` receives raw `ChatMessage` objects — the provider must reconstruct `UserMessage` / `AgentResponse` strings from messages, adding parsing logic |

---

### Phase 11 — Memory Consolidation Pipeline

**Current approach:** `RunMemoryConsolidationAsync()` — a two-agent pipeline (extraction agent + consolidation agent) — runs once at session end if the transcript is non-empty.

**Context Provider mapping:** The consolidation pipeline is a **multi-agent workflow**, not a per-turn hook. It could theoretically be triggered from `StoreAIContextAsync` on the last advisor turn, but detecting "last turn" inside a provider requires external signaling.

**Fit: Low.**

| | Detail |
|-|--------|
| **Pro** | None significant — the pipeline is already well-encapsulated in `RunMemoryConsolidationAsync()` |
| **Con** | Provider hooks run on every turn; the consolidation pipeline should run once at session end — the trigger mechanism doesn't align |
| **Con** | The pipeline requires the full session transcript, which a per-turn provider does not naturally accumulate (though it could via `ProviderSessionState<T>`) |

---

## 4. Recommended V8 Architecture

### Providers to Introduce

| Provider | Replaces | Attaches To | Fit |
|----------|----------|-------------|-----|
| `UserProfileContextProvider` | `BuildPlannerMemory()`, `BuildCriticMemory()`, `BuildNarrativeMemory()`, `BuildAdvisorMemory()` (profile fields only) | plannerAgent, criticAgent, narrativeAgent, advisorAgent | High |
| `ConversationTurnContextProvider` | Manual `turnCollection.UpsertAsync()` in advisor while-loop | advisorAgent | High |
| `SemanticMemoryContextProvider` (future) | `BuildPlannerMemory()` (preferences/dislikes/history) + `search_user_memory` tool (optionally) | plannerAgent, advisorAgent | Medium |

### What Stays

| Component | Reason |
|-----------|--------|
| `search_diet_knowledge` tool (plannerAgent) | Agent-driven RAG — adaptive retrieval is more efficient than pre-fetching |
| `search_user_memory` tool (advisorAgent) | On-demand pull is more token-efficient than always-on injection |
| `LookupUserProfileAsync()` | Pre-session logic — gates new-user vs. returning-user branching before any agent exists |
| `PrintEpisodicRecallAsync()` | UX feature — console display for the user, not agent context |
| Phase 8 upserts (profile + plan history) | Business logic conditioned on plan approval |
| Phase 9 meal indexing | Data pipeline — not agent-invocation-driven |
| `RunMemoryConsolidationAsync()` | End-of-session multi-agent workflow — doesn't fit per-turn hooks |

### Illustrative Code Sketch — `UserProfileContextProvider`

```csharp
internal sealed class UserProfileContextProvider : AIContextProvider
{
    private readonly VectorStoreCollection<Guid, UserProfileRecord> _profiles;
    private readonly string _username;

    public UserProfileContextProvider(
        VectorStoreCollection<Guid, UserProfileRecord> profiles,
        string username)
        : base(null, null)
    {
        _profiles = profiles;
        _username = username;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        UserProfileRecord? profile = await LookupProfileAsync(_username);
        if (profile is null) return new AIContext();

        string contextText =
            $"User: {profile.Username}. " +
            $"Diet: {profile.PreferredDietType}. " +
            (string.IsNullOrWhiteSpace(profile.FoodRestrictions)
                ? ""
                : $"HARD RESTRICTIONS (never include): {profile.FoodRestrictions}. ");

        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User, contextText)]
        };
    }

    // No StoreAIContextAsync override — profile writes remain in business logic
}
```

### Illustrative Code Sketch — `ConversationTurnContextProvider`

```csharp
internal sealed class ConversationTurnContextProvider : AIContextProvider
{
    private readonly VectorStoreCollection<Guid, ConversationTurnRecord> _turns;
    private readonly string _username;
    private readonly string _sessionId;
    private readonly string _planContext;
    private readonly ProviderSessionState<TurnState> _sessionState;

    public ConversationTurnContextProvider(
        VectorStoreCollection<Guid, ConversationTurnRecord> turns,
        string username, string sessionId, string planContext)
        : base(null, null)
    {
        _turns = turns;
        _username = username;
        _sessionId = sessionId;
        _planContext = planContext;
        _sessionState = new ProviderSessionState<TurnState>(
            _ => new TurnState(), GetType().Name);
    }

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        state.TurnNumber++;
        _sessionState.SaveState(context.Session, state);

        string userMessage = context.RequestMessages
            .FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        string agentResponse = context.ResponseMessages
            ?.FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? "";

        await _turns.UpsertAsync(new ConversationTurnRecord
        {
            Id = Guid.NewGuid(),
            SessionId = _sessionId,
            TurnNumber = state.TurnNumber,
            Username = _username,
            UserMessage = userMessage,
            AgentResponse = agentResponse,
            Timestamp = DateTime.UtcNow.ToString("o"),
            PlanContext = _planContext
        }, cancellationToken);
    }

    public class TurnState { public int TurnNumber { get; set; } }
}
```

---

## 5. Key Learnings & Achievements

Studying the V7 → V8 transition delivers the following concrete learnings:

### L1 — Pipeline Hooks vs. Manual Orchestration
**Learning:** Context providers replace scattered, boilerplate code (4× `Build*Memory()` helpers + manual `UpsertAsync()` in a while-loop) with a pipeline that runs automatically around every invocation. The developer stops thinking about "when to call" and starts thinking about "what to inject / what to store."

### L2 — Agent-Scoped vs. Session-Scoped State
**Learning:** A provider instance is shared across all sessions of an agent. Per-session data must live in `AgentSession` via `ProviderSessionState<T>`. This distinction prevents subtle state-bleed bugs in multi-user or concurrent scenarios — a mistake V7 could have made if memory helpers stored user-specific state on the agent itself.

### L3 — Always-On Injection vs. On-Demand Tool Calls
**Learning:** Context providers inject on every invocation. Agent tools fire only when the agent decides to call them. For memory lookup (`search_user_memory`), the tool pattern is more token-efficient and gives the agent discretion about relevance. Knowing when to use each pattern is a key architectural judgment.

### L4 — Provider Composability
**Learning:** Multiple providers stack on one agent (`AIContextProviders = [profileProvider, turnProvider, semanticProvider]`). Each handles one concern. This replaces the V7 monolithic `BuildAdvisorMemory()` that combined profile, notes, and restrictions into one string. Composability improves testability: each provider can be unit-tested independently.

### L5 — Message Source Tracking
**Learning:** Messages injected by providers are stamped with `AgentRequestMessageSourceType.AIContextProvider`. The advanced `InvokingCoreAsync` / `InvokedCoreAsync` overrides can filter by source, preventing feedback loops where a provider accidentally re-processes its own previously injected messages as user input.

### L6 — What Context Providers Cannot Replace
**Learning:** Not everything belongs in a provider. Pre-session decisions (new-user vs. returning-user branching), business-logic-conditioned writes (profile upsert after plan approval), session-end pipelines (memory consolidation), and multi-step agentic workflows remain outside the provider model. Recognising these boundaries is as important as knowing the API.

### L7 — Structured Output Compatibility
**Learning:** When an agent uses `RunAsync<T>()` for structured output, provider-injected messages must be placed carefully. Appending large context blocks after the instruction can interfere with the model's structured output compliance. Providers should inject as `ChatRole.User` context messages, not override the instruction, to preserve output format reliability.

---

## 6. RC4 API Notes (Implementation-Level)

RC4 (`Microsoft.Agents.AI` 1.0.0-rc4) has minor differences from the public docs examples:

| Docs | RC4 Reality |
|------|-------------|
| `public override string StateKey` | `public override IReadOnlyList<string> StateKeys` (plural) |
| `ProviderSessionState<T>` | Not present in RC4 — use `AgentSession.StateBag.SetValue<T>()` / `TryGetValue<T>()` directly |
| `TryGetValue<T>` accepts value types | RC4 constraint: `T` must be a **reference type** — wrap primitives (e.g. `int`) in a class |
| `AgentSession.StateBag` indexer `[key] = value` | RC4 `AgentSessionStateBag` has no indexer — use `SetValue<T>(key, value)` |

These differences are correctly handled in `MealPlannerV8.cs`.

---

## Summary Table

| V7 Phase | Context Provider Fit | V8 Action |
|----------|---------------------|-----------|
| Profile injection (Build*Memory) | **High** | Replace with `UserProfileContextProvider` |
| Conversation turn persistence | **High** | Replace with `ConversationTurnContextProvider` |
| Semantic notes → planner/advisor context | **Medium** | Introduce `SemanticMemoryContextProvider` (optional) |
| Episodic recall injection (advisor) | **Medium** | Inject in provider on first turn (optional; V7 console UX may be preserved) |
| Narrative preview (streaming) | **Low** | Keep as-is; streaming + one-shot doesn't benefit from store hook |
| Diet knowledge RAG tool | **Low** | Keep as tool — adaptive retrieval is more efficient |
| `search_user_memory` tool | **Low** | Keep as tool — agent discretion over injection timing |
| Profile + plan history upsert (Phase 8) | **Not applicable** | Keep in business logic — conditioned on plan approval |
| Meal indexing (Phase 9) | **Not applicable** | Keep as data pipeline |
| Memory consolidation (Phase 11) | **Low** | Keep as standalone end-of-session pipeline |
