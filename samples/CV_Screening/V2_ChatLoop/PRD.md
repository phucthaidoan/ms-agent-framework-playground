# PRD: CV Screening — V2: Multi-Turn Chat Loop

## Learning Goal

Extend the base sample with a **multi-turn conversation** after screening.
Once the CV is screened, the user can ask follow-up questions about the result
(e.g., "Why was this candidate rejected?", "What training would close the gap?").

**New AF concept:** `AgentSession` — maintaining conversation history across multiple turns.

---

## What Changes vs Base Sample

| Aspect | Base | V2 |
|---|---|---|
| Interaction | One-shot paste + output | Paste CV → screening → open Q&A loop |
| Session | No session used | `AgentSession` passed on every turn |
| Termination | Ends automatically | User types `exit` to quit |

---

## Agents

| Agent | Role | Session |
|---|---|---|
| `CvScreenerAgent` | Screens CV, returns structured result (same as base) | No session (one-shot) |
| `CvAdvisorAgent` | Answers follow-up questions about the screening result | Yes — `AgentSession` maintained across turns |

---

## Flow

```
User pastes CV
      │
      ▼
CvScreenerAgent  ──► ScreeningResult (structured output, one-shot)
      │
      ▼
Print verdict (qualified / not qualified + criteria)
      │
      ▼
CvAdvisorAgent session created, pre-seeded with screening context
      │
      ▼
Loop:
  User types question  ──► advisor.RunAsync(question, session)
                       ──► Print answer
  User types "exit"    ──► End
```

---

## Pre-seeding the Session

After screening, inject the result as context into the advisor's session before
the first user question:

```csharp
AgentSession session = await advisorAgent.CreateSessionAsync();
// Inject screening result as first assistant message or system context
string context = $"Screening result for {result.CandidateName}: {result.Summary}. " +
                 $"Matched: {string.Join(", ", result.MatchedCriteria)}. " +
                 $"Missing: {string.Join(", ", result.MissingCriteria)}.";
await advisorAgent.RunAsync(context, session);
```

---

## Sample Questions to Try

- "Why was this candidate not qualified?"
- "What skills would make this candidate suitable?"
- "Write a rejection email for this candidate."
- "Compare this candidate to the ideal profile."

---

## Key Code Pattern to Learn

```csharp
AgentSession session = await advisorAgent.CreateSessionAsync();

while (true)
{
    Console.Write("> ");
    string input = Console.ReadLine() ?? string.Empty;
    if (input.Trim().ToLower() == "exit") break;

    AgentResponse response = await advisorAgent.RunAsync(input, session);
    Console.WriteLine(response.Text);
}
```

---

## Out of Scope

- Agent-as-Tool composition
- Streaming
- Multiple job roles
