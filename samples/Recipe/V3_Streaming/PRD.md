# PRD: Meal Planner — V3: Streaming Output

## Learning Goal

Before the structured refinement loop runs, stream a **narrative planning preview**
token-by-token so the user sees the model "thinking" in real time.

**New AF concept:** `RunStreamingAsync` + `AgentResponseUpdate` — consuming incremental LLM output
instead of waiting for a complete response.

---

## What Changes vs V2

| Aspect | V2 (Chat Loop) | V3 (Streaming) |
|---|---|---|
| Plan generation start | Silent until first `RunAsync` returns | Narrative preview streams token-by-token first |
| Agents | 3 (planner + critic + advisor) | 4 (narrativeAgent + planner + critic + advisor) |
| Structured output | Unchanged | Unchanged — streaming and structured are separate calls |

> There is no `RunStreamingAsync<T>`. Streaming a narrative and extracting structured output
> are always two separate agent calls. The narrative agent streams prose; the planner agent
> returns `MealPlan` via `RunAsync<MealPlan>`.

---

## Agents

| Agent | Role | Output mode |
|---|---|---|
| `NarrativeAgent` | Streams a prose preview of the plan approach | `RunStreamingAsync` |
| `MealPlannerAgent` | Generates and refines structured meal plans | `RunAsync<MealPlan>` |
| `NutritionCriticAgent` | Validates compliance and macros | `RunAsync<NutritionCritique>` |
| `MealAdvisorAgent` | Answers follow-up questions (V2 concept) | `RunAsync` with `AgentSession` |

---

## Flow

```
User picks diet + days
      │
      ▼
NarrativeAgent.RunStreamingAsync(previewPrompt)
  → each AgentResponseUpdate printed immediately (typing effect)
      │
      ▼
[V2 host-orchestrated refinement loop unchanged]
      │
      ▼
[V2 multi-turn advisor Q&A unchanged]
```

---

## Key Code Pattern to Learn

```csharp
// KEY CONCEPT: RunStreamingAsync — each update is a chunk of tokens
await foreach (AgentResponseUpdate update in narrativeAgent.RunStreamingAsync(prompt))
{
    Console.Write(update);   // tokens printed as they arrive
}
Console.WriteLine();

// Structured extraction is a SEPARATE call — no RunStreamingAsync<T> exists
AgentResponse<MealPlan> structured = await plannerAgent.RunAsync<MealPlan>(prompt, plannerSession);
MealPlan plan = structured.Result;
```

---

## Console Output Shape

```
Step 1: Streaming plan preview...

For a 3-day Mediterranean meal plan targeting 2000 kcal per day within a $50
budget, I will focus on seafood-forward meals emphasising olive oil, legumes,
and seasonal vegetables...
[tokens appear progressively]

Step 2: Generating structured plan with refinement loop...
[PLANNER] Calling MealPlannerAgent...
[PLANNER] Initial plan generated (3 days, est. $44.80)

[ITERATION 1/3] Calling NutritionCriticAgent...
[CRITIC] Approved ✓

[same output as V2 from here...]
```

---

## Sample Inputs for Testing

Use the same diet + day combinations as V1/V2.

**What to observe:**
- The narrative stream appears **before** any `[PLANNER]` lines — confirms the streaming call runs first
- Tokens appear one at a time (no blank wait), then the refinement loop begins
- The final advisor Q&A still works after streaming completes

---

## Out of Scope

- Streaming the refinement loop responses (only the narrative preview streams)
- Streaming the advisor Q&A answers
- RAG for ingredient data
- Dynamic profiles / personas
