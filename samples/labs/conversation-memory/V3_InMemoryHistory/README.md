# V3 — InMemory History Provider (Raw Message Access)

## What You'll Learn

How to access the raw `List<ChatMessage>` stored by the built-in `InMemoryChatHistoryProvider` — useful for auditing, UI display, logging, or feeding history into another system.

## Key Concept

When you use `AsAIAgent()` with OpenAI Chat Completion, the framework automatically attaches an `InMemoryChatHistoryProvider`. This provider stores the full conversation as a typed list of `ChatMessage` objects. You can read them at any time:

```csharp
// After running some turns...
InMemoryChatHistoryProvider? provider = agent.GetService<InMemoryChatHistoryProvider>();
IList<ChatMessage> messages = provider?.GetMessages(session) ?? [];

// Inspect each message
foreach (ChatMessage msg in messages)
{
    Console.WriteLine($"[{msg.Role}]: {msg.Text}");
}
```

## Message Count

Each conversation turn stores **2 messages**:
- 1 `User` message (your input)
- 1 `Assistant` message (the bot's response)

After 3 turns: 6 messages total.

## Architecture

```
RunAsync(message, session)
         ↓
InMemoryChatHistoryProvider.ProvideChatHistoryAsync()  — loads history into request
         ↓
LLM call
         ↓
InMemoryChatHistoryProvider.StoreChatHistoryAsync()    — saves new messages
         ↓
GetMessages(session)  →  List<ChatMessage>  →  your code
```

## Use Cases

| Use Case | How |
|----------|-----|
| Audit log | Iterate messages after each turn |
| UI chat display | Render `Role` + `Text` |
| Export to file | `JsonSerializer.Serialize(messages)` |
| Token counting | Sum message text lengths |

## Running This Sample

```
Enter sample number: 1202
```
