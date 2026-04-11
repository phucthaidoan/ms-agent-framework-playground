# PRD: Meal Planner — V2: Multi-Turn Chat Loop

## Learning Goal

Extend V1 with a **multi-turn conversation** after the approved plan is printed.
The user can ask follow-up questions about the plan (e.g., "Can you swap Day 2 lunch?",
"How can I reduce calories on Day 1?") and the advisor remembers prior answers.

**New AF concept:** `AgentSession` — maintaining conversation history across multiple `RunAsync` calls.

---

## What Changes vs V1

| Aspect | V1 (Host Loop) | V2 (Chat Loop) |
|---|---|---|
| After plan prints | Sample ends | Multi-turn Q&A session opens |
| Session usage | `plannerSession` only (for refinement) | `advisorSession` passed on every advisor turn |
| Agents | 2 (planner + critic) | 3 (planner + critic + advisor) |
| Termination | Automatic | User types `exit` |

---

## Agents

| Agent | Role | Session |
|---|---|---|
| `MealPlannerAgent` | Generates and refines meal plans | `plannerSession` (for multi-turn refinement — same as V1) |
| `NutritionCriticAgent` | Validates diet compliance and macros | No session (one-shot per iteration) |
| `MealAdvisorAgent` | Answers follow-up questions about the final plan | `advisorSession` — maintained across all Q&A turns |

---

## Flow

```
User picks diet + days
      │
      ▼
[same as V1: host-orchestrated refinement loop]
      │
      ▼
Print final approved plan
      │
      ▼
MealAdvisorAgent session created
Pre-seed: inject approved planJson as context
      │
      ▼
Loop:
  User types question  ──► advisorAgent.RunAsync(question, advisorSession)
                       ──► Print answer (history preserved)
  User types "exit"    ──► End
```

---

## Pre-seeding the Session

After the refinement loop, inject the approved plan JSON as context before the first user question:

```csharp
AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();

string planContext =
    $"Here is the approved {dietType} meal plan for {numberOfDays} days " +
    $"(target: {TargetCaloriesPerDay} kcal/day, budget: ${BudgetLimit} USD): " +
    $"{planJson}. Use this as the basis for answering follow-up questions.";

await mealAdvisorAgent.RunAsync(planContext, advisorSession);
```

The `planJson` string is already available from the refinement loop (preserved as `string planJson`).

---

## Key Code Pattern to Learn

```csharp
// KEY CONCEPT: one session created once — passed to every RunAsync call
AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();

// Pre-seed: load plan as context
await mealAdvisorAgent.RunAsync(planContext, advisorSession);

while (true)
{
    Console.Write("> ");
    string input = Console.ReadLine() ?? string.Empty;
    if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    // Same session on every turn — history preserved automatically
    AgentResponse response = await mealAdvisorAgent.RunAsync(input, advisorSession);
    Console.WriteLine(response.Text);
}
```

---

## Console Output Shape

```
[same V1 refinement loop output...]

Step 4: Meal Plan Advisor (type 'exit' to quit)
Plan loaded. Ask your questions (type 'exit' to finish):

> Can you swap the Day 1 lunch for something lower in carbs?
The lentil soup on Day 1 lunch can be replaced with a grilled ...

> What calories would that swap save?
The original lunch was approximately 510 kcal. The replacement ...

> exit

Session ended. Meal planning complete.
```

---

## Sample Questions to Try

- "Can you swap the Day 2 breakfast for something quicker to prepare?"
- "How can I reduce the total cost while keeping the same diet?"
- "What would change if I added an extra 200 kcal to Day 3 dinner?"
- "Write a shopping list for Day 1."

**What to observe:** Ask a follow-up ("What about the cost of that swap?") — the advisor references
the prior answer without needing the plan restated. This confirms session history is working.

---

## Out of Scope

- Streaming (future: stream advisor answers token-by-token)
- RAG for ingredient substitutions
- Dynamic profiles / personas
- Database or UI
