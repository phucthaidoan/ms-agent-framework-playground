# V5 — Custom AIContextProvider (Memory Injection)

## What You'll Learn

How `AIContextProvider` creates a memory layer that **runs transparently around every agent invocation** — injecting context before each LLM call and extracting facts after.

## Key Concept

Unlike `ChatHistoryProvider` (which stores all messages), `AIContextProvider` stores **extracted semantic facts** and injects them selectively:

```
Before LLM call:  ProvideAIContextAsync()  → inject instructions/messages
After LLM call:   StoreAIContextAsync()    → extract and persist facts
```

```csharp
public sealed class UserPreferenceContextProvider : AIContextProvider
{
    // INJECT: Add stored preferences as instructions before each call
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var prefs = _sessionState.GetOrInitializeState(context.Session);
        if (prefs.ContactMethod is null) return new(new AIContext());

        return new(new AIContext
        {
            Instructions = $"User prefers {prefs.ContactMethod} for follow-up."
        });
    }

    // EXTRACT: Scan messages for preference keywords and save them
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct = default)
    {
        // Parse user messages, update state...
        _sessionState.SaveState(context.Session, prefs);
        return default;
    }
}
```

## What `AIContext` Can Carry

| Field | Purpose |
|-------|---------|
| `Instructions` | Additional system-level instructions appended to the agent's base instructions |
| `Messages` | Additional messages injected into the conversation (e.g., retrieved memories) |
| `Tools` | Additional tools the agent should have access to for this call |

## ChatHistoryProvider vs AIContextProvider

| | `ChatHistoryProvider` | `AIContextProvider` |
|---|---|---|
| Stores | All messages verbatim | Extracted semantic facts |
| Purpose | Replay full conversation | Inject selective context |
| Example | "Here's the chat transcript" | "User prefers email" |

## Composability

Multiple providers can be registered — they run in registration order:

```csharp
AIContextProviders = [
    new UserPreferenceContextProvider(),
    new CustomerTierContextProvider(),    // inject VIP/standard tier
    new RecentOrderContextProvider(),     // inject recent order info
]
```

> **Note:** Providers run in order for both `ProvideAIContextAsync` (inject) and `StoreAIContextAsync` (persist). Each provider's `Instructions` are appended independently — they do not overwrite each other.

## Running This Sample

```
Enter sample number: 1205
```
