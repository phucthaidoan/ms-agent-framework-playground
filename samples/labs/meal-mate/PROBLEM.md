# Personalized Cooking Coach — Microsoft Agent Framework Lab
## Topic: Context Providers

---

## Problem Statement

**MealMate** is a consumer cooking app with 200,000 monthly active users. The app includes an AI
cooking coach that answers questions like "what should I cook tonight?" or "how do I fix this
sauce?". Currently every conversation starts completely cold: the agent has no idea what
ingredients the user has at home, knows nothing about their dietary restrictions, and forgets
everything the user said two messages ago.

The result is embarrassing and sometimes dangerous: the agent enthusiastically recommends a
peanut-based satay sauce to a user with a nut allergy, suggests a recipe requiring chicken breast
when the user's pantry holds only canned tuna and pasta, and asks "are you vegetarian?" on turn
3 of a conversation where the user already said so on turn 1.

The engineering team wants the agent to feel like a personal chef who knows you. Before the model
even starts reasoning, it must know what is in the user's fridge and pantry today, what their
dietary restrictions are, and what happened earlier in this cooking session — "we already decided
on Italian tonight, you said you're out of parmesan." None of this context should require the
model to decide to ask for it. It must always be there, injected unconditionally.

The challenge: the AI model has no memory. Every invocation starts from zero unless something
explicitly builds its context. Tools can query data, but only if the model chooses to call them
on that turn. For safety-critical data like nut allergies, that reactive guarantee is not enough.
**Context Providers are the mechanism that makes context unconditional.**

---

## Domain Entities

```
UserProfile
  - UserId          : string
  - Name            : string
  - DietaryTags     : string[]   // e.g. ["vegetarian", "nut-allergy", "lactose-intolerant"]
  - SkillLevel      : string     // "beginner" | "intermediate" | "advanced"

PantryItem
  - Name            : string
  - Quantity        : string     // e.g. "500g", "2 cans", "half a bottle"
  - ExpiresInDays   : int?       // null = non-perishable

CookingSessionFact
  - Turn            : int
  - Fact            : string     // e.g. "User chose Italian cuisine for tonight"
```

---

## Concept Mapping

| # | Concept | Official Doc | Where it fits | Why needed | What breaks without it |
|---|---------|-------------|--------------|------------|------------------------|
| 1 | `AIContextProvider` (base class) | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Wraps every `RunAsync()` call | Unconditionally injects user profile + pantry before model reasons | Model has no idea user is vegetarian or has a nut allergy — gives dangerous advice |
| 2 | `ProvideAIContextAsync` override | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Pre-invocation hook | Injects `DietaryTags`, pantry list, skill level into the prompt as structured messages | Agent asks "do you have eggs?" when eggs are already listed as out-of-stock in pantry |
| 3 | `StoreAIContextAsync` override | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Post-invocation hook | Extracts session decisions ("chose Italian", "skipped spicy option") and persists in session state | Agent forgets on turn 2 that the user already picked a cuisine on turn 1 — asks again |
| 4 | `ProviderSessionState<T>` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Per-session state store inside each provider | Stores `List<CookingSessionFact>` per session without leaking across users | Provider stores facts in a field → User A's Italian dinner decision bleeds into User B's session |
| 5 | Multiple providers composing | [Agent Pipeline Architecture](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline) | `AIContextProviders` list on `ChatClientAgentOptions` | Separates profile injection, pantry injection, and session-fact logging into testable units | One monolithic provider can't be tested or swapped (e.g. replace in-memory pantry with a real fridge API) |

---

## Additional Concepts (mechanically required)

| # | Concept | Why added | What it enables |
|---|---------|-----------|----------------|
| A | `AgentSession` | Every hook receives the session — you cannot read/write per-session facts without it | Required to call `ProviderSessionState.GetOrInitializeState(session)` and `SaveState(session, state)` |
| B | `InvokingContext` / `InvokedContext` | Method signatures of both hooks receive these; you cannot implement overrides without knowing what they expose | `InvokingContext.AIContext.Messages` is where you append your injected messages; `InvokedContext.ResponseMessages` is what you parse to extract session facts |
| C | `AIContext` (return type) | `ProvideAIContextAsync` must return `AIContext`; its `Messages` and `Instructions` fields control what the model sees | Returning `new AIContext()` means the provider injects nothing — returning `null` crashes the pipeline |

