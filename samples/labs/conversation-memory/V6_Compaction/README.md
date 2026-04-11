# V6 — Compaction Strategies

## What You'll Learn

How to keep conversation history within token budgets using compaction strategies — preventing context window overflow and controlling API costs in long conversations.

## Why Compaction?

Every `RunAsync` call includes the full conversation history. Without compaction:
- Long conversations hit context window limits → errors
- More tokens = higher API costs
- More tokens = slower responses

## Important: Experimental API

```csharp
#pragma warning disable MAAI001  // required — compaction is experimental
```

## Registration Pattern

Register via `AsBuilder().UseAIContextProviders()` — **not** via `ChatClientAgentOptions.AIContextProviders`:

```csharp
AIAgent agent = chatClient
    .AsBuilder()
    .UseAIContextProviders(new CompactionProvider(myStrategy))  // ✅ correct
    .BuildAIAgent(new ChatClientAgentOptions { ... });
```

> Registering via `AsBuilder()` runs the provider **inside the tool-calling loop**, which is critical for correctness with `ChatHistoryProvider`.
>
> **Why not `ChatClientAgentOptions.AIContextProviders`?** That registration runs *outside* the tool loop — compaction would fire at the wrong point and produce incorrect message grouping. Always use `AsBuilder()` for `CompactionProvider`.
>
> **Note:** `AsBuilder()` is only needed when adding middleware like `CompactionProvider`. For V1–V5 scenarios without compaction, the simpler `.AsAIAgent(instructions: ...)` shorthand is sufficient.

## How Compaction Actually Works: Storage vs. LLM View

**Compaction does NOT modify stored history.** This is the most important thing to understand:

- `InMemoryChatHistoryProvider` always retains the **full original message list**
- `CompactionProvider` builds a transient, compacted view **inside the pipeline** before each LLM call
- That compacted view is sent to the LLM, then discarded — storage is never written back

```
RunAsync() call:
  [stored history: 14 msgs]
        ↓ CompactionProvider
  [compacted view: 4 msgs] → sent to LLM
        ↓ LLM response
  [2 new msgs appended] → stored history now 16 msgs
```

This means:
- Reading `InMemoryChatHistoryProvider.GetMessages()` after `RunAsync()` shows the full uncompacted list
- The LLM's context was smaller — you can observe this by calling `CompactionProvider.CompactAsync(strategy, storedMessages)` to re-apply compaction at display time
- Stored history grows by exactly 2 messages per turn (user + assistant), regardless of compaction

## Compaction Applies Only to In-Memory History

Compaction has **no effect** on service-managed agents (OpenAI Responses API, Azure AI Foundry, Copilot Studio). Those services manage their own context.

## The 5 Strategies

### 1. TruncationCompactionStrategy
Drops the oldest message groups when token count exceeds a threshold.
```csharp
new TruncationCompactionStrategy(
    trigger: CompactionTriggers.TokensExceed(32_000),
    minimumPreserved: 10)  // always keep 10 most recent groups
```

### 2. SlidingWindowCompactionStrategy
Keeps only the last N user turns.
```csharp
new SlidingWindowCompactionStrategy(
    trigger: CompactionTriggers.TurnsExceed(20))
```

### 3. ToolResultCompactionStrategy
Collapses verbose tool call groups into compact summaries like `[Tool calls: LookupOrder]`.
```csharp
new ToolResultCompactionStrategy(
    trigger: CompactionTriggers.TokensExceed(512))
```

### 4. SummarizationCompactionStrategy
Uses an LLM to summarize older messages into a single `[Summary]` group.
```csharp
new SummarizationCompactionStrategy(
    chatClient: summarizerChatClient,   // use a smaller/cheaper model
    trigger: CompactionTriggers.TokensExceed(1280),
    minimumPreserved: 4)
```

### 5. PipelineCompactionStrategy (Recommended for production)
Runs strategies in order — gentlest first, most aggressive last:
```csharp
PipelineCompactionStrategy pipeline = new([
    new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(512), 2, null),
    new SummarizationCompactionStrategy(summarizer, CompactionTriggers.TokensExceed(1280), 4, null, null),
    new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(20), 4, null),
    new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(32_000), 6, null),
]);
```

## Strategy Comparison

| Strategy | Aggressiveness | Preserves Context | Requires LLM |
|----------|---------------|-------------------|--------------|
| ToolResult | Low | High | No |
| Summarization | Medium | Medium | Yes |
| SlidingWindow | High | Low | No |
| Truncation | High | Low | No |
| Pipeline | Configurable | Depends | Depends |

## Running This Sample

```
Enter sample number: 1206
```
