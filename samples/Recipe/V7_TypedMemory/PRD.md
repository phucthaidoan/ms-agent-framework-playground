# PRD: Meal Planner — V7: Typed Memory Architecture

## Learning Goal

Replace V6's single flat `UserMemoryRecord` with a **typed memory architecture** based on the official Microsoft Foundry Agent Service memory model — three distinct collections, each with a clear memory type, different persistence rules, and targeted retrieval.

**New AF concepts:**
- **Memory taxonomy** — User Profile, Episodic, and Semantic memory as separate collections
- **`VectorSearchOptions.Filter`** — lambda-based scoped retrieval that returns only one user's records
- **"One document per turn"** pattern — conversation turns stored individually and durably
- **Memory Processing Pipeline** — Extract → Consolidate two-agent pattern replaces raw string appending
- **Advisor pulls memory on demand** — `search_user_memory` tool replaces static memory dump in instructions
- **Jaeger / OpenTelemetry tracing** — all agent calls traced end-to-end via OTLP exporter

---

## What Changes vs V6

| Aspect | V6 (User Memory) | V7 (Typed Memory) |
|---|---|---|
| Memory storage | One flat `UserMemoryRecord` per user | Three typed collections: `meal_user_profile`, `meal_conversation_turns`, `meal_semantic_notes` |
| Advisor notes | Pipe-separated string, cap 5, raw append | Individual `SemanticNoteRecord` per fact, typed by NoteType |
| Plan history | JSON-packed string inside profile record | `SemanticNoteRecord` with `NoteType = "plan_history"` |
| Conversation durability | `List<string>` in process — lost on exit | `ConversationTurnRecord` per turn — durable, persisted immediately |
| Advisor memory access | Static context string injected at startup | `search_user_memory` tool — advisor retrieves what it needs |
| Episodic recall | None | "In your last session (date), you discussed: ..." from live retrieval |
| Memory consolidation | String-manipulation deduplication | `MemoryConsolidationAgent` — semantic dedup + conflict resolution via structured output |
| Scoped retrieval | All or nothing | `VectorSearchOptions.Filter = r => r.Username == username` — user-scoped per query |
| Vector field | Mixed dietary identity + advisor phrases | Profile vector = dietary identity only; notes live in separate collection |

---

## Memory Taxonomy (from Microsoft Foundry Agent Service)

Microsoft defines three memory types for agents. V7 maps each to a dedicated collection:

| Microsoft Memory Type | V7 Collection | Role |
|---|---|---|
| **User Profile Memory** | `meal_user_profile` | Static/slow-changing identity: name, diet defaults, food restrictions, goals. Loaded once at session start. |
| **Episodic Memory** | `meal_conversation_turns` | Record of specific past exchanges. One record per advisor turn. Enables "what did we discuss last time?" |
| **Semantic Memory** | `meal_semantic_notes` | General facts about the user not tied to a specific session. One record per fact (preference, dislike, plan history). |

The two existing session-scoped collections from V5 are unchanged:
- `meal_diet_profiles` — domain knowledge (diet rules)
- `meal_plan_meals` — current session's approved plan

---

## New Vector Store Schemas

### `meal_user_profile` — User Profile Memory

```csharp
class UserProfileRecord
{
    [VectorStoreKey]   Guid    Id
    [VectorStoreData]  string  Username
    [VectorStoreData]  string  PreferredDietType
    [VectorStoreData]  int     DefaultCalories
    [VectorStoreData]  double  DefaultBudget
    [VectorStoreData]  int     DefaultPlanDays
    [VectorStoreData]  string  FoodRestrictions      // append-only
    [VectorStoreData]  string  WeightGoal
    [VectorStoreData]  string  LastSessionDate       // ISO 8601
    [VectorStoreData]  int     TotalSessionsCount

    // KEY CONCEPT: vector encodes dietary identity ONLY.
    // V6 polluted this field with advisor phrases — here it is clean.
    [VectorStoreVector(1536)]
    string Vector => $"User {Username}: {PreferredDietType} diet, {DefaultCalories} kcal,
                      ${DefaultBudget} budget. Restrictions: {FoodRestrictions}. Goal: {WeightGoal}."
}
```