---

## Failure Scenarios

| Scenario | What fails | Which concept prevents it |
|----------|-----------|--------------------------|
| Provider stores `userId` as a field | User A's nut-allergy flag overwrites User B's profile mid-session — User B gets allergy-unsafe suggestions | `ProviderSessionState<T>` scopes state to the session, not the provider instance |
| No `ProvideAIContextAsync` override for dietary tags | Model recommends a peanut satay sauce to a user with `"nut-allergy"` in their profile — app receives a complaint | `ProvideAIContextAsync` unconditionally injects `DietaryTags` before every invocation |
| No `StoreAIContextAsync` — session facts lost | Turn 1: "Let's make pasta tonight." Turn 2: "What ingredients do I need?" → Agent asks "What are you making?" again | `StoreAIContextAsync` persists "decided on pasta" into session state after turn 1 |
| `ProvideAIContextAsync` returns `null` | `NullReferenceException` in the framework's merge logic — agent crashes mid-conversation | Base class contract: always return `new AIContext()` (possibly empty), never `null` |
| Pantry provider reads session facts from `InvokingContext.AIContext.Messages` without filtering | Session-fact messages injected by `FactLoggingProvider` are mistakenly parsed as pantry items | Filter to `AgentRequestMessageSourceType.External` to read only real user messages |
| Single provider handles profile + pantry + facts | Cannot unit-test allergy injection without also wiring up a pantry service and a fact store | Three providers compose independently; each has its own `StateKey` and fake service dependency |

---

## Deep Dive Q&A

**Q1: Why not just put the user's dietary restrictions in the system prompt once at session creation?**

Answer: The system prompt is set once when the agent is configured and cannot vary per user or per
session without rebuilding the agent. With `ProvideAIContextAsync`, the provider runs before every
invocation and receives the current `AgentSession` — so it can load the *current user's* dietary
tags from a profile service for that specific session. Ten concurrent users each get their own
profile injected without any shared state. System prompt injection only works when context is
identical for everyone; context providers work when it must differ per user.

**Q2: The `ProviderSessionState<T>` stores a `List<CookingSessionFact>`. What concurrency bug
occurs if you store that list as a field on the provider instead?**

Answer: `AIContextProvider` instances are shared across all sessions. If `FactLoggingProvider` has
a field `private List<CookingSessionFact> _facts = new()`, then User A's "decided on Italian"
fact and User B's "decided on Mexican" fact are added to the same list. User B's agent starts
suggesting Italian dishes. The fix: `ProviderSessionState<T>` stores the list inside
`AgentSession.StateBag` under a provider-unique key, so each session has its own isolated list.
Concrete rule: **never store per-session data in provider fields**.

**Q3: Why do you need `StoreAIContextAsync` when you could just keep a running transcript in
the system prompt?**

Answer: A running transcript grows unboundedly — every turn appends the full exchange. At 20 turns,
you are pushing thousands of tokens of raw dialogue to the model on every invocation. `StoreAIContextAsync`
extracts only *meaningful facts* ("chose Italian", "skipped spicy option", "out of parmesan") —
a compact, structured log that stays small regardless of conversation length. The model gets the
signal without the noise. Additionally, the transcript approach cannot distinguish which facts are
still relevant (the user changed their mind about Italian at turn 5), whereas a fact store can be
updated or invalidated.

**Q4: The `PantryProvider` and `ProfileProvider` both inject messages before the model runs.
In what order does the model see them, and does it matter?**

Answer: Providers run in registration order. If you register `[ProfileProvider, PantryProvider]`,
`ProfileProvider.InvokingAsync()` runs first, appending a profile message to `AIContext`. Then
`PantryProvider.InvokingAsync()` receives the already-augmented context and appends a pantry
message. The model sees: `[profile message, pantry message, user input]`. Order matters for
prompt structure — put the most safety-critical context (dietary restrictions) first so it is
not accidentally deprioritized by the model. The `FactLoggingProvider` should come last so the
model sees resolved facts closest to the user input, maximising relevance weighting.

**Q5: What happens if the user's pantry data changes between turn 1 and turn 2 (they just used
the last egg)?**

