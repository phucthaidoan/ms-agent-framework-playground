# PRD: AI-Assisted CV Screening & Interview Coordinator

## Overview

A simple multi-agent console application demonstrating Microsoft Agent Framework by simulating a
Talent Acquisition workflow — CV screening followed by automated interview preparation notifications.

## Goal

Learn and demonstrate these AF concepts:

- **Structured Output** — extract candidate info from CV text
- **Tool Calling** — simulate sending notifications
- **Sequential Agent Composition** — screen first, pass result to coordinator
- **Instructions** — domain-specific prompting

> **Future learning (not in scope now):** Refactor to use **Agent-as-Tool** composition where
> `InterviewCoordinatorAgent` calls `CvScreenerAgent` as a tool.

---

## Scenario

User pastes a raw CV into the console. The system:

1. **Screens the CV** against a hardcoded job description using `CvScreenerAgent`
2. **Returns a structured verdict** (qualified / not qualified + reasoning)
3. **If qualified**, `InterviewCoordinatorAgent` notifies each hardcoded interviewer via a simulated tool call

---

## Agents

| Agent                        | Role                                                               | Key AF Concept    |
| ---------------------------- | ------------------------------------------------------------------ | ----------------- |
| `CvScreenerAgent`            | Reads CV, scores against job criteria, returns structured result   | Structured Output |
| `InterviewCoordinatorAgent`  | Receives screening result, calls `NotifyInterviewer` per interviewer | Tool Calling    |

---

## Structured Output Shape

```csharp
class ScreeningResult
{
    bool IsQualified { get; set; }
    string CandidateName { get; set; }
    string Summary { get; set; }           // 2-3 sentence summary
    List<string> MatchedCriteria { get; set; }
    List<string> MissingCriteria { get; set; }
}
```

---

## Hardcoded Job Description

> Senior C# Developer — 5+ years experience, Azure, .NET, REST APIs required. Bonus: AI/ML experience.

## Hardcoded Interviewers

```
["Alice (Tech Lead)", "Bob (HR)", "Carol (CTO)"]
```

## Tool: `NotifyInterviewer`

Simulated — prints to console:

```
[NOTIFICATION] Alice (Tech Lead): Please prepare for interview with John Doe.
```

---

## Sample CVs for Testing

### CV-1: Strong match

```
Name: John Doe
Experience: 7 years C# developer at FinTech Corp.
Skills: .NET 8, Azure (AKS, Service Bus, Blob), REST APIs, SQL Server, Azure OpenAI.
Education: BSc Computer Science.
```

### CV-2: Partial match (missing Azure)

```
Name: Jane Smith
Experience: 6 years C# developer, mostly on-premises systems.
Skills: .NET Framework, WCF, SQL Server, WinForms. No cloud experience.
Education: BSc Software Engineering.
```

### CV-3: Not a match (wrong field)

```
Name: Alex Johnson
Experience: 5 years Java/Spring Boot developer.
Skills: Java, Kubernetes, PostgreSQL, React. No C# or .NET experience.
Education: BSc Computer Science.
```

### CV-4: Great match with AI bonus

```
Name: Sara Lee
Experience: 8 years .NET developer, last 3 years focused on AI solutions.
Skills: C#, .NET 8, Azure OpenAI, Semantic Kernel, REST APIs, Azure DevOps.
Education: MSc Artificial Intelligence.
```

---

## Flow

```
User pastes CV
      │
      ▼
CvScreenerAgent  ──► ScreeningResult (structured output)
      │
      ▼
 IsQualified?
   No  ──► Print "Candidate not suitable. No further action."
   Yes ──► InterviewCoordinatorAgent
                  │
                  ▼
         NotifyInterviewer tool × 3 interviewers
                  │
                  ▼
         Print notifications to console
```

---

## Out of Scope

- Agent-as-Tool composition (future)
- Real email/calendar integration
- File upload UI
- Database
- Multi-turn conversation
