# Conversations & Memory — Sample Learning Path

## Overview

This section contains progressive, focused samples that teach the Microsoft Agent Framework's conversation and memory concepts using a **Customer Support Bot** domain. Each sample is self-contained and teaches exactly one key concept.

---

## Why Customer Support Bot?

A support bot naturally motivates every concept:
- **Sessions**: A ticket must track context across multiple messages
- **Serialization**: Sessions must survive server restarts
- **History**: Support agents audit conversation logs
- **Custom storage**: Enterprise bots store history in real databases
- **Context injection**: The bot remembers user preferences (contact method, language)
- **Compaction**: Long support conversations exceed context windows

---

## Learning Path

| Sample | Concept | Complexity |
|--------|---------|------------|
| **V1 — Basic Session** | `AgentSession` enables multi-turn conversation | ⭐ |
| **V2 — Session Serialization** | Persist and restore sessions across restarts | ⭐⭐ |
| **V2b — File-Backed Session** | Write serialized session to disk; resume by typing the file path back; error handling for missing/corrupt files | ⭐⭐ |
| **V3 — InMemory History** | Access raw conversation history from the built-in provider | ⭐⭐ |
| **V4a — File History Provider** | Implement `ChatHistoryProvider` with file-system storage | ⭐⭐⭐ |
| **V4b — PostgreSQL History Provider** | Same interface, real database (Docker via Testcontainers) | ⭐⭐⭐ |
| **V5 — Custom Context Provider** | Inject and persist memory around each LLM call | ⭐⭐⭐ |
| **V6 — Compaction** | Manage long conversations: truncation, sliding window, summarization | ⭐⭐⭐⭐ |
| **V7 — Integration** | All concepts combined in one realistic bot | ⭐⭐⭐⭐⭐ |
| **V8 — Tool Calls in History** | How tool invocations are recorded as multi-role message groups | ⭐⭐⭐ |

---

## Key Concepts at a Glance

### AgentSession
The stateful container passed to every `RunAsync()` call. Without it, each call is a fresh conversation.

```csharp
AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("My order #1234 is missing", session);
await agent.RunAsync("What order number did I mention?", session); // ✅ recalls
```

### Session Serialization
Sessions can be serialized to JSON and restored later — enabling persistence across restarts.

```csharp
JsonElement serialized = await agent.SerializeSessionAsync(session);
AgentSession resumed = await agent.DeserializeSessionAsync(serialized);
```

> **Critical:** Always restore with the same agent configuration (same provider types and `StateKeys`). Missing providers lose their state silently.

### ChatHistoryProvider
Controls WHERE messages are stored (in-memory, file, database, Redis...).
Override `ProvideChatHistoryAsync()` (load) + `StoreChatHistoryAsync()` (save).

### ProviderSessionState\<T\>
Type-safe per-session state stored inside the `AgentSession` itself — never in provider instance fields.

```csharp
private readonly ProviderSessionState<State> _sessionState = new(
    _ => new State { FilePath = ... },
    nameof(MyProvider));           // unique state key
```

### AIContextProvider
Injects additional context (instructions, messages) before each LLM call and extracts state after.
Override `ProvideAIContextAsync()` (inject) + `StoreAIContextAsync()` (persist).

### Compaction
Reduces conversation history size to stay within token budgets.
Register via `AsBuilder().UseAIContextProviders(new CompactionProvider(...))`.

> **Note:** Compaction is experimental — add `#pragma warning disable MAAI001` to files using it.

### Agent Creation: `.AsAIAgent()` vs `.AsBuilder().BuildAIAgent()`

| Pattern | When to use |
|---------|------------|
| `.AsAIAgent(instructions: ..., name: ...)` | Simple agents — V1 through V5 |
| `.AsBuilder().UseAIContextProviders(...).BuildAIAgent(options)` | Required when adding `CompactionProvider` (V6, V7) — it must run inside the tool-calling loop |

For `ChatHistoryProvider` and `AIContextProviders`, use `ChatClientAgentOptions` in either pattern. Only `CompactionProvider` requires `AsBuilder().UseAIContextProviders()`.

---

## Prerequisites

- .NET 8.0 SDK
- OpenAI API key in user secrets (`OpenAIApiKey`)
- Docker Desktop (V4b only)

---

## Running the Samples

From the `SampleConsoleRunner`, select samples **1200–1207** from the interactive menu.

Sample numbers:
- 1200 — V1 Basic Session
- 1201 — V2 Session Serialization
- 1209 — V2b File-Backed Session Persistence
- 1202 — V3 InMemory History
- 1203 — V4a File History Provider
- 1204 — V4b PostgreSQL History Provider
- 1205 — V5 Custom Context Provider
- 1206 — V6 Compaction Strategies
- 1207 — V7 Integration
- 1208 — V8 Tool Calls in History
