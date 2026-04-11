# PRD: Meal Planner — V4: Dynamic Instructions

## Learning Goal

Replace hardcoded diet constants with a **`DietProfile` data record** selected by the user.
All four agent instruction strings are built at runtime from the selected profile —
demonstrating that agent behaviour is data-driven, not code-driven.

**New AF concept:** Dynamic `instructions` — composing system prompts at runtime from data,
rather than from hardcoded constants or strings.

---

## What Changes vs V3

| Aspect | V3 (Streaming) | V4 (Dynamic Instructions) |
|---|---|---|
| User input | Pick diet type + enter days separately | Pick a complete `DietProfile` (diet, days, calories, budget, persona) |
| Agent instructions | Hardcoded constants (`TargetCaloriesPerDay`, `BudgetLimit`) | Built at runtime from `selectedProfile.*` fields |
| `CheckBudget` | Uses `BudgetLimit` constant | Accepts `budgetLimit` parameter from profile |
| Calorie / budget targets | Fixed (2000 kcal, $50) | Per-profile (varies by selection) |

---

## Diet Profiles (hardcoded menu)

| # | Name | Diet | Days | Calories | Budget | Persona |
|---|---|---|---|---|---|---|
| 1 | Office Worker | Mediterranean | 5 | 1800 kcal | $60 | Sedentary adult with a mild weight-loss goal |
| 2 | Athlete | Keto | 7 | 2800 kcal | $80 | Endurance athlete with high protein priority |
| 3 | Plant-Based Beginner | Vegan | 3 | 2000 kcal | $40 | Transitioning from omnivore, prefers familiar ingredients |

---

## Agents

| Agent | Role | Instructions |
|---|---|---|
| `NarrativeAgent` | Streams prose preview (V3 concept) | Built from `selectedProfile` |
| `MealPlannerAgent` | Generates and refines structured plans | Built from `selectedProfile` |
| `NutritionCriticAgent` | Validates compliance and macros | Built from `selectedProfile` |
| `MealAdvisorAgent` | Answers follow-up questions (V2 concept) | Built from `selectedProfile` |

All four agents receive instructions assembled from the same `selectedProfile` object.

---

## Flow

```
Print profile menu (3 profiles)
User selects profile
      │
      ▼
Build all agent instructions from selectedProfile.*
      │
      ▼
[V3 streaming narrative preview — uses profile fields in prompt]
      │
      ▼
[V3 host-orchestrated refinement loop — uses profile fields in prompts]
      │
      ▼
CheckBudget(plan.EstimatedTotalCost, selectedProfile.BudgetLimit)
      │
      ▼
[V2 multi-turn advisor Q&A — advisor instructions include persona note]
```

---

## Key Code Pattern to Learn

```csharp
// KEY CONCEPT: behaviour driven by a data record, not constants
private record DietProfile(
    string Name, string DietType, int DaysToplan,
    int TargetCaloriesPerDay, decimal BudgetLimit, string PersonaNote);

DietProfile[] profiles = [
    new("Office Worker",        "Mediterranean", 5, 1800, 60m, "Sedentary adult, weight-loss goal"),
    new("Athlete",              "Keto",          7, 2800, 80m, "Endurance athlete, high protein"),
    new("Plant-Based Beginner", "Vegan",         3, 2000, 40m, "Transitioning omnivore")
];

// User selects profile — all instructions built from selectedProfile
string plannerInstructions =
    $"You are a meal planner for the '{selectedProfile.DietType}' diet. " +
    $"This plan is for: {selectedProfile.PersonaNote}. " +
    $"Target {selectedProfile.TargetCaloriesPerDay} kcal/day, budget ${selectedProfile.BudgetLimit}...";

ChatClientAgent plannerAgent = client
    .GetChatClient("gpt-4.1-nano")
    .AsAIAgent(instructions: plannerInstructions);  // ← built from data
```

---

## Console Output Shape

```
Select a dietary profile:
  1. Office Worker — Mediterranean, 5 days, 1800 kcal/day, $60 budget
       Sedentary adult with a mild weight-loss goal
  2. Athlete — Keto, 7 days, 2800 kcal/day, $80 budget
       Endurance athlete with high protein priority
  3. Plant-Based Beginner — Vegan, 3 days, 2000 kcal/day, $40 budget
       Transitioning from omnivore, prefers familiar ingredients

Enter profile number: 2

Profile  : Athlete
Diet     : Keto
Days     : 7
Calories : 2800 kcal/day
Budget   : $80 USD total
Persona  : Endurance athlete with high protein priority

Step 1: Streaming plan preview...
For a 7-day Keto meal plan designed for an endurance athlete targeting 2800 kcal...
[tokens stream]

[same refinement loop and advisor output as V3...]
```

---

## Sample Inputs for Testing

| Profile | What to observe |
|---|---|
| Office Worker (Mediterranean, 5d, $60) | Plan spans 5 days; calorie target is 1800, not the V3 constant of 2000 |
| Athlete (Keto, 7d, $80) | Full 7-day Keto plan; critic checks carbs <50g/day with 2800 kcal target |
| Plant-Based Beginner (Vegan, 3d, $40) | Tight $40 budget; persona note referenced in plan |

**Key test:** Run with "Athlete", then run again with "Plant-Based Beginner" — entirely different plan
generated from the same code. Only the profile selection changed.

---

## Out of Scope

- RAG for loading profiles from a database or file
- More than 3 profiles
- User-defined custom profiles
- Streaming the advisor Q&A