### `meal_conversation_turns` — Episodic / Working Memory

```csharp
// KEY CONCEPT: "One document per turn" — Microsoft Cosmos DB recommended pattern.
// Each exchange stored individually and immediately — not buffered until session end.
// Enables both recency-based retrieval (last K turns) and semantic search over history.
class ConversationTurnRecord
{
    [VectorStoreKey]   Guid    Id
    [VectorStoreData]  string  SessionId       // groups all turns from one session
    [VectorStoreData]  int     TurnNumber      // ordering within a session
    [VectorStoreData]  string  Username        // filter key for scoped retrieval
    [VectorStoreData]  string  UserMessage
    [VectorStoreData]  string  AgentResponse
    [VectorStoreData]  string  Timestamp       // ISO 8601
    [VectorStoreData]  string  PlanContext     // "Keto, 3 days, $50" — self-contained

    [VectorStoreVector(1536)]
    string Vector => $"User asked: {UserMessage} | Advisor replied: {AgentResponse}
                      | Context: {PlanContext}"
}
```

### `meal_semantic_notes` — Semantic Memory

```csharp
// KEY CONCEPT: one record per fact — individually searchable, typed, replaceable.
// Replaces V6's pipe-separated AdvisorNotes and DislikedIngredients strings.
// NoteType allows targeted retrieval: search for "dislike" notes vs "preference" notes.
class SemanticNoteRecord
{
    [VectorStoreKey]   Guid    Id
    [VectorStoreData]  string  Username        // filter key for scoped retrieval
    [VectorStoreData]  string  NoteType        // "dislike" / "preference" / "plan_history"
    [VectorStoreData]  string  Content         // "avoid avocado"
    [VectorStoreData]  string  Source          // "extraction" or "consolidation"
    [VectorStoreData]  string  CreatedDate     // ISO 8601

    [VectorStoreVector(1536)]
    string Vector => $"User {Username} {NoteType}: {Content}"
}
```

---

## New Agents

### `MemoryConsolidationAgent`

Replaces V6's `PreferencesExtractionAgent`. Runs a two-step pipeline at session end:

**Step 1 — Extract** (same as V6)
```
Instructions: "Extract user's stated preferences, dislikes, or requests from this
conversation. Return a comma-separated list of concise phrases or empty string."
Input: joined user turns from this session
Output: "avoid mushrooms, prefer salmon lunches, larger breakfast"
```

**Step 2 — Consolidate** (NEW)
```
Instructions: "You are a memory consolidation agent. Merge new phrases with existing notes.
Deduplicate semantically equivalent facts ('no avocado' = 'avoid avocado').
Resolve conflicts — newer fact wins. Classify each note: 'dislike', 'preference', or 'plan_history'.
Return JSON: { NotesToAdd: [{NoteType, Content}], NoteIdsToDelete: [guids] }"
Input: extracted phrases + existing SemanticNoteRecords as JSON
Output: ConsolidatedMemory (structured)
```

This is `RunAsync<ConsolidatedMemory>` — same structured output pattern as planner/critic.

### `UserMemorySearchTool`

New tool registered on the advisor agent. Wraps both `meal_conversation_turns` and `meal_semantic_notes`, searching both with `VectorSearchFilter` scoped to `Username`.

```
Tool name: "search_user_memory"
Description: "Search the user's past conversation turns and preference notes.
Use when the user mentions past sessions, past preferences, dislikes, or dietary history."
```

---

## Agents

| Agent | Role | Tools | Output | Change in V7 |
|---|---|---|---|---|
| `NarrativeAgent` | Streams prose preview | None | Streaming tokens | Memory context built from `SemanticNoteRecord` query |
| `MealPlannerAgent` | Generates and refines plans | `search_diet_knowledge` | `RunAsync<MealPlan>` | Memory context built from `SemanticNoteRecord` query |
| `NutritionCriticAgent` | Validates compliance | None | `RunAsync<NutritionCritique>` | Restriction check built from `UserProfileRecord` |
| `MealAdvisorAgent` | Answers follow-up questions | `search_meal_plan`, `search_user_memory` | `RunAsync` | Gains `search_user_memory` tool; per-turn storage |
| `ExtractionAgent` | Extracts preference phrases | None | `RunAsync` (string) | Unchanged |
| `MemoryConsolidationAgent` | Deduplicates + classifies notes | None | `RunAsync<ConsolidatedMemory>` | **NEW** — replaces string-manipulation dedup |

