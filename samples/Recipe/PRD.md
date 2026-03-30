# PRD: AI-Powered Meal Plan Generator

## Overview

A multi-agent console application demonstrating Microsoft Agent Framework by simulating a
meal planning workflow — generating a personalized meal plan, validating it for dietary compliance
and nutritional balance, and optimizing it for budget constraints.

## Goal

Learn and demonstrate these AF concepts:

- **Structured Output** — extract and produce a typed meal plan from LLM
- **Tool Calling** — simulate sending the final plan to a "meal service"
- **Sequential Agent Composition** — planner generates, critic validates, planner refines
- **Iterative Refinement Loop** — agents critique and planner improves until approved
- **Instructions** — domain-specific, constraint-driven prompting per agent
- **Agent-as-Tool** — `NutritionCriticAgent` called as a tool by the `PlannerAgent`

> **Future learning (not in scope now):** Add streaming to surface each refinement step in real-time,
> and add RAG to pull ingredient costs from a vector store instead of hardcoded estimates.

---

## Scenario

User selects a diet type and number of days from the console. The system:

1. **Generates an initial meal plan** using `MealPlannerAgent` based on diet type and calorie target
2. **Validates the plan** using `NutritionCriticAgent` for diet compliance and macro balance
3. **Refines the plan** if validation fails — planner regenerates with critic feedback (max 3 loops)
4. **Checks the budget** via a tool call to `CheckBudget` against a hardcoded limit
5. **Delivers the final plan** via a simulated tool call to `PrintMealPlan`

---

## Agents

| Agent                   | Role                                                                          | Key AF Concept                    |
| ----------------------- | ----------------------------------------------------------------------------- | --------------------------------- |
| `MealPlannerAgent`      | Generates and refines meal plans using critic feedback                        | Structured Output, Instructions   |
| `NutritionCriticAgent`  | Validates diet compliance and macro balance, returns structured critique      | Agent-as-Tool, Structured Output  |

---

## Structured Output Shapes

### `MealPlan` (produced by `MealPlannerAgent`)

```csharp
class MealPlan
{
    string DietType { get; set; }                  // e.g., "Vegan", "Keto"
    int NumberOfDays { get; set; }
    List<DayPlan> Days { get; set; }
    decimal EstimatedTotalCost { get; set; }       // USD
}

class DayPlan
{
    int DayNumber { get; set; }
    List<Meal> Meals { get; set; }                 // Breakfast, Lunch, Dinner
}

class Meal
{
    string MealType { get; set; }                  // Breakfast / Lunch / Dinner
    string Name { get; set; }                      // e.g., "Tofu Stir-Fry"
    int Calories { get; set; }
    int ProteinGrams { get; set; }
    int CarbsGrams { get; set; }
    int FatGrams { get; set; }
    decimal EstimatedCost { get; set; }
    List<string> Ingredients { get; set; }
}
```

### `NutritionCritique` (produced by `NutritionCriticAgent`)

```csharp
class NutritionCritique
{
    bool Approved { get; set; }
    List<string> DietViolations { get; set; }      // e.g., ["Meal 2 contains dairy — invalid for Vegan"]
    List<string> MacroIssues { get; set; }         // e.g., ["Day 1 carbs exceed Keto limit"]
    List<string> Suggestions { get; set; }         // Actionable fixes for the planner
}
```

---

## Hardcoded Inputs

### Diet Types

```
["Vegan", "Keto", "Mediterranean"]
```

### Constraints

```
TargetCalories:  2000 kcal/day
BudgetLimit:     $50 USD total
NumberOfDays:    3
MaxIterations:   3
```

### Diet Rules (embedded in `NutritionCriticAgent` instructions)

| Diet          | Forbidden                                  | Required / Preferred                        |
| ------------- | ------------------------------------------ | ------------------------------------------- |
| Vegan         | Meat, fish, dairy, eggs, honey             | Plant proteins, legumes, whole grains       |
| Keto          | Grains, sugar, high-carb fruits/vegetables | High fat (≥65%), low carbs (<10%)           |
| Mediterranean | Processed food, red meat (limit)           | Olive oil, fish, vegetables, whole grains   |

---

## Tools

### `CheckBudget`

Simulated — validates total estimated cost against the hardcoded limit:

```
[BUDGET CHECK] Total estimated cost: $43.50 — Within limit of $50.00 ✓
```

### `PrintMealPlan`

Simulated — prints the final approved plan to the console in a readable format:

```
[MEAL PLAN] Day 1 — Breakfast: Avocado Toast | 450 kcal | $4.50
[MEAL PLAN] Day 1 — Lunch: Lentil Soup | 520 kcal | $3.80
[MEAL PLAN] Day 1 — Dinner: Chickpea Curry | 610 kcal | $5.20
...
```

---

## Flow

