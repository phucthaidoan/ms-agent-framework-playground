# PRD: Meal Planner — V1: Host-Orchestrated Refinement Loop

## Learning Goal

Refactor the base Meal Planner sample to make the iterative refinement loop **visible in host code**.
Instead of `NutritionCriticAgent.AsAIFunction()` (where the loop is hidden inside the LLM),
the host calls each agent explicitly — generate, critique, refine — printing progress at every step.

**New AF concept:** Sequential `RunAsync<T>` calls in host code as an alternative to Agent-as-Tool.
The loop is yours to control: you decide when to stop, what to log, and how to pass feedback.

---

## What Changes vs Base Sample

| Aspect | Base (Agent-as-Tool) | V1 (Host-Orchestrated) |
|---|---|---|
| Critic invocation | `criticAgent.AsAIFunction()` passed as `tools:` to planner | `criticAgent.RunAsync<NutritionCritique>()` called directly by host |
| Refinement loop | Hidden inside LLM reasoning — one `RunAsync` call returns final plan | Explicit `for` loop in host code — each iteration prints to console |
| `plannerAgent` tools | `[criticTool]` | *(none)* — planner is a pure generator |
| Visibility | Opaque — you only see the approved result | Transparent — see each critique and each fix |

---

## Agents

| Agent | Role | How called |
|---|---|---|
| `MealPlannerAgent` | Generates and refines meal plans from plain text prompts | `RunAsync<MealPlan>()` — twice per failed iteration (generate + refine) |
| `NutritionCriticAgent` | Validates diet compliance and macro balance | `RunAsync<NutritionCritique>()` — once per iteration |

Neither agent holds a reference to the other. The host is the orchestrator.

---

## Refinement Loop (host code)

```
[PLANNER] plannerAgent.RunAsync<MealPlan>(initialPrompt)
        │
        ▼
for iteration = 1..MaxIterations:
        │
        ├─ [ITERATION N] criticAgent.RunAsync<NutritionCritique>(planJson)
        │
        ├─ critique.Approved == true?
        │     Yes ──► [CRITIC] Approved ✓ — break
        │     No  ──► print DietViolations, MacroIssues, Suggestions
        │
        ├─ iteration == MaxIterations?
        │     Yes ──► [PLANNER] Max iterations reached — break
        │
        └─ [PLANNER] plannerAgent.RunAsync<MealPlan>(feedback + planJson)
```

---

## Key Code Pattern to Learn

```csharp
// Two independent agents — no tools, no AsAIFunction
ChatClientAgent plannerAgent = client.GetChatClient("gpt-4.1-nano").AsAIAgent(instructions: "...");
ChatClientAgent criticAgent  = client.GetChatClient("gpt-4.1-nano").AsAIAgent(instructions: "...");

// Host drives the loop
AgentResponse<MealPlan> initial = await plannerAgent.RunAsync<MealPlan>(prompt);
string planJson = initial.Text;

for (int i = 1; i <= MaxIterations; i++)
{
    AgentResponse<NutritionCritique> critique = await criticAgent.RunAsync<NutritionCritique>(planJson);
    if (critique.Result.Approved) break;

    // Build feedback string from critique, pass back to planner
    AgentResponse<MealPlan> refined = await plannerAgent.RunAsync<MealPlan>(feedback);
    planJson = refined.Text;
}
```

---

## Console Output Shape

```
[PLANNER] Calling MealPlannerAgent...
[PLANNER] Initial plan generated (3 days, est. $42.50)

[ITERATION 1/3] Calling NutritionCriticAgent...
[CRITIC] Not approved. Issues found:
  • Day 2 dinner contains cheese — invalid for Vegan
[CRITIC] Suggestions:
  → Replace cheese with nutritional yeast or cashew cream

[PLANNER] Refining based on critic feedback...
[PLANNER] Plan refined (est. $40.20)

[ITERATION 2/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓

[BUDGET CHECK] Estimated total: $40.20 — Within limit of $50.00 ✓

Day 1
  [Breakfast   ] Smoothie Bowl                    420 kcal  |  P:12g C:68g F:10g  |  $3.50
  [Lunch       ] Lentil Soup                      510 kcal  |  P:28g C:72g F:8g   |  $3.20
  [Dinner      ] Tofu Stir-Fry                    590 kcal  |  P:32g C:55g F:18g  |  $5.10
...
```

---

## Structured Output Shapes

Same as base sample — no changes to `MealPlan`, `DayPlan`, `Meal`, or `NutritionCritique`.

---

## Sample Inputs for Testing

Use the same inputs as the base PRD: Vegan / Keto / Mediterranean, 1–3 days.

**What to observe vs base:**
- Keto: watch the critic catch high-carb meals and the planner fix them across iterations
- Vegan: watch the critic flag any dairy/meat that slipped in

---

## Out of Scope

- Streaming (future: surface each token as the planner refines)
- RAG for ingredient costs
- Multi-turn user conversation
- Database or UI
