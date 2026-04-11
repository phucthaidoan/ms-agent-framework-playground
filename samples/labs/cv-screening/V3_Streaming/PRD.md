# PRD: CV Screening — V3: Streaming Output

## Learning Goal

Replace the one-shot `RunAsync` screening with a **streaming** response so the
screening summary appears token-by-token in the console, simulating real-time analysis.

**New AF concept:** `RunStreamingAsync` + `AgentResponseUpdate` — consuming incremental LLM output.

---

## What Changes vs Base Sample

| Aspect | Base | V3 |
|---|---|---|
| Screener response | `RunAsync` returns complete result | `RunStreamingAsync` streams tokens as they arrive |
| Structured output | `RunAsync<ScreeningResult>` | Stream plain text summary first, then parse structured result |
| UX | Instant (or delayed) full output | Text appears progressively, like ChatGPT typing effect |

---

## Agents

| Agent | Role | Output mode |
|---|---|---|
| `CvScreenerAgent` (streaming) | Streams a narrative analysis of the CV | `RunStreamingAsync` |
| `CvScreenerAgent` (structured) | Extracts structured verdict after narrative | `RunAsync<ScreeningResult>` |
| `InterviewCoordinatorAgent` | Notifies interviewers (same as base) | `RunAsync` |

> Two calls to screener: first for the streamed narrative, then for the structured verdict.
> This keeps the learning contrast clear — streaming vs structured are separate calls.

---

## Flow

```
User pastes CV
      │
      ▼
CvScreenerAgent.RunStreamingAsync(cvText)
  → Stream narrative analysis token by token to console
      │
      ▼
CvScreenerAgent.RunAsync<ScreeningResult>(cvText)
  → Get structured verdict (IsQualified, criteria lists)
      │
      ▼
Print structured verdict
      │
      ▼
IsQualified? → notify interviewers (same as base)
```

---

## Key Code Pattern to Learn

```csharp
Output.Title("Streaming CV Analysis...");

// Stream the narrative
List<AgentResponseUpdate> updates = new();
await foreach (AgentResponseUpdate update in screenerAgent.RunStreamingAsync(cvText))
{
    updates.Add(update);
    Console.Write(update);   // tokens appear as they arrive
}
Console.WriteLine();

Output.Separator();
Output.Title("Extracting structured verdict...");

// Structured extraction (separate call)
AgentResponse<ScreeningResult> structured = await screenerAgent.RunAsync<ScreeningResult>(cvText);
ScreeningResult result = structured.Result;
```

---

## Sample CVs

Use the same 4 CVs from the base PRD.

---

## Out of Scope

- Agent-as-Tool composition
- Multi-turn chat
- Multiple job roles
