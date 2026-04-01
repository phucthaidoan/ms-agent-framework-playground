# V8 — Tool Calls in Conversation History

## What You'll Learn

How tool invocations are recorded in conversation history — what message roles appear, how they're structured, and what this means for custom `ChatHistoryProvider` implementations and compaction.

## The Tool-Calling Message Pattern

A single tool-calling turn produces up to **four messages** in the history:

```
User      → "Can you look up ticket T-1001?"
Assistant → [tool_call] LookupTicket(ticketId="T-1001")   ← structured, no text
Tool      → "Ticket T-1001: Laptop screen flicker..."      ← the return value
Assistant → "Ticket T-1001 is about a screen flicker..."   ← final answer
```

This is the `ChatRole` sequence:

| # | Role | Content | Notes |
|---|------|---------|-------|
| 1 | `User` | User message text | Normal user turn |
| 2 | `Assistant` | `FunctionCallContent` (no text) | LLM's decision to call a tool |
| 3 | `Tool` | `FunctionResultContent` | The tool's return value, keyed by `CallId` |
| 4 | `Assistant` | Final answer text | LLM's response after seeing the result |

## Why This Matters

### For Custom ChatHistoryProviders
Your `StoreChatHistoryAsync` receives **all four message types** via `context.RequestMessages` and `context.ResponseMessages`. If your storage backend only persists `User` and `Assistant` text messages, tool-using conversations will be corrupted on reload.

```csharp
// StoreChatHistoryAsync receives ALL roles — you must persist them all:
existing.AddRange(context.RequestMessages);   // includes Tool role messages
existing.AddRange(context.ResponseMessages ?? []);  // includes Assistant tool_call messages
```

### For Compaction
`ToolResultCompactionStrategy` treats the assistant tool_call + tool result pair (messages 2+3) as a **compaction group**. When history grows large, these groups are collapsed into a compact summary:

```
[Tool calls: LookupTicket, GetQueueStatus]
```

The final assistant answer (message 4) is preserved — only the raw arguments and results are removed.

## Inspecting History Programmatically

```csharp
InMemoryChatHistoryProvider? provider = agent.GetService<InMemoryChatHistoryProvider>();
IList<ChatMessage> messages = provider?.GetMessages(session) ?? [];

foreach (ChatMessage msg in messages)
{
    if (msg.Role == ChatRole.Assistant)
    {
        // Check if this is a tool_call decision vs a final answer
        var toolCalls = msg.Contents.OfType<FunctionCallContent>();
        // toolCalls is non-empty for the tool_call message, empty for the final answer
    }

    if (msg.Role == ChatRole.Tool)
    {
        var results = msg.Contents.OfType<FunctionResultContent>();
        // Each FunctionResultContent has .CallId (links back to the FunctionCallContent)
        // and .Result (the tool's return value)
    }
}
```

## Message Count Arithmetic

Without tools: `1 user turn = 2 messages` (User + Assistant)

With tools: `1 user turn = 2 + (2 × N) messages` where N = number of tool calls

Example — user asks one question that triggers two tool calls:
- 1 User message
- 1 Assistant message (two `FunctionCallContent` items inside it)
- 2 Tool messages (one per call result)
- 1 Assistant message (final answer)
= **5 messages total**

## Running This Sample

```
Enter sample number: 1208
```