---

## Key New API: `VectorSearchOptions.Filter`

This is the only new API surface introduced in V7. Every other pattern already exists in V6.

```csharp
// KEY CONCEPT: scoped retrieval — restricts vector search to one user's records.
// Without this filter, all users' turns would be searched together.
// Filter is a lambda expression — pre-filters before semantic ranking.
VectorSearchOptions<ConversationTurnRecord> options = new()
{
    Filter = r => r.Username == username
};

await foreach (VectorSearchResult<ConversationTurnRecord> hit in
    turnCollection.SearchAsync(query, top: 20, options))
{
    // guaranteed: only this user's records
}
```

> **Note:** The older `OldFilter = new VectorSearchFilter().EqualTo(...)` API is obsolete as of `Microsoft.Extensions.VectorData` 9.5+. Use the lambda `Filter` property instead.

---

## Memory Lifecycle

### What is never deleted

| Collection | Cleared when |
|---|---|
| `meal_user_profile` | Never |
| `meal_conversation_turns` | Never globally — scoped by Username filter |
| `meal_semantic_notes` | Individual stale notes replaced by consolidation agent |

### What is session-scoped (cleared each run)

| Collection | Cleared when |
|---|---|
| `meal_diet_profiles` | User answers Y to "Refresh knowledge?" |
| `meal_plan_meals` | Every session start (`EnsureCollectionDeletedAsync`) |

---

## Flow

```
[NEW] sessionId = Guid.NewGuid().ToString()
[NEW] turnNumber = 0
      │
      ▼
[User identity lookup]
  SearchAsync on meal_user_profile (score ≥ 0.85)
      │
      ▼
[NEW — Episodic display for returning users]
  SearchAsync on meal_conversation_turns (Username filter, top: 20)
  → Sort by Timestamp descending → find latest SessionId → display its turns
  → "In your last session (date), you discussed: ..."
      │
      ▼
[NEW — Semantic notes retrieval]
  SearchAsync on meal_semantic_notes (Username filter, top: 10)
  → Build memoryForPlanner + advisorMemoryContext from retrieved notes
    (replaces reading packed fields from UserMemoryRecord)
      │
      ▼
User enters: diet, days, calories, budget  (pre-filled from UserProfileRecord)
      │
      ▼
NarrativeAgent.RunStreamingAsync  (instructions + restrictions + notes)
      │
      ▼
Host-orchestrated refinement loop  (unchanged from V6)
  plannerAgent calls search_diet_knowledge
  criticAgent checks restriction #7 from UserProfileRecord.FoodRestrictions
      │
CheckBudget / PrintMealPlan
      │
      ▼
[First memory write — MODIFIED]
  UpsertAsync(UserProfileRecord) → meal_user_profile
  UpsertAsync(new SemanticNoteRecord { NoteType="plan_history" }) → meal_semantic_notes
    ← replaces JSON-packed PlanHistorySummary string in V6
      │
      ▼
[Post-plan indexing — unchanged]
  UpsertAsync each MealRecord → meal_plan_meals
      │
      ▼
MealAdvisorAgent session created
  → tools: search_meal_plan + search_user_memory  [search_user_memory is NEW]
Loop:
  User types question
    → advisorAgent.RunAsync(question, advisorSession)
      [may call search_meal_plan — V5 unchanged]
      [may call search_user_memory — NEW: retrieves from turns + notes with filter]
  [NEW] UpsertAsync(ConversationTurnRecord) → meal_conversation_turns
  turnNumber++
  User types "exit"
      │
      ▼
[Phase 11 — Two-agent pipeline — MODIFIED]
  Step 1: ExtractionAgent → extracted phrases string
  Step 2: Load existing SemanticNoteRecords (Username filter, top: 20)
  Step 3: ConsolidationAgent.RunAsync<ConsolidatedMemory>(phrases + existing JSON)
  Step 4: UpsertAsync each note in NotesToAdd → meal_semantic_notes
          Mark NoteIdsToDelete as "[merged]"
  Output: "[MEMORY] N note(s) consolidated."
```

