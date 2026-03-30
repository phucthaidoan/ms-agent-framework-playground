# PRD: CV Screening — V4: Multiple Job Roles (Dynamic Instructions)

## Learning Goal

Allow the user to **pick a job role** before pasting a CV. The screener agent's
instructions are built dynamically based on the selected role, so the same agent
evaluates different criteria depending on context.

**New AF concept:** Dynamic `instructions` — composing system prompts at runtime from data,
rather than hardcoding them.

---

## What Changes vs Base Sample

| Aspect | Base | V4 |
|---|---|---|
| Job description | Hardcoded (Senior C# Developer) | Chosen by user at runtime |
| Agent instructions | Static string | Built dynamically from selected `JobRole` |
| Interviewers | Same 3 for everyone | Role-specific panel |

---

## Job Roles (hardcoded menu)

| # | Role | Key Requirements | Interview Panel |
|---|---|---|---|
| 1 | Senior C# Developer | 5+ yrs C#, Azure, .NET, REST APIs. Bonus: AI/ML | Alice (Tech Lead), Bob (HR), Carol (CTO) |
| 2 | QA Engineer | 3+ yrs test automation, Selenium/Playwright, CI/CD. Bonus: performance testing | Dave (QA Lead), Bob (HR), Eve (Engineering Manager) |
| 3 | Product Manager | 3+ yrs PM experience, Agile/Scrum, stakeholder management. Bonus: technical background | Frank (CPO), Bob (HR), Grace (UX Lead) |

---

## Flow

```
Print available roles
User selects role (1 / 2 / 3)
      │
      ▼
Build agent instructions dynamically from selected role
      │
      ▼
User pastes CV
      │
      ▼
CvScreenerAgent (dynamic instructions) ──► ScreeningResult
      │
      ▼
IsQualified?
  No  ──► Print "not suitable"
  Yes ──► InterviewCoordinatorAgent notifies role-specific panel
```

---

## Key Code Pattern to Learn

```csharp
// Role definition as data
record JobRole(string Title, string Requirements, string[] Interviewers);

JobRole[] roles = [
    new("Senior C# Developer", "5+ yrs C#, Azure, .NET, REST APIs. Bonus: AI/ML",
        ["Alice (Tech Lead)", "Bob (HR)", "Carol (CTO)"]),
    new("QA Engineer", "3+ yrs test automation, Selenium/Playwright, CI/CD",
        ["Dave (QA Lead)", "Bob (HR)", "Eve (Engineering Manager)"]),
    new("Product Manager", "3+ yrs PM, Agile/Scrum, stakeholder management",
        ["Frank (CPO)", "Bob (HR)", "Grace (UX Lead)"])
];

// Dynamic instructions
string instructions =
    $"You are a Talent Acquisition specialist. " +
    $"Evaluate the CV against this role: '{selectedRole.Title}'. " +
    $"Requirements: {selectedRole.Requirements}. " +
    $"Be strict but fair.";

ChatClientAgent screenerAgent = client
    .GetChatClient("gpt-4.1-nano")
    .AsAIAgent(instructions: instructions);
```

---

## Sample CVs

Add two new CVs for QA and PM roles, alongside the 4 existing ones:

### CV-5: QA Engineer match
```
Name: Tom Baker
Experience: 4 years QA automation engineer.
Skills: Selenium, Playwright, GitHub Actions, Azure DevOps pipelines, C# test scripts.
Education: BSc Computer Science.
```

### CV-6: Product Manager match
```
Name: Lisa Wong
Experience: 5 years Product Manager at SaaS companies.
Skills: Agile, Scrum, JIRA, stakeholder management, user story writing, OKR planning.
Education: MBA, BSc Business Informatics.
```

---

## Out of Scope

- Agent-as-Tool composition
- Streaming
- Multi-turn chat