```
User selects: DietType, NumberOfDays
        │
        ▼
MealPlannerAgent ──► MealPlan (structured output)
        │
        ▼
 Refinement Loop (max 3 iterations)
        │
        ├──► NutritionCriticAgent (called as tool)
        │         └──► NutritionCritique (structured output)
        │
        ├──► Approved?
        │     No  ──► MealPlannerAgent.Refine(plan + critique.Suggestions)
        │     Yes ──► EXIT loop
        │
        ▼
CheckBudget tool ──► within limit?
   No  ──► Print warning, continue with plan
   Yes ──► Continue
        │
        ▼
PrintMealPlan tool ──► Display final plan to console
```

---

## AF Concepts Mapping

| Concept                       | Where It Appears                                                              |
| ----------------------------- | ----------------------------------------------------------------------------- |
| **Structured Output**         | `MealPlannerAgent.RunAsync<MealPlan>()` and `NutritionCriticAgent.RunAsync<NutritionCritique>()` |
| **Tool Calling**              | `CheckBudget` and `PrintMealPlan` called by the orchestrator                  |
| **Agent-as-Tool**             | `NutritionCriticAgent.AsAIFunction()` passed as a tool to `MealPlannerAgent`  |
| **Instructions**              | Each agent has domain-specific instructions (diet rules, macro targets)       |
| **Sequential Composition**    | Planner → Critic → Planner (loop) → Budget Check → Print                     |
| **Iterative Refinement**      | Loop exits when `NutritionCritique.Approved == true` or max iterations hit    |

---

## Agent-as-Tool vs Host-Orchestrated Loop

### How Each Works

- **Agent-as-Tool** (base sample): `criticAgent.AsAIFunction()` is passed as `tools:` to `plannerAgent`. The planner LLM calls the critic whenever it decides to validate. A single `plannerAgent.RunAsync<MealPlan>()` call returns the final approved plan — the refinement loop is hidden inside the model's reasoning.
- **Host-Orchestrated Loop** (V1): two independent agents with no knowledge of each other. The host runs an explicit `for` loop — call planner → call critic → if rejected, build a feedback string and call planner again. Every iteration is visible in host code.

### Trade-offs

| | Agent-as-Tool | Host-Orchestrated Loop |
|---|---|---|
| **Visibility** | Opaque — you only see the final result | Transparent — every critique and refinement is logged |
| **Control** | LLM decides when and how many times to call critic | Host decides loop count, stop condition, feedback format |
| **Debuggability** | Hard — can't see why a plan was rejected or how it was fixed | Easy — each iteration prints violations and suggestions |
| **Code complexity** | Low — one `RunAsync` call | Higher — explicit loop and feedback string assembly |
| **Risk** | LLM may skip validation or loop without bound | Host enforces `MaxIterations` hard cap |
| **When to use** | Quick prototyping; trust the model to self-correct | Production, teaching, debugging, or auditable workflows |

### Key Insight

Agent-as-Tool hides complexity inside the model — convenient but opaque. A host-orchestrated loop exposes every step — more code but full control and observability. Neither pattern is universally better; the choice depends on whether transparency and auditability matter more than conciseness.

---

## Sample Inputs for Testing

### Input 1: Vegan, 3 days

```
Diet: Vegan
Days: 3
```

Expected: Plan uses only plant-based ingredients. Critic approves without violations.

### Input 2: Keto, 3 days

```
Diet: Keto
Days: 3
```

Expected: High fat meals. Critic may trigger 1-2 refinement rounds if carbs are initially too high.

### Input 3: Mediterranean, 3 days

```
Diet: Mediterranean
Days: 3
```

Expected: Fish, olive oil, vegetables dominate. Budget check likely passes under $50.

---

## Console Output Shape

```
> Generating 3-day Vegan meal plan...

[ITERATION 1] MealPlannerAgent: Plan generated.
[ITERATION 1] NutritionCriticAgent: Violations found — ["Day 2 lunch contains cheese"].
[ITERATION 2] MealPlannerAgent: Plan refined based on feedback.
[ITERATION 2] NutritionCriticAgent: Approved ✓

[BUDGET CHECK] Total estimated cost: $38.20 — Within limit of $50.00 ✓

[MEAL PLAN] Day 1 — Breakfast: Smoothie Bowl | 420 kcal | $3.50
[MEAL PLAN] Day 1 — Lunch: Lentil Soup | 510 kcal | $3.20
[MEAL PLAN] Day 1 — Dinner: Tofu Stir-Fry | 590 kcal | $5.10
...
```

---

## Out of Scope

- Real database or file persistence
- UI or web frontend
- Streaming (future: show token-by-token refinement)
- RAG for ingredient cost lookup (future: vector store with real prices)
- Multi-turn user conversation / interactive editing
- Real nutrition API integration