---

## Console Output Shape

```
==============================
 Meal Planner V7 — Typed Memory Architecture
==============================

Enter your name (new or returning user): Alice

[MEMORY] Searching user profile for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-28, 2 sessions total):
  Diet        : Keto
  Calories    : 2000 kcal/day
  Budget      : $50 USD
  Plan days   : 3
  Restrictions: no nuts

[MEMORY] Recalling last session...
  → In your last session (2026-03-28), you discussed:
    • "I don't like avocado, can we use something else?"
    • "What does Day 1 breakfast look like?"

Describe your diet preference (press Enter to keep 'Keto'): [Enter]

[... plan generation unchanged ...]

[MEMORY] Plan preferences saved for alice.
[MEMORY] Plan history note stored.

[RAG] 9 meals indexed for advisor queries.

Step 5: Meal Plan Advisor
> What did I ask about last time?
[MEMORY] Searching user memory for: "what did I ask last time"
  → Past turn [Keto, 3 days, $50] (score: 0.921)
  → Semantic note [dislike]: avoid avocado (score: 0.887)
Last session you asked about avocado substitutes in breakfast.
I suggested replacing avocado with extra cheese or sour cream.
Your preference to avoid avocado has been noted for this plan as well.

> exit

[MEMORY] Extracting preferences from advisor conversation...
[MEMORY] Loading existing notes for consolidation...
  → 2 existing notes loaded
[MEMORY] Consolidating with MemoryConsolidationAgent...
  → 1 note(s) added, 0 note(s) replaced
[MEMORY] Memory consolidated.

==============================
Session ended. Meal planning complete.
```

---

## Key Code Patterns to Learn

**Scoped retrieval with VectorSearchOptions:**
```csharp
// KEY CONCEPT: Filter lambda restricts semantic search to one user's records.
// This is how multi-user isolation works within a shared collection.
VectorSearchOptions<SemanticNoteRecord> options = new()
{
    Filter = r => r.Username == username
};
await foreach (var hit in noteCollection.SearchAsync("dislikes preferences", 10, options))
    // guaranteed: only this user's notes
```

**Per-turn storage:**
```csharp
// KEY CONCEPT: "one document per turn" — stored immediately, not buffered.
AgentResponse response = await advisorAgent.RunAsync(input, advisorSession);
await turnCollection.UpsertAsync(new ConversationTurnRecord
{
    Id = Guid.NewGuid(), SessionId = sessionId, TurnNumber = ++turnNumber,
    Username = username, UserMessage = input, AgentResponse = response.Text,
    Timestamp = DateTime.UtcNow.ToString("o"),
    PlanContext = $"{dietDescription}, {numberOfDays} days, ${budget}"
});
```

**Two-agent consolidation pipeline:**
```csharp
// KEY CONCEPT: Memory Processing Pipeline — Extract then Consolidate.
// Phase 1: extraction (what was said this session)
AgentResponse extractResult = await extractionAgent.RunAsync(sessionTranscript);

// Phase 2: consolidation (merge with existing, deduplicate, resolve conflicts)
string consolidationInput =
    $"New phrases: {extractResult.Text}\nExisting notes: {existingNotesJson}";
AgentResponse<ConsolidatedMemory> result =
    await consolidationAgent.RunAsync<ConsolidatedMemory>(consolidationInput);

// Phase 3: apply
foreach (NoteToAdd note in result.Result.NotesToAdd)
    await noteCollection.UpsertAsync(new SemanticNoteRecord { ... });
```

**search_user_memory tool:**
```csharp
// KEY CONCEPT: advisor pulls memory on demand — no static dump in instructions.
// Searches both episodic (turns) and semantic (notes) collections.
AIFunctionFactory.Create(
    memorySearchTool.Search,
    "search_user_memory",
    "Search past conversation turns and preference notes. Use when user references past sessions or preferences.")
```

---

## Walkthrough Scenarios

Each scenario is a full session transcript showing exact prompts and expected console output. Run them in order — each builds on state left by the previous.

---

### Scenario 1 — First-Time User (Alice)

