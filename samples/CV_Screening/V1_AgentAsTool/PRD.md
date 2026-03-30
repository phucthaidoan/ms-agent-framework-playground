# PRD: CV Screening — V1: Agent-as-Tool

## Learning Goal

Refactor the base CV Screening sample to use **Agent-as-Tool** composition.
Instead of two sequential `RunAsync` calls in `Program`, the `InterviewCoordinatorAgent`
*calls* the `CvScreenerAgent` as a tool. There is a single top-level agent entry point.

**New AF concept:** `agent.AsAIFunction()` — wrapping an agent as an `AITool` for another agent.

---

## What Changes vs Base Sample

| Aspect | Base (sequential) | V1 (agent-as-tool) |
|---|---|---|
| Entry point | Two explicit `RunAsync` calls | One `RunAsync` on coordinator |
| Screener invocation | Called directly by host code | Called by coordinator via tool |
| Control flow | Host code gates on `IsQualified` | Coordinator decides what to do next |

---

## Agents

| Agent | Role | How exposed |
|---|---|---|
| `CvScreenerAgent` | Screens CV, returns `ScreeningResult` as structured output | Wrapped via `.AsAIFunction()` as a tool |
| `InterviewCoordinatorAgent` | Orchestrator — calls screener tool, then notifies interviewers | Top-level agent, called once by host |

---

## Tools available to `InterviewCoordinatorAgent`

1. `screen_cv` — the `CvScreenerAgent` wrapped as a tool (takes raw CV text, returns screening JSON)
2. `notify_interviewer` — same simulated notification tool as base sample

---

## Flow

```
Host calls coordinator.RunAsync(cvText)
      │
      ▼
CoordinatorAgent calls screen_cv tool (CvScreenerAgent)
      │
      ▼
CoordinatorAgent receives ScreeningResult
      │
      ▼
 IsQualified?
   No  ──► Coordinator responds "not suitable"
   Yes ──► Coordinator calls notify_interviewer × 3
```

---

## Key Code Pattern to Learn

```csharp
// Wrap screener agent as a tool
AIFunction screenerTool = screenerAgent.AsAIFunction(
    name: "screen_cv",
    description: "Screen a candidate CV against the job description. Returns qualification verdict.");

// Pass it to coordinator alongside notify tool
ChatClientAgent coordinator = client
    .GetChatClient("gpt-4.1-nano")
    .AsAIAgent(
        instructions: "...",
        tools: [ screenerTool, AIFunctionFactory.Create(NotifyInterviewer, ...) ]);

// Single entry point
await coordinator.RunAsync(cvText);
```

---

## Sample CVs

Use the same 4 CVs from the base PRD.

---

## Out of Scope

- Streaming
- Multi-turn chat
- Multiple job roles
