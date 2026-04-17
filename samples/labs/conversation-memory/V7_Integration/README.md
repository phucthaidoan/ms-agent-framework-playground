# V7 — Integration (All Concepts Combined)

## What You'll Learn

How all the Conversations & Memory concepts work **together** in a single realistic Customer Support Bot that persists across simulated restarts.

## Building Blocks Used

| Concept | Sample | Role in V7 |
|---------|--------|-----------|
| `AgentSession` | V1 | Links all turns together |
| Session serialization | V2 | Survives the Phase 1 → Phase 2 restart |
| `FileBackedChatHistoryProvider` | V4a | Durable storage — history survives restarts |
| `UserPreferenceContextProvider` | V5 | Preference injected automatically every turn |
| `PipelineCompactionStrategy` | V6 | Keeps history bounded in Phase 3 |

## Three-Phase Demo

### Phase 1 — First Session
- User states contact preference (email) → `UserPreferenceContextProvider` stores it
- User reports issue (speed drop) → `FileBackedChatHistoryProvider` saves messages to file
- Session serialized to JSON at the end

### Phase 2 — After Simulated Restart
- New agent created (fresh instances of all providers)
- Session restored from Phase 1's JSON → file path + preference state travel inside the session
- Agent recalls: full conversation history (from file) + contact preference (from provider state)

### Phase 3 — Long Conversation
- 6 more turns added → compaction triggered per turn
- Stored history continues to grow (+2 msgs per turn — compaction never modifies storage)
- LLM's view is trimmed by compaction, keeping token costs bounded

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        AIAgent                               │
│                                                              │
│   ChatHistoryProvider          AIContextProviders            │
│   ┌───────────────────┐   ┌──────────────────────────────┐  │
│   │ FileBackedHistory │   │ UserPreferenceProvider       │  │
│   │ (load/save file)  │   │ (inject/extract preferences) │  │
│   └───────────────────┘   └──────────────────────────────┘  │
│                                                              │
│   AsBuilder().UseAIContextProviders()                        │
│   ┌───────────────────────────────────────────────────────┐  │
│   │ CompactionProvider (PipelineCompactionStrategy)       │  │
│   │  1. ToolResult → 2. Summarization → 3. SlidingWindow  │  │
│   │  4. Truncation (backstop)                             │  │
│   └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                         │
                  AgentSession
         (carries state for all providers +
          serializable across restarts)
```

> **Important:** `CompactionProvider` does NOT modify stored history. It builds a transient
> compacted view for each LLM call only. `FileBackedChatHistoryProvider` always retains the
> full original message list. The value of compaction is **managing token costs per call**,
> not reducing storage size.

## Key Wiring Pattern

```csharp
AIAgent agent = chatClient
    .AsBuilder()
    .UseAIContextProviders(new CompactionProvider(pipeline))  // runs inside tool loop
    .BuildAIAgent(new ChatClientAgentOptions
    {
        ChatHistoryProvider = new FileBackedChatHistoryProvider(),
        AIContextProviders  = [new UserPreferenceContextProvider()]
    });
```

## Running This Sample

```
Enter sample number: 1207
```