**What to observe:** No memory exists in any of the three typed collections. Full input prompts shown. Food restriction captured. After session end, `meal_user_profile`, `meal_semantic_notes` (plan history + any dislike), and `meal_conversation_turns` are all written for the first time.

```
==============================
 Meal Planner V7 — Typed Memory Architecture
==============================

Enter your name (new or returning user): Alice

[MEMORY] Searching user profile for: "alice"
  → No match above threshold (score: 0.201) — new user

Welcome! This is your first session, Alice.
Let's set up your meal preferences.

==============================
Import/refresh diet knowledge base? (Y/N): N

Diet preference (e.g. 'Keto', 'Vegan', 'Mediterranean'): Keto
Days to plan? (1–7): 3
Daily calories? (Enter for 2000): [Enter]
Budget USD? (Enter for $50): [Enter]
Food restrictions / allergies? (Enter to skip): no nuts

==============================
User     : alice
Diet     : Keto
Days     : 3
Calories : 2000 kcal/day
Budget   : $50.00 USD
Restrict : no nuts
==============================

Step 1: Streaming plan preview...
For a 3-day Keto plan targeting 2000 kcal within a $50 budget, I'll focus on
high-fat proteins and low-carb vegetables...
[tokens stream]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
[RAG] Searching diet knowledge for: "Keto high fat low carb"
  → Retrieved: Keto (score: 0.921)
[PLANNER] Initial plan generated (3 days, est. $44.20)

[ITERATION 1/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓

[BUDGET CHECK] Estimated total: $44.20 — Within limit of $50.00 ✓

Step 4: Final Meal Plan
[plan table — no nuts in any meal]

==============================
[MEMORY] Plan preferences saved for alice.
[MEMORY] Plan history note stored.

[RAG] 9 meals indexed for advisor queries.

Step 5: Meal Plan Advisor
> I don't like avocado, can we avoid it next time?
[RAG] Searching meal plan for: "avocado"
  → Retrieved: Day 1 Breakfast — Scrambled Eggs with Avocado (score: 0.887)
Of course! I can substitute extra cheese or sour cream next time for similar fat content.

> exit

[MEMORY] Extracting preferences from advisor conversation...
[MEMORY] Loading existing notes for consolidation...
  → 0 existing notes loaded
[MEMORY] Consolidating with MemoryConsolidationAgent...
  → 1 note(s) added, 0 note(s) replaced
[MEMORY] Memory consolidated.

==============================
Session ended. Meal planning complete.
```

**Memory state after Scenario 1:**
```
meal_user_profile:
  alice — Keto, 2000 kcal, $50, 3 days, no nuts, 1 session

meal_semantic_notes:
  [plan_history]  Keto 3-day plan, $44.20 total (2026-03-28)
  [dislike]       avoid avocado

meal_conversation_turns:
  Turn 1 — "I don't like avocado..." / "Of course! I can substitute..."
```

---

### Scenario 2 — Returning User + Episodic Recall (Alice, Session 2)

**What to observe:** Profile pre-fills all inputs. Episodic recall section shows Alice's last turn. `search_user_memory` tool fires when Alice asks "what did I say last time?". Consolidation deduplicates "avoid avocado" — only 1 note remains, not 2.

```
Enter your name (new or returning user): Alice

[MEMORY] Searching user profile for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-28, 1 session total):
  Diet        : Keto
  Calories    : 2000 kcal/day
  Budget      : $50 USD
  Plan days   : 3
  Restrictions: no nuts

[MEMORY] Recalling last session...
  → In your last session (2026-03-28), you discussed:
    • "I don't like avocado, can we avoid it next time?"

==============================
Import/refresh diet knowledge base? (Y/N): N

Diet? (Enter to keep 'Keto'): [Enter]
Days to plan? (Enter to keep 3): [Enter]
Daily calories? (Enter to keep 2000): [Enter]
Budget USD? (Enter to keep $50): [Enter]

[plan generation — avocado excluded via semantic note in planner context]

Step 5: Meal Plan Advisor
> What did I ask about last time?
[MEMORY] Searching user memory for: "what did I ask last time"
  → Past turn [Keto, 3 days, $50] (score: 0.921)
  → Semantic note [dislike]: avoid avocado (score: 0.887)
Last session you asked about avocado substitutes.
I suggested replacing avocado with extra cheese or sour cream.
Your preference to avoid avocado has been noted for this plan as well.

> exit

[MEMORY] Extracting preferences from advisor conversation...
[MEMORY] Loading existing notes for consolidation...
  → 2 existing notes loaded
[MEMORY] Consolidating with MemoryConsolidationAgent...
  → 0 note(s) added, 0 note(s) replaced
  (dedup: "avoid avocado" already exists — no new note created)
[MEMORY] Memory consolidated.
```