Answer: Because `ProvideAIContextAsync` is called fresh on every invocation, it re-queries the
pantry service each time. If the pantry service reflects the updated stock (egg count = 0), the
provider injects the updated pantry list on turn 2 — the model naturally stops suggesting
egg-based recipes. This is the key advantage over a one-time system prompt: the context is
live-reloaded on every turn. To make this work, the pantry service must be stateless (no caching
in the provider field), and the provider simply calls it with the current `userId` retrieved from
session state on each invocation.

---

## Lab Versions

| Version | Folder | New Concept | What it adds vs previous |
|---------|--------|-------------|--------------------------|
| V1 | `V1_BaselineAgent` | No providers — raw agent | Agent runs with hardcoded system prompt only; has no profile, pantry, or session memory |
| V2 | `V2_ProfileProvider` | `AIContextProvider` + `ProvideAIContextAsync` | Injects user profile (name, dietary tags, skill level) before every turn; agent stops asking "are you vegetarian?" |
| V3 | `V3_PantryProvider` | Second provider composing | Adds `PantryProvider` alongside `ProfileProvider`; agent now suggests recipes using only available ingredients |
| V4 | `V4_FactLoggingProvider` | `StoreAIContextAsync` + `ProviderSessionState<T>` | After each turn, extracts session decisions and stores in session state; next turn's context includes what was already decided |
| V5 | `V5_AdvancedFiltering` | `InvokingCoreAsync` override | Replaces `ProvideAIContextAsync` in `PantryProvider` with `InvokingCoreAsync` to demonstrate explicit message filtering and source stamping |

---

## Test Scenarios

### V1 — BaselineAgent (no providers)

**Scenario A — Unknown dietary restriction:**
Input: "What should I cook tonight?"
Expected (ideal): Agent knows user is vegetarian and nut-allergic.
Actual (V1): Agent suggests chicken satay with peanut sauce — no dietary awareness at all.

**Scenario B — Pantry blindness:**
Input: "I want to make pasta."
Expected (ideal): Agent knows user has no parmesan and suggests a substitution.
Actual (V1): Recipe includes parmesan without question.

**Scenario C — Forgotten session decision:**
Turn 1: "Let's do Italian tonight."
Turn 2: "What should I start with?"
Expected (ideal): Agent builds on the Italian theme.
Actual (V1): Agent asks "What cuisine are you in the mood for?" — completely forgot turn 1.

**Scenario D — Skill level mismatch:**
Input: "How do I make a beurre blanc?"
Expected (ideal): Agent knows user is a beginner and simplifies the explanation.
Actual (V1): Agent gives a professional-level technique explanation with no adaptation.

---

### V2 — ProfileProvider (`ProvideAIContextAsync`)

**Scenario A — Allergy-safe first response:**
User profile: `DietaryTags = ["nut-allergy", "vegetarian"]`
Input: "Suggest something quick for dinner."
Expected: Agent suggests a nut-free, vegetarian meal without being asked.
Pass condition: Response contains no nut-based ingredients; contains no meat.

**Scenario B — Skill-adapted explanation:**
User profile: `SkillLevel = "beginner"`
Input: "How do I julienne carrots?"
Expected: Agent explains with simple language, no assumed knife skills.
Pass condition: Response does not use terms like "chiffonade" or "brunoise" without explanation.

**Scenario C — Profile injected on every turn:**
Turn 1: "What's a good vegetarian dish?"
Turn 2: "Can you give me a non-vegetarian version?"
Expected: Turn 2 response acknowledges the dietary restriction and declines or flags it.
Pass condition: Agent does not suggest meat ignoring the stored `vegetarian` tag.

**Scenario D — Unknown user graceful handling:**
Simulate profile service returning null for an unrecognised `userId`.
Expected: Provider returns `new AIContext()` — agent proceeds with a generic response.
Pass condition: No exception thrown; agent responds with a helpful but generic suggestion.

---

### V3 — PantryProvider (composing multiple providers)

**Scenario A — Ingredient-aware suggestion:**
Pantry: `["pasta (500g)", "canned tuna (2 cans)", "olive oil", "garlic", "lemon"]`
Input: "What can I make for dinner?"
Expected: Agent suggests pasta al tonno or similar — uses only available ingredients.
Pass condition: Response does not require an ingredient absent from the pantry list.

**Scenario B — Substitution surfaced proactively:**
Pantry: `["pasta (500g)", "garlic", "olive oil"]` — no parmesan.
Input: "I want to make cacio e pepe."
Expected: Agent flags that parmesan/pecorino is missing and suggests a substitution.
Pass condition: Response mentions the missing cheese and offers an alternative.

