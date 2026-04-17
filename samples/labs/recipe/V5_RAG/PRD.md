# PRD: Meal Planner — V5: RAG-Powered Planning

## Learning Goal

Replace hardcoded diet knowledge and full-plan context injection with **Retrieval-Augmented Generation (RAG)**.
Two vector store collections are used — one to ground the planner, one to ground the advisor.

**New AF concept:** RAG — domain knowledge and plan content stored in a SQLite vector store, retrieved semantically at runtime via tool calls, so neither agent has facts baked into its system prompt.

---

## What Changes vs V4

| Aspect | V4 (Dynamic Instructions) | V5 (RAG) |
|---|---|---|
| Diet knowledge source | Hardcoded `DietProfile` array in code | `meal_diet_profiles` vector store collection |
| Profile selection | User picks from numbered menu | User types free-text; planner retrieves best match |
| Planner instructions | String-interpolated from profile fields | General — planner calls `search_diet_knowledge` tool |
| Planner tools | None | `search_diet_knowledge` (vector search) |
| Advisor context | Full `planJson` injected into session (expensive on long plans) | Meals indexed in `meal_plan_meals`; advisor calls `search_meal_plan` tool |
| Advisor tools | None | `search_meal_plan` (vector search) |
| Extensibility | Add diet = code change | Add diet = one `UpsertAsync` call |

---

## Two RAG Uses

### 1. Diet Knowledge Retrieval (planner)

The `MealPlannerAgent` calls `search_diet_knowledge` with a query derived from the user's diet description. The vector store returns the best-matching `DietKnowledgeRecord` (rules, macros, ingredients, foods to avoid, budget tips). The planner uses this to generate a compliant plan — no diet rules are hardcoded.

### 2. Meal Plan Retrieval (advisor)

After the refinement loop, every approved meal is indexed as a `MealRecord` in `meal_plan_meals`. The `MealAdvisorAgent` calls `search_meal_plan` per question to retrieve only the 2–3 most relevant meals — rather than receiving the full plan JSON. This avoids expensive context on multi-day plans (7 days × 3 meals with ingredients + macros ≈ 3–5KB per turn).

---

## Agents

| Agent | Role | Tools | Output |
|---|---|---|---|
| `NarrativeAgent` | Streams prose preview (V3 concept) | None | Streaming tokens |
| `MealPlannerAgent` | Generates and refines structured plans | `search_diet_knowledge` | `RunAsync<MealPlan>` |
| `NutritionCriticAgent` | Validates compliance and macros | None | `RunAsync<NutritionCritique>` |
| `MealAdvisorAgent` | Answers follow-up questions | `search_meal_plan` | `RunAsync` with `AgentSession` |

---

## Vector Store Schema

### `meal_diet_profiles` (ingested at startup, optional refresh)

```csharp
class DietKnowledgeRecord
{
    [VectorStoreKey]   Guid Id
    [VectorStoreData]  string DietName          // "Keto"
    [VectorStoreData]  string Rules             // inclusion/exclusion rules
    [VectorStoreData]  string MacroGuidelines   // fat/carb/protein ratios
    [VectorStoreData]  string TypicalIngredients
    [VectorStoreData]  string FoodsToAvoid
    [VectorStoreData]  string BudgetTips
    [VectorStoreVector(1536)]
    string Vector => $"{DietName}: {Rules}. Macros: {MacroGuidelines}. Ingredients: {TypicalIngredients}"
}
```

### `meal_plan_meals` (written after each refinement loop)

```csharp
class MealRecord
{
    [VectorStoreKey]   Guid Id
    [VectorStoreData]  int DayNumber
    [VectorStoreData]  string MealType          // Breakfast / Lunch / Dinner
    [VectorStoreData]  string Name
    [VectorStoreData]  int Calories
    [VectorStoreData]  string Macros            // "P:32g C:10g F:45g"
    [VectorStoreData]  string EstimatedCost     // "$6.50"
    [VectorStoreData]  string Ingredients       // comma-separated
    [VectorStoreVector(1536)]
    string Vector => $"Day {DayNumber} {MealType}: {Name}. Ingredients: {Ingredients}. Calories: {Calories}..."
}
```

---

## Flow

```
[Ingestion check]
  "Refresh diet knowledge base? (Y/N)"
  If Y → delete + recreate meal_diet_profiles → UpsertAsync 3 DietKnowledgeRecords
      │
      ▼
User enters: diet description (free text), days, calorie target, budget
      │
      ▼
NarrativeAgent.RunStreamingAsync(previewPrompt)   [V3 concept]
      │
      ▼
Host-orchestrated refinement loop:   [V1 concept]
  plannerAgent.RunAsync<MealPlan>(prompt)
    └── LLM calls: search_diet_knowledge("keto high fat low carb")
           → returns DietKnowledgeRecord fields
    └── LLM generates plan grounded in retrieved knowledge
  criticAgent.RunAsync<NutritionCritique>(planJson)
  if not approved → plannerAgent refines with feedback
      │
CheckBudget / PrintMealPlan
      │
      ▼
[Post-plan indexing]
  delete + recreate meal_plan_meals
  For each Meal in approved MealPlan → UpsertAsync(MealRecord)
  Output: "[RAG] N meals indexed for advisor queries"
      │
      ▼
MealAdvisorAgent session created (NO planJson pre-seeding)
  → advisor has search_meal_plan tool
Loop:
  User types question → advisorAgent.RunAsync(question, advisorSession)
    └── LLM calls: search_meal_plan("Day 1 breakfast swap")
           → returns 2-3 relevant MealRecords
    └── LLM answers using retrieved meals only
  User types "exit" → End
```