**Key V7 concept demonstrated:** `Filter = r => r.Username == "alice"` scopes both the turn and note search — Bob's data (if any) is never returned here.

---

### Scenario 3 — Conflicting Preference Resolution (Alice, Session 3)

**What to observe:** Alice says she now *likes* avocado. In the advisor session, she says "I love avocado, please add it back". Consolidation agent receives the new phrase AND the existing `[dislike] avoid avocado` note. Newer fact wins — the dislike note is marked `[merged]` and replaced with `[preference] include avocado`.

```
Enter your name (new or returning user): Alice

[MEMORY] Searching user profile for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-28, 2 sessions total):
[... pre-fills ...]

[MEMORY] Recalling last session...
  → In your last session (2026-03-28), you discussed:
    • "What did I ask about last time?"

[plan generation]

Step 5: Meal Plan Advisor
> I love avocado now, please include it in future plans
Of course! I'll make sure avocado features in your upcoming meal plans.

> exit

[MEMORY] Extracting preferences from advisor conversation...
  → Extracted: "include avocado"
[MEMORY] Loading existing notes for consolidation...
  → 2 existing notes loaded
[MEMORY] Consolidating with MemoryConsolidationAgent...
  → 1 note(s) added, 1 note(s) replaced
  (conflict resolved: "avoid avocado" → "[merged]"; new: [preference] include avocado)
[MEMORY] Memory consolidated.
```

**Memory state after Scenario 3:**
```
meal_semantic_notes:
  [plan_history]  Keto 3-day plan (session 1)
  [plan_history]  Keto 3-day plan (session 2)
  [plan_history]  Keto 3-day plan (session 3)
  [dislike]       avoid avocado  ← Content now "[merged]", Source="consolidation"
  [preference]    include avocado ← NEW, wins the conflict
```

---

### Scenario 4 — Second User Isolation (Bob, First Time)

**What to observe:** Bob enters his name. Profile lookup returns no match. Bob's advisor session never sees Alice's turns or notes — `VectorSearchFilter` ensures cross-user isolation. After Bob's session, his data is written separately.

```
Enter your name (new or returning user): Bob

[MEMORY] Searching user profile for: "bob"
  → No match above threshold (score: 0.181) — new user

Welcome! This is your first session, Bob.
Let's set up your meal preferences.

Diet preference: Mediterranean
Days to plan?: 5
Daily calories? (Enter for 2000): [Enter]
Budget USD? (Enter for $50): 80
Food restrictions / allergies? (Enter to skip): no shellfish

[plan generation for Bob]

Step 5: Meal Plan Advisor
> What did Alice ask last session?
[MEMORY] Searching user memory for: "what did Alice ask"
  → No results (Username filter: "bob" — Alice's turns not visible)
I don't have any record of a user named Alice in your history.
I can only access your own past sessions and preferences.

> exit

[MEMORY] Extracting preferences from Bob's advisor conversation...
[MEMORY] Loading existing notes for consolidation...
  → 0 existing notes loaded
[MEMORY] Consolidating with MemoryConsolidationAgent...
  → 0 note(s) added
[MEMORY] Memory consolidated.
```

**Key V7 concept demonstrated:** `Filter = r => r.Username == "bob"` means Alice's conversation turns and notes are completely invisible to Bob's advisor. This is how multi-tenant isolation works within a shared SQLite collection.

---

### Scenario 5 — Plan History as Semantic Note

**What to observe:** After each session, a `plan_history` typed `SemanticNoteRecord` is written to `meal_semantic_notes`. The planner's memory context in Session 4 includes previous plan summaries, which causes it to vary meals instead of repeating the same dishes.

