# PRD: Meal Planner — V6: Per-User Memory

## Learning Goal

Extend the RAG-powered planner with **persistent per-user memory** — so returning users skip repetitive input, personal restrictions flow into agent instructions and validation, and advisor conversations contribute to future sessions.

**New AF concept:** User memory as a vector store collection — the same `SearchAsync` / `UpsertAsync` pattern applied not to domain knowledge or plan content, but to user-owned, session-spanning state.

---

## What Changes vs V5

| Aspect | V5 (RAG) | V6 (User Memory) |
|---|---|---|
| User identity | None — stateless, anonymous each run | Username entered at startup; looked up via `meal_user_memory` vector store |
| Preferences | Re-entered every run | Saved after each session; pre-fill prompts on return |
| Food restrictions | Not tracked | Persisted per user; enforced by planner (hard) and critic (check #7) |
| Dislikes | Not tracked | Extracted from advisor conversation by `PreferencesExtractionAgent`; influence next session's planner |
| Plan history | Not tracked | Last 5 plan summaries stored; planner uses them to vary meals across sessions |
| Advisor notes | Not tracked | Last 5 preference phrases extracted and stored; influence planner and advisor instructions |
| Memory persistence | `meal_plan_meals` deleted each run | `meal_user_memory` never deleted — accumulates across runs |
| Advisor extract | None | `PreferencesExtractionAgent` runs at session end to distil conversation into structured notes |

---

## New Vector Store Collection: `meal_user_memory`

Unlike `meal_diet_profiles` and `meal_plan_meals`, this collection is **never cleared between sessions**. It grows one record per unique user.

### `UserMemoryRecord` Schema

```csharp
class UserMemoryRecord
{
    [VectorStoreKey]   Guid    Id
    [VectorStoreData]  string  Username              // normalised: "alice"
    [VectorStoreData]  string  PreferredDietType     // "Keto"
    [VectorStoreData]  int     DefaultCalories       // 2000
    [VectorStoreData]  decimal DefaultBudget         // 50.00
    [VectorStoreData]  int     DefaultPlanDays       // 3
    [VectorStoreData]  string  FoodRestrictions      // "no nuts, gluten-free"   (comma-separated, append-only)
    [VectorStoreData]  string  DislikedIngredients   // "mushrooms, tofu"        (comma-separated, deduplicated)
    [VectorStoreData]  string  AdvisorNotes          // "more fish lunches|avoid tofu"  (pipe-separated, cap 5)
    [VectorStoreData]  string  PlanHistorySummary    // JSON list of {Date,Diet,Days,Cost}, cap 5
    [VectorStoreData]  string  WeightGoal            // "weight loss" / "maintenance" / "muscle gain"
    [VectorStoreData]  string  LastSessionDate       // ISO 8601
    [VectorStoreData]  int     TotalSessionsCount    // monotonic counter

    [VectorStoreVector(1536)]
    string Vector => $"User {Username}: {PreferredDietType} diet, {DefaultCalories} kcal, " +
                     $"${DefaultBudget} budget. Restrictions: {FoodRestrictions}. " +
                     $"Dislikes: {DislikedIngredients}. Goal: {WeightGoal}."
}
```

**Why a vector field on user memory?**
The vector encodes the user's dietary identity, enabling fuzzy name lookup — `"alice"` reliably retrieves Alice's record even with minor variations. It also demonstrates that the vector store pattern generalises to user state, not just knowledge bases.

---

## New Agent: `PreferencesExtractionAgent`

Runs once at the end of the advisor loop, only if the user had at least one exchange.

**Instructions:**
> "You are a preferences extractor. Read the following meal advisor conversation and extract only the user's stated preferences, dislikes, or requests for future plans. Return a comma-separated list of concise phrases, or an empty string if none found."

**Input:** joined user turns from the advisor conversation
**Output:** e.g. `"more fish-based lunches, avoid tofu, larger breakfast"`

Output is split and appended to `AdvisorNotes` (cap 5, newest first) and `DislikedIngredients` (deduplicated).

---

## Agents

| Agent | Role | Tools | Output | Change in V6 |
|---|---|---|---|---|
| `NarrativeAgent` | Streams prose preview | None | Streaming tokens | Instructions enriched with `FoodRestrictions`, `AdvisorNotes` |
| `MealPlannerAgent` | Generates and refines structured plans | `search_diet_knowledge` | `RunAsync<MealPlan>` | Instructions enriched with restrictions, dislikes, history, notes, goal |
| `NutritionCriticAgent` | Validates compliance and macros | None | `RunAsync<NutritionCritique>` | Check #7 added: flag user-specific restriction violations |
| `MealAdvisorAgent` | Answers follow-up questions | `search_meal_plan` | `RunAsync` with `AgentSession` | Instructions prepended with user context summary |
| `PreferencesExtractionAgent` | Distils advisor transcript into notes | None | `RunAsync` (string) | **NEW** — runs once after advisor exit |

---

## Memory Lifecycle

### Startup — Read

1. Prompt: `"Enter your name (new or returning user): "` → normalise to lowercase, trimmed
2. `SearchAsync(username, top: 1)` on `meal_user_memory`
3. **Score ≥ 0.85 (returning user):**
   - Load `UserMemoryRecord` into `userMemory`
   - Display welcome-back summary (see Console Output Shape)
   - Pre-fill `dietDescription`, `numberOfDays`, `targetCalories`, `budget` from stored defaults
   - Each input prompt becomes: `"Diet? (press Enter to keep 'Keto'): "`
4. **Score < 0.85 (first-time user):**
   - Display: `"Welcome! Let's set up your preferences."`
   - Full input prompts as normal
   - Create a fresh `UserMemoryRecord` with a new `Guid`

The score threshold is printed to the console — `[MEMORY] Retrieved: alice (score: 0.923)` — following the same `[RAG]` visual language already established in V5.

### Agent Instructions — Read

**NarrativeAgent** (if restrictions or notes exist):
```
"The user has the following restrictions: {FoodRestrictions}. Do not mention these foods."
"The user has previously requested: {AdvisorNotes}. Reflect these in your narrative."
```

**PlannerAgent** (if memory loaded):
```
"HARD RESTRICTIONS for this user (allergies/intolerances — must be enforced): {FoodRestrictions}"
"The user dislikes these ingredients — avoid where possible: {DislikedIngredients}"
"Previous plans for this user: {PlanHistorySummary}. Aim for variety — do not repeat meals."
"Past feedback from this user: {AdvisorNotes}."
"User goal: {WeightGoal}."
```

**CriticAgent** (if `FoodRestrictions` non-empty):
```
"7. USER RESTRICTION CHECK: This user has the following restrictions: {FoodRestrictions}.
    Any meal containing a restricted ingredient must be flagged as a diet violation,
    even if it is otherwise diet-compliant."
```

**AdvisorAgent** (if memory loaded):
```
"User context: {Username} prefers {PreferredDietType}, targets {DefaultCalories} kcal/day,
 budget ${DefaultBudget}. Restrictions: {FoodRestrictions}. Dislikes: {DislikedIngredients}.
 Past requests: {AdvisorNotes}."
```

### After Plan Approved — First Write

1. Overwrite `PreferredDietType`, `DefaultCalories`, `DefaultBudget`, `DefaultPlanDays` with current session values
2. Append `{ Date, Diet, Days, Cost }` to `PlanHistorySummary` — drop oldest if > 5 entries
3. Increment `TotalSessionsCount`; set `LastSessionDate`
4. `UpsertAsync` → `meal_user_memory`
5. Output: `"[MEMORY] Plan preferences saved for {username}."`

### After Advisor Exit — Second Write

1. Run `PreferencesExtractionAgent` on accumulated user turns (if any)
2. Append extracted phrases to `AdvisorNotes` (cap 5, newest first)
3. Append extracted dislikes to `DislikedIngredients` (deduplicate)
4. `UpsertAsync` → `meal_user_memory`
5. Output: `"[MEMORY] Preferences updated from advisor conversation."`

---

## Memory Update Rules

| Field | Rule |
|---|---|
| `PreferredDietType`, `DefaultCalories`, `DefaultBudget`, `DefaultPlanDays` | Overwrite — current session input always wins |
| `FoodRestrictions` | Append-only, never auto-removed (allergies are permanent) |
| `DislikedIngredients` | Append from extraction, deduplicate |
| `AdvisorNotes` | Append newest, cap at 5, oldest dropped |
| `PlanHistorySummary` | Append newest entry, cap at 5, oldest dropped |
| `TotalSessionsCount` | Always increment |
| `LastSessionDate` | Always overwrite |

---

## Flow

```
[Startup — memory lookup]
  "Enter your name:"
  → SearchAsync(username) on meal_user_memory
  → IF score ≥ 0.85: display welcome-back summary, pre-fill inputs
  → IF score < 0.85: first-time user, full input prompts, new UserMemoryRecord
      │
      ▼
User enters: diet, days, calories, budget  (pre-filled for returning users)
      │
      ▼
NarrativeAgent.RunStreamingAsync(previewPrompt)
  → instructions + FoodRestrictions + AdvisorNotes
      │
      ▼
Host-orchestrated refinement loop:
  plannerAgent.RunAsync<MealPlan>(prompt)
    └── LLM calls: search_diet_knowledge(...)      [V5 concept]
    └── instructions + FoodRestrictions + DislikedIngredients + PlanHistorySummary + AdvisorNotes
  criticAgent.RunAsync<NutritionCritique>(planJson)
    └── check #7: user restriction violations      [NEW]
  if not approved → plannerAgent refines with feedback
      │
CheckBudget / PrintMealPlan
      │
      ▼
[First memory write]
  UpsertAsync(UserMemoryRecord) — preferences + plan history updated
  Output: "[MEMORY] Plan preferences saved for {username}."
      │
      ▼
[Post-plan indexing — V5 concept unchanged]
  UpsertAsync each MealRecord → meal_plan_meals
      │
      ▼
MealAdvisorAgent session created
  → instructions + user context summary    [NEW]
  → search_meal_plan tool                  [V5 concept]
Loop:
  User types question
    → advisorAgent.RunAsync(question, advisorSession)
    → accumulate user turn in advisorTranscript list
  User types "exit"
    → PreferencesExtractionAgent(advisorTranscript)   [NEW]
    → UpsertAsync(UserMemoryRecord) — notes + dislikes updated
    → Output: "[MEMORY] Preferences updated from advisor conversation."
```

---

## Console Output Shape

```
Enter your name (new or returning user): alice

[MEMORY] Searching user memory for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-14, 3 sessions total):
  Diet        : Keto
  Calories    : 2000 kcal/day
  Budget      : $50 USD
  Plan days   : 3
  Restrictions: gluten-free
  Dislikes    : mushrooms, tofu
  Your notes  : "more variety in breakfasts"
Press Enter to keep these preferences, or enter new values below.

Describe your diet preference (press Enter to keep 'Keto'):
How many days to plan? (press Enter to keep 3):
Daily calorie target? (press Enter to keep 2000):
Total budget in USD? (press Enter to keep $50):

[... narrative, plan generation, refinement loop unchanged from V5 ...]

[MEMORY] Plan preferences saved for alice.

[RAG] 9 meals indexed for advisor queries.

Step 5: Meal Plan Advisor (type 'exit' to quit)

> I don't like the mushrooms in Day 2 dinner
[RAG] Searching meal plan for: "Day 2 dinner mushrooms"
  → Retrieved: Day 2 Dinner — Beef and Mushroom Stir-Fry (score: 0.951)
Here is an alternative without mushrooms: ...

> exit

[MEMORY] Extracting preferences from advisor conversation...
  → Extracted: "avoid mushrooms"
[MEMORY] Preferences updated from advisor conversation.

Session ended. Meal planning complete.
```

---

## Key Code Patterns to Learn

**Username lookup with score threshold:**
```csharp
// KEY CONCEPT: similarity score drives a business decision — new vs. returning user
UserMemoryRecord? userMemory = null;
await foreach (VectorSearchResult<UserMemoryRecord> hit in userCollection.SearchAsync(username, 1))
{
    if (hit.Score >= 0.85)
    {
        userMemory = hit.Record;
        Output.Gray($"[MEMORY] Retrieved: {hit.Record.Username} (score: {hit.Score:F3})");
    }
}
```

**Memory-enriched planner instructions:**
```csharp
// KEY CONCEPT: user memory flows into agent instructions — not hardcoded, retrieved per user
string memoryContext = userMemory is null ? string.Empty :
    $"HARD RESTRICTIONS: {userMemory.FoodRestrictions}. " +
    $"Dislikes: {userMemory.DislikedIngredients}. " +
    $"Previous plans: {userMemory.PlanHistorySummary}.";

ChatClientAgent plannerAgent = client.GetChatClient("gpt-4.1-nano")
    .AsAIAgent(instructions: $"You are a professional meal planner. {memoryContext} ...");
```

**Preferences extraction agent:**
```csharp
// KEY CONCEPT: LLM used for extraction, not just generation — distils conversation into structured notes
ChatClientAgent extractionAgent = client.GetChatClient("gpt-4.1-nano")
    .AsAIAgent(instructions:
        "You are a preferences extractor. Extract the user's stated preferences, dislikes, " +
        "or requests from this advisor conversation. Return a comma-separated list of concise phrases, " +
        "or an empty string if none found.");

AgentResponse extractResult = await extractionAgent.RunAsync(string.Join("\n", advisorTranscript));
```

**Upsert for create-or-update:**
```csharp
// KEY CONCEPT: UpsertAsync is idempotent — same Id creates on first run, overwrites on return runs
userMemory.TotalSessionsCount++;
userMemory.LastSessionDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
await userCollection.UpsertAsync(userMemory);
Output.Green($"[MEMORY] Plan preferences saved for {userMemory.Username}.");
```

---

## Walkthrough Scenarios

Each scenario is a full session transcript showing exact prompts and expected console output. Run them in order — each one builds on the state left by the previous.

---

### Scenario 1 — First-Time User (Alice)

**What to observe:** No memory exists. Full input prompts shown. Food restriction captured at setup. Memory saved at the end.

```
==============================
 Meal Planner V6 — Per-User Memory
==============================

Enter your name (new or returning user): Alice

[MEMORY] Searching user memory for: "alice"
  → No match above threshold (score: 0.201) — new user

Welcome! This is your first session, Alice.
Let's set up your meal preferences.

==============================
Import/refresh diet knowledge base? (Y/N): N

Describe your diet preference (e.g. 'Keto', 'plant-based vegan', 'Mediterranean'): Keto
How many days to plan? (1–7): 3
Daily calorie target? (press Enter for 2000): [Enter]
Total budget in USD? (press Enter for $50): [Enter]
Any food restrictions or allergies? (e.g. 'no nuts, gluten-free' or press Enter to skip): no nuts

==============================
User      : alice
Diet      : Keto
Days      : 3
Calories  : 2000 kcal/day
Budget    : $50 USD total
Restrict  : no nuts
==============================

Step 1: Streaming plan preview...
For a 3-day Keto plan targeting 2000 kcal per day within a $50 budget, I will focus on
high-fat proteins and low-carb vegetables...
[tokens stream]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
[RAG] Searching diet knowledge for: "Keto high fat low carbohydrate"
  → Retrieved: Keto (score: 0.921)
[PLANNER] Initial plan generated (3 days, est. $44.20)

[ITERATION 1/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓

[BUDGET CHECK] Estimated total: $44.20 — Within limit of $50.00 ✓

Step 4: Final Meal Plan
[plan table printed — no nuts in any meal]

==============================
[MEMORY] Saving plan preferences for alice...
[MEMORY] Plan preferences saved for alice.

[RAG] Indexing approved meal plan for advisor queries...
[RAG] 9 meals indexed for advisor queries.

Step 5: Meal Plan Advisor (type 'exit' to quit)
Meal plan indexed. Ask your questions (type 'exit' to finish):

> What does Day 1 breakfast look like?
[RAG] Searching meal plan for: "Day 1 breakfast"
  → Retrieved: Day 1 Breakfast — Scrambled Eggs with Avocado (score: 0.944)
Day 1 breakfast is Scrambled Eggs with Avocado: 480 kcal, P:28g C:6g F:38g, $4.50.
Ingredients: eggs, avocado, butter, spinach, cheddar.

> I don't like avocado, can we use something else next time?
[RAG] Searching meal plan for: "avocado substitute breakfast"
  → Retrieved: Day 1 Breakfast — Scrambled Eggs with Avocado (score: 0.887)
Of course! Next time I can replace avocado with extra cheese or sour cream for similar fat content.

> exit

[MEMORY] Extracting preferences from advisor conversation...
  → Extracted: "avoid avocado"
[MEMORY] Preferences updated from advisor conversation.

==============================
Session ended. Meal planning complete.
```

**Memory state after Session 1:**
```
Username           : alice
PreferredDietType  : Keto
DefaultCalories    : 2000
DefaultBudget      : 50
DefaultPlanDays    : 3
FoodRestrictions   : no nuts
DislikedIngredients: avoid avocado
AdvisorNotes       : avoid avocado
PlanHistorySummary : [{"Date":"2026-03-28","Diet":"Keto","Days":3,"Cost":44.20}]
TotalSessionsCount : 1
```

---

### Scenario 2 — Returning User, Same Diet (Alice, Session 2)

**What to observe:** Memory loaded at startup. Inputs pre-filled — Alice just presses Enter for everything. Planner instructions include `no nuts` and `avoid avocado`. Planner also sees Session 1 plan history and varies the meals.

```
==============================
 Meal Planner V6 — Per-User Memory
==============================

Enter your name (new or returning user): Alice

[MEMORY] Searching user memory for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-28, 1 session total):
  Diet        : Keto
  Calories    : 2000 kcal/day
  Budget      : $50 USD
  Plan days   : 3
  Restrictions: no nuts
  Dislikes    : avoid avocado
  Your notes  : "avoid avocado"
Press Enter to keep these preferences, or enter new values below.

==============================
Import/refresh diet knowledge base? (Y/N): N

Describe your diet preference (press Enter to keep 'Keto'): [Enter]
How many days to plan? (press Enter to keep 3): [Enter]
Daily calorie target? (press Enter to keep 2000): [Enter]
Total budget in USD? (press Enter to keep $50): [Enter]

==============================
User      : alice
Diet      : Keto
Days      : 3
Calories  : 2000 kcal/day
Budget    : $50 USD total
Restrict  : no nuts
Dislikes  : avoid avocado
==============================

[... narrative preview ...]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
  ← planner instructions include:
      "HARD RESTRICTIONS: no nuts."
      "The user dislikes: avoid avocado. Avoid where possible."
      "Previous plans: [Keto, 3 days, $44.20 on 2026-03-28]. Aim for variety."
[RAG] Searching diet knowledge for: "Keto high fat low carb"
  → Retrieved: Keto (score: 0.921)
[PLANNER] Initial plan generated (3 days, est. $46.10)
  ← meals differ from Session 1; no avocado; no nuts

[ITERATION 1/3] Calling NutritionCriticAgent...
  ← critic checks include restriction check #7: "no nuts"
[CRITIC] Approved ✓

[MEMORY] Plan preferences saved for alice.

> How does today's plan compare to last week's?
I don't have direct access to your previous plan, but based on your stored notes,
last time you had avocado-based dishes which we've avoided today. Today's plan
focuses on egg, salmon, and beef-based meals for variety.

> exit

[MEMORY] Extracting preferences from advisor conversation...
  → No new preferences detected in conversation.

==============================
Session ended. Meal planning complete.
```

**Memory state after Session 2:**
```
PlanHistorySummary : [{"Date":"2026-03-28","Diet":"Keto","Days":3,"Cost":46.10},
                      {"Date":"2026-03-28","Diet":"Keto","Days":3,"Cost":44.20}]
TotalSessionsCount : 2
AdvisorNotes       : avoid avocado   (unchanged — no new extraction)
```

---

### Scenario 3 — Returning User, Restriction Violation Caught by Critic (Alice, Session 3)

**What to observe:** Alice's `no nuts` restriction flows into the critic as check #7. If the planner accidentally includes nuts in a meal, the critic catches it and forces a refinement.

```
Enter your name (new or returning user): Alice

[MEMORY] Searching user memory for: "alice"
  → Retrieved: alice (score: 0.923)

[... welcome summary, pre-filled prompts, Enter for all ...]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
[RAG] Searching diet knowledge for: "Keto"
  → Retrieved: Keto (score: 0.921)
[PLANNER] Initial plan generated (3 days, est. $43.80)

[ITERATION 1/3] Calling NutritionCriticAgent...
  ← critic instruction includes check #7:
      "USER RESTRICTION CHECK: This user has restrictions: no nuts.
       Any meal containing a restricted ingredient must be flagged as a diet violation."
[CRITIC] Not approved. Issues found:
  • USER RESTRICTION VIOLATION: Day 2 Lunch — Walnut-Crusted Salmon contains walnuts (no nuts restriction)

[PLANNER] Refining based on critic feedback...
[PLANNER] Plan refined (est. $44.50)

[ITERATION 2/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓   ← walnut meal replaced with seed-crusted alternative

[MEMORY] Plan preferences saved for alice.
```

**What this teaches:** Memory flows into the validation layer — the critic enforces personal restrictions, not just diet rules. A learner can see that personalisation is not just about pre-filling prompts; it changes what the quality gate accepts.

---

### Scenario 4 — Returning User, Diet Change (Alice switches to Mediterranean)

**What to observe:** Stored preferences pre-fill Keto, but Alice overrides to Mediterranean. The new diet overwrites `PreferredDietType`. Food restrictions (`no nuts`) and dislikes (`avoid avocado`) carry over — they are personal constraints, not diet-specific.

```
Enter your name (new or returning user): Alice

[MEMORY] Searching user memory for: "alice"
  → Retrieved: alice (score: 0.923)

Welcome back, Alice!
Remembered from your last session (2026-03-28, 3 sessions total):
  Diet        : Keto
  ...

Describe your diet preference (press Enter to keep 'Keto'): Mediterranean
How many days to plan? (press Enter to keep 3): 5
Daily calorie target? (press Enter to keep 2000): [Enter]
Total budget in USD? (press Enter to keep $50): 70

==============================
User      : alice
Diet      : Mediterranean
Days      : 5
Calories  : 2000 kcal/day
Budget    : $70 USD total
Restrict  : no nuts        ← carried over from memory, not re-entered
Dislikes  : avoid avocado  ← carried over
==============================

[... plan generation for Mediterranean ...]

[MEMORY] Plan preferences saved for alice.
  ← PreferredDietType now "Mediterranean", DefaultPlanDays now 5, DefaultBudget now 70
  ← FoodRestrictions still "no nuts" (append-only, not overwritten by diet change)
```

**Memory state after Session 4:**
```
PreferredDietType  : Mediterranean   ← updated
DefaultBudget      : 70              ← updated
DefaultPlanDays    : 5               ← updated
FoodRestrictions   : no nuts         ← unchanged (personal, not diet-specific)
PlanHistorySummary : [Mediterranean 5d, Keto 3d, Keto 3d, Keto 3d]
TotalSessionsCount : 4
```

---

### Scenario 5 — Second User (Bob, First Time)

**What to observe:** Bob enters a different name. His lookup returns no match (score near 0). He gets a fresh session with no pre-fills. Alice's memory is completely unaffected — user records are isolated by username.

```
Enter your name (new or returning user): Bob

[MEMORY] Searching user memory for: "bob"
  → No match above threshold (score: 0.183) — new user

Welcome! This is your first session, Bob.
Let's set up your meal preferences.

Describe your diet preference (e.g. 'Keto', 'plant-based vegan', 'Mediterranean'): Vegan
How many days to plan? (1–7): 7
Daily calorie target? (press Enter for 2000): 1800
Total budget in USD? (press Enter for $50): 60
Any food restrictions or allergies? (press Enter to skip): soy-free

[... full new-user flow, no Alice preferences visible ...]

[MEMORY] Plan preferences saved for bob.
```

**What this teaches:** The `meal_user_memory` collection holds one record per user. Each username lookup retrieves only that user's record. There is no session bleed between users.

---

## Out of Scope

- Explicit restriction removal (users cannot delete `FoodRestrictions` entries in this version)
- Multi-user simultaneous sessions
- Authentication or user isolation beyond username lookup
- Cross-diet history (history tracks all diets, but planner uses the combined list as-is)
- Editing stored preferences mid-session