---

## Key Code Patterns to Learn

**Ingestion:**
```csharp
// KEY CONCEPT: embed domain knowledge at startup — not in code
foreach (DietKnowledgeRecord record in dietRecords)
    await dietCollection.UpsertAsync(record);
```

**Planner agent — grounded by retrieval:**
```csharp
// KEY CONCEPT: no diet rules hardcoded — agent retrieves them via tool
ChatClientAgent plannerAgent = client.GetChatClient("gpt-4.1-nano")
    .AsAIAgent(
        instructions: "Use search_diet_knowledge to retrieve dietary rules, then generate the plan.",
        tools: [AIFunctionFactory.Create(dietSearchTool.Search, "search_diet_knowledge", "...")]);
```

**Post-plan indexing:**
```csharp
// KEY CONCEPT: index the plan so advisor retrieves by similarity — not by full context injection
foreach (DayPlan day in plan.Days)
    foreach (Meal meal in day.Meals)
        await mealCollection.UpsertAsync(new MealRecord { DayNumber = day.DayNumber, ... });
```

**Advisor agent — no planJson pre-seeding:**
```csharp
// KEY CONCEPT: advisor fetches relevant meals per question — never sees full plan JSON
ChatClientAgent advisorAgent = client.GetChatClient("gpt-4.1-nano")
    .AsAIAgent(
        instructions: "Use search_meal_plan to look up meals before answering.",
        tools: [AIFunctionFactory.Create(mealSearchTool.Search, "search_meal_plan", "...")]);

AgentSession advisorSession = await advisorAgent.CreateSessionAsync();
// No planJson pre-seeding here — compare with V2/V3/V4
```

---

## Console Output Shape

```
Import/refresh diet knowledge base? (Y/N): Y
Embedding diet knowledge 1/3: Vegan...
Embedding diet knowledge 2/3: Keto...
Embedding diet knowledge 3/3: Mediterranean...
Diet knowledge embedded successfully.

Describe your diet preference: Keto
How many days to plan? (1–7): 3
Daily calorie target? (press Enter for 2000):
Total budget in USD? (press Enter for $50):

Diet      : Keto
Days      : 3
Calories  : 2000 kcal/day
Budget    : $50 USD total

Step 1: Streaming plan preview...
For a 3-day Keto meal plan targeting 2000 kcal per day...
[tokens stream]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
[RAG] Searching diet knowledge for: "Keto high fat low carbohydrate"
  → Retrieved: Keto (score: 0.921)
[PLANNER] Initial plan generated (3 days, est. $42.80)

[ITERATION 1/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓

[BUDGET CHECK] Estimated total: $42.80 — Within limit of $50.00 ✓

Step 4: Final Meal Plan
[plan table printed...]

[RAG] Indexing approved meal plan for advisor queries...
[RAG] 9 meals indexed for advisor queries.

Step 5: Meal Plan Advisor (type 'exit' to quit)
Meal plan indexed. Ask your questions (type 'exit' to finish):

> Can you swap the Day 1 breakfast?
[RAG] Searching meal plan for: "Day 1 breakfast"
  → Retrieved: Day 1 Breakfast — Scrambled Eggs with Avocado (score: 0.944)
The Day 1 breakfast is Scrambled Eggs with Avocado (420 kcal)...

> exit
Session ended. Meal planning complete.
```

---

## Sample Inputs for Testing

| Input | What to observe |
|---|---|
| "Keto", 3 days | `[RAG] Searching diet knowledge...` → Keto record retrieved (score 0.9+) |
| "plant based vegan", 3 days | Vegan record retrieved — not Keto; plan has zero animal products |
| "Mediterranean", 5 days | Mediterranean record retrieved; red meat not repeated |
| Ask "swap Day 2 lunch" | Only Day 2 lunch meals retrieved — not all 15 meals |
| Ask follow-up "what about the cost?" | Advisor references prior answer via session history |

**Key test:** Ask advisor a question — confirm `[RAG] Searching meal plan...` appears (not silent context). This proves the advisor retrieves rather than reads from a pre-loaded context.

---

## Out of Scope

- Updating diet knowledge records at runtime
- User-defined custom diets or meal substitution rules
- Persisting the meal plan across runs (collection cleared each session)
- Chunking meals differently (per-ingredient indexing)
- Authentication or multi-user stores
