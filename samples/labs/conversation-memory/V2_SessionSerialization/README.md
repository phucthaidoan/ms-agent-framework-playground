# V2 — Session Serialization & Restoration

## What You'll Learn

How to persist an `AgentSession` to durable storage and restore it — enabling sessions to survive application restarts, deployments, and distributed systems.

## Key Concept

`AgentSession` is in-memory by default. To keep conversation state across process restarts (e.g., store in a database, Redis, or pass to another service), serialize it first.

```csharp
// Serialize — returns a JsonElement (opaque state blob)
JsonElement element = await agent.SerializeSessionAsync(session);
string json = JsonSerializer.Serialize(element); // store this string anywhere

// Later (different process, after restart, etc.) — restore
JsonElement restored = JsonSerializer.Deserialize<JsonElement>(savedJson);
AgentSession session = await agent.DeserializeSessionAsync(restored);

// Continue exactly where you left off
string reply = (await agent.RunAsync("Continuing our conversation...", session)).Text;
```

## Architecture

```
Session in memory
      ↓
SerializeSessionAsync()  →  JsonElement  →  JSON string  →  [database / file / Redis]
                                                                      ↓
                                                       DeserializeSessionAsync()
                                                                      ↓
                                                         Restored session in memory
                                                                      ↓
                                                     Continue conversation as before
```

## Critical Rule

> **Restore with the SAME agent configuration.** `DeserializeSessionAsync` must be called on an agent with identical provider configuration (same `ChatHistoryProvider` type, same `AIContextProviders`) as the agent that created the session. The session state is tied to each provider's `StateKeys` values — if a provider is missing or its key changed, that provider's state is silently lost.

## Running This Sample

```
Enter sample number: 1201
```
