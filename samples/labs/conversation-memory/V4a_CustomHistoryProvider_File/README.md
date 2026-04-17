# V4a — Custom ChatHistoryProvider (File-backed)

## What You'll Learn

How to implement the `ChatHistoryProvider` interface to replace the default in-memory storage with any external backend. This sample uses a local JSON file — the simplest real backend, no external services required.

## Key Concept

`ChatHistoryProvider` controls where messages live. Override two methods:

```csharp
public sealed class FileBackedChatHistoryProvider : ChatHistoryProvider
{
    // Load history from storage before each LLM call
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken ct = default)
    { ... }

    // Save new messages to storage after each LLM call
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken ct = default)
    { ... }
}
```

Register it:
```csharp
AIAgent agent = client.GetChatClient("model")
    .AsAIAgent(new ChatClientAgentOptions
    {
        ChatHistoryProvider = new FileBackedChatHistoryProvider()
    });
```

## Critical Pattern: ProviderSessionState\<T\>

> **A single provider instance is shared across ALL sessions.**
> NEVER store session-specific data (file paths, database keys, user IDs) in provider instance fields.

Use `ProviderSessionState<T>` to store per-session data **inside the session itself**:

```csharp
private readonly ProviderSessionState<State> _sessionState = new(
    stateInitializer: _ => new State { FilePath = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid()}.json") },
    stateKey: nameof(FileBackedChatHistoryProvider));  // unique key, no clashes

public override string StateKey => _sessionState.StateKey;

// In your method:
State state = _sessionState.GetOrInitializeState(context.Session);
// ... use state.FilePath ...
_sessionState.SaveState(context.Session, state); // persist changes
```

The `stateKey` must be unique per provider type. It's how the framework serializes/deserializes each provider's portion of the session.

## What the Demo Shows

1. **Run 1**: Two-turn conversation — history stored to a temp JSON file
2. Session serialized (file path travels inside the serialized session)
3. **Run 2**: Session restored — `ProvideChatHistoryAsync` loads from the same file — agent recalls context

## Inspect the File

The sample prints the temp file path. Open it to see the raw `ChatMessage[]` JSON.

## Running This Sample

```
Enter sample number: 1203
```