```
[After running Alice through 3 sessions, run Alice's 4th session]

Enter your name (new or returning user): Alice

[MEMORY] Searching user profile for: "alice"
  → Retrieved: alice (score: 0.923)

[MEMORY] Loading semantic notes...
  → 5 notes retrieved for alice (top 10, Username filter)

[planner context includes:]
  Plan history:
    - 2026-03-28: Keto 3-day plan ($44.20) — eggs, bacon, avocado
    - 2026-03-28: Keto 3-day plan ($42.10) — salmon, cheese, spinach
    - 2026-03-28: Keto 3-day plan ($46.80) — chicken, butter, cauliflower

[PLANNER] Plan generated — meals varied to avoid repetition from history
```

**Key V7 concept demonstrated:** `NoteType = "plan_history"` separates plan history from preference notes — targeted retrieval can filter *only* plan history or *only* dislikes via the `Filter` lambda. In V6, all this was jammed into a single JSON-packed string field.

---

## Observability: Jaeger / OpenTelemetry Tracing

V7 adds end-to-end tracing so every agent call — planner, critic, advisor, extraction, consolidation — appears as a span in Jaeger.

### Setup

```csharp
// 1. Start Jaeger container (OTLP gRPC on 4317, UI on 16686)
var jaeger = new ContainerBuilder("jaegertracing/all-in-one:latest")
    .WithPortBinding(16686, 16686)
    .WithPortBinding(4317, 4317)
    .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
    .Build();
await jaeger.StartAsync();

// 2. Configure TracerProvider
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MealPlannerV7"))
    .AddSource("MealPlannerV7")
    .AddOtlpExporter(opt => opt.Endpoint = new Uri("http://localhost:4317"))
    .Build();

// 3. Root span wraps the entire session
var activitySource = new ActivitySource("MealPlannerV7");
using var rootSpan = activitySource.StartActivity("MealPlannerSession");

// 4. All agents share one IChatClient with OTel instrumentation
IChatClient chatClient = openAIClient
    .GetChatClient("gpt-4.1-nano")
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: "MealPlannerV7", configure: c => c.EnableSensitiveData = true)
    .Build();
```

### What appears in Jaeger

| Span | Triggered by |
|---|---|
| `MealPlannerSession` | Root — entire session |
| `chat gpt-4.1-nano` (×N) | Each `RunAsync` / `RunStreamingAsync` call |
| Tool call child spans | `search_diet_knowledge`, `search_meal_plan`, `search_user_memory` |

View at `http://localhost:16686` → search for service `MealPlannerV7`.

---

## Known Issues & Fixes Applied

These bugs were discovered during multi-session testing and fixed in the implementation.

### Bug 1 — Episodic recall showed only current-session turns after day 2

**Root cause:** `SearchAsync("meal plan conversation", top: 3)` returns the 3 most *semantically similar* turns. After session 2, session 2's turns dominated the top 3 by similarity score, pushing session 1 turns out entirely.

**Fix:** Retrieve `top: 20`, sort ALL results by `Timestamp` descending (ISO 8601 sorts lexicographically), find the latest `SessionId`, then display only that session's turns in `TurnNumber` order.

```csharp
// Before (broken): only 3 most similar turns — misses older sessions
collection.SearchAsync("meal plan conversation", 3, options)

// After (fixed): wide retrieval, then recency sort
var turns = /* SearchAsync top:20 */;
string latestSessionId = turns.OrderByDescending(t => t.Timestamp).First().SessionId;
var lastSession = turns.Where(t => t.SessionId == latestSessionId).OrderBy(t => t.TurnNumber);
```

### Bug 2 — `plan_history` notes missing from consolidation

**Root cause:** The consolidation step queried existing notes with `"preferences dislikes"`. `plan_history` notes (e.g. "Keto 3-day plan, $44.20") scored too low against this query and were excluded.

**Fix:** Broadened query to `"user preferences dislikes plan history notes"` so all note types rank high enough to be retrieved.

---

## Out of Scope

- Deleting individual conversation turns (no explicit delete UI)
- TTL-based expiry of old turns (would require Cosmos DB or custom cleanup job)
- Cross-session conversation continuity (advisor session still starts fresh per run)
- Displaying full past conversation history (only last 3 relevant turns shown)