**Scenario C — Both providers fire together:**
Profile: `DietaryTags = ["lactose-intolerant"]`; Pantry: `["milk (1L)", "pasta", "garlic"]`
Input: "What pasta dish can I make?"
Expected: Agent avoids dairy-heavy suggestions despite milk being in pantry.
Pass condition: Response references the lactose intolerance AND the pantry contents.

**Scenario D — Empty pantry graceful handling:**
Simulate pantry service returning an empty list.
Expected: Provider returns `new AIContext()` — agent asks user what they have available.
Pass condition: No exception; agent responds with "What ingredients do you have on hand?"

---

### V4 — FactLoggingProvider (`StoreAIContextAsync` + `ProviderSessionState<T>`)

**Scenario A — Session decision persists across turns:**
Turn 1: Agent response includes "Great, let's go with Italian tonight."
Turn 2: "What should I start with?"
Expected: Turn 2 context includes "Decided: Italian cuisine for tonight."
Pass condition: Turn 2 response suggests an Italian starter — does not ask about cuisine again.

**Scenario B — Multiple facts accumulate:**
Turn 1: Decided on Italian.
Turn 2: Decided to skip pasta (too heavy).
Turn 3: "What's a good main?"
Expected: Turn 3 context includes both facts; agent suggests a non-pasta Italian main.
Pass condition: Agent does not suggest pasta; stays within Italian.

**Scenario C — Session isolation (two parallel users):**
User A session: accumulates facts about an Italian dinner.
User B session: accumulates facts about a Thai dinner.
Expected: User A's Italian facts do not appear in User B's context.
Pass condition: Each session's fact log contains only its own decisions.

**Scenario D — Empty facts on first turn:**
Turn 1, no prior facts stored.
Expected: Provider returns empty `AIContext()` — no facts message injected.
Pass condition: No exception; agent responds normally to the opening message.

---

### V5 — AdvancedFiltering (`InvokingCoreAsync` override)

**Scenario A — Provider messages excluded from pantry parsing:**
`ProfileProvider` injects a message: "User is vegetarian."
`PantryProvider` (using `InvokingCoreAsync`) queries only `External`-source messages.
Expected: "vegetarian" from the profile message is NOT parsed as a pantry item.
Pass condition: Log shows `filteredInputMessages` count = 1 (only the actual user input).

**Scenario B — Fact-log entries not misread as pantry:**
Turn 2: `FactLoggingProvider` has injected "Decided: Italian cuisine."
`PantryProvider` filters to `External` only.
Expected: "Italian" is not parsed as an available ingredient.
Pass condition: Pantry suggestion uses only items from the real pantry list.

**Scenario C — Injected messages carry source stamp:**
After `InvokingCoreAsync` runs, inspect `context.AIContext.Messages`.
Expected: Provider's messages carry `AgentRequestMessageSourceType.AIContextProvider` stamp.
Pass condition: Pantry messages are distinguishable from user messages in the log.

**Scenario D — Exception in agent skips fact storage:**
Simulate the LLM call throwing (`InvokeException != null`).
Expected: `InvokedCoreAsync` detects the exception and skips `StoreAIContextAsync`.
Pass condition: No second exception from the provider; fact log unchanged.

---

## Implementation Notes

- **Target framework**: net9.0 (matches existing projects in this repo)
- **NuGet packages required**: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`, `Microsoft.Extensions.AI.OpenAI`
- **External dependencies**: All providers use hardcoded/in-memory fake data — no cloud subscription required
- **Each version**: Separate `.csproj` in its own sub-folder; runs as a console app with a fixed script of hardcoded turns (no interactive console input)
- **Fake services to implement per version**:
  - `FakeProfileService` — returns `UserProfile` by `userId` (hardcoded dictionary, 3 users with different tags)
  - `FakePantryService` — returns `List<PantryItem>` by `userId` (hardcoded per user, one user has empty pantry)
  - Session fact log replaced by `ProviderSessionState<List<CookingSessionFact>>` from V4 onward

---

## Sources

- [Context Providers | Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp)
- [AIContextProvider Class | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.aicontextprovider?view=agent-framework-dotnet-latest)
- [Adding Context Providers | Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/journey/adding-context-providers)
- [Agent Pipeline Architecture | Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline)
