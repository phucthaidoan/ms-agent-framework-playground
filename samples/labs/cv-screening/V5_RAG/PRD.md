# PRD: CV Screening V5 — RAG-Powered Screening

## Scenario

A Talent Acquisition system that screens candidate CVs against a **pre-built job role knowledge base** stored in a vector store. Instead of hardcoding job requirements into the agent's system prompt, role profiles are embedded and retrieved semantically at runtime — grounding the LLM's evaluation in retrieved facts.

This mirrors how real enterprise TA systems work: a central knowledge base of role profiles, competency frameworks, and interview question banks — all searchable by AI agents.

---

## New Concept: RAG (Retrieval-Augmented Generation)

| Step | What happens |
|---|---|
| **Ingest** | 3 job role profiles are embedded and stored in a SQLite vector store (`cv_job_profiles` collection) |
| **Retrieve** | Screener agent calls `search_job_profiles` tool with query derived from CV content |
| **Augment** | Retrieved profile text is injected into the LLM context alongside the CV |
| **Generate** | LLM evaluates CV against retrieved (not hardcoded) criteria — structured result returned |

**Why RAG vs hardcoded instructions?**
- **Scalability**: Add 100 roles without touching agent code — just add records to the store
- **Accuracy**: Richer, structured role profiles (competency areas, red flags, interview topics) vs a one-liner requirement string
- **Separation of concerns**: Knowledge lives in data, not in code

---

## Agents

### 1. ScreenerAgent (with `search_job_profiles` tool)
- **Instructions**: "You are a Talent Acquisition specialist. Use the `search_job_profiles` tool to retrieve the most relevant job profile for this CV, then evaluate the candidate against the retrieved criteria. Extract the candidate's full name."
- **Tools**: `search_job_profiles(query: string) → string`
- **Output**: `ScreeningResult` (structured via `RunAsync<ScreeningResult>`)

### 2. CoordinatorAgent (with `search_interview_questions` + `notify_interviewer` tools)
- **Instructions**: "You are an interview coordinator. Use `search_interview_questions` to fetch relevant interview topics for the role, then notify each interviewer with personalized, topic-relevant messages."
- **Tools**: `search_interview_questions(roleTitle: string) → string`, `notify_interviewer(name, candidate, message)`
- **Interviewers**: Alice (Tech Lead), Bob (HR), Carol (CTO)

---

## Knowledge Base (Job Role Profiles)

Three roles are embedded at startup. Each profile contains:

| Field | Description |
|---|---|
| `Title` | Role name (used as search anchor) |
| `Requirements` | Must-have and nice-to-have skills |
| `CompetencyAreas` | Evaluation dimensions |
| `InterviewTopics` | Topics for the interviewer panel |
| `RedFlags` | Warning signs in a CV |

**Roles:**
1. **Senior C# Developer** — 5+ years C#, .NET 6+, Azure, REST APIs. Bonus: AI/ML
2. **QA Engineer** — 3+ years test automation, Selenium/Playwright, CI/CD. Bonus: performance testing
3. **Product Manager** — 3+ years PM, Agile/Scrum, stakeholder management. Bonus: technical background

---

## Vector Store

- **Engine**: SQLite via `Microsoft.SemanticKernel.Connectors.SqliteVec`
- **DB path**: same temp DB as rag samples (`af-course-vector-store.db`), separate collection `cv_job_profiles`
- **Embedding model**: `text-embedding-3-small` (1536 dimensions)
- **Vector surface**: `"{Title}: {Requirements}. Key competencies: {CompetencyAreas}"`

---

## Structured Output

```csharp
class ScreeningResult
{
    bool IsQualified
    string CandidateName
    string MatchedRole        // NEW: which role was retrieved
    string Summary
    List<string> MatchedCriteria
    List<string> MissingCriteria
}
```

---

## Flow Diagram

```
User pastes CV
      │
      ▼
[Ingestion check] — Y → embed 3 profiles into cv_job_profiles
      │
      ▼
ScreenerAgent.RunAsync<ScreeningResult>(cvText)
      │
      ├── LLM calls: search_job_profiles("C# developer Azure experience")
      │       │
      │       └── VectorStore.SearchAsync(query, topK: 2)
      │               → returns formatted profile text
      │
      ├── LLM evaluates CV against retrieved profile
      └── returns ScreeningResult (structured)
            │
            ▼
      [Gate: IsQualified?]
      NO  → print rejection, exit
      YES →
            CoordinatorAgent.RunAsync(prompt, session)
              │
              ├── LLM calls: search_interview_questions("Senior C# Developer")
              │       → returns InterviewTopics from store
              │
              └── LLM calls: notify_interviewer × 3
                      → personalized messages with retrieved interview topics
```

---

## Sample CVs for Testing

Use CVs from `samples/labs/cv-screening/PRD.md`. Recommended test cases:

| CV | Expected role retrieved | Expected outcome |
|---|---|---|
| John Doe (C#, Azure, 7 years) | Senior C# Developer | Qualified — notifications include Azure/SOLID topics |
| Alex Johnson (Java, no Azure) | Senior C# Developer | Not qualified — missing .NET/Azure |
| Tom Baker (Selenium, CI/CD) | QA Engineer | Qualified — notifications include test pyramid topics |
| Sara Lee (PM, Scrum) | Product Manager | Qualified — notifications include OKR/prioritization topics |

---

## Key Teaching Moments (in code comments)

1. **Ingestion phase** — `// KEY CONCEPT: embed domain knowledge into vector store at startup`
2. **Search tool class** — `// KEY CONCEPT: RAG = search tool wrapping vector store — agent decides when to call it`
3. **Screener agent creation** — `// KEY CONCEPT: agent grounded by retrieval, not by hardcoded instructions`
4. **RunAsync call** — `// KEY CONCEPT: LLM calls search_job_profiles before evaluating — grounds its answer in retrieved facts`

---

## Out of Scope

- Refreshing/updating role profiles at runtime
- Multi-tenant or role-based access to the vector store
- Chunking long CVs before embedding
- Persistence of screening results
- Authentication / audit trail
