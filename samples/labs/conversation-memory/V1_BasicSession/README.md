# V1 — Basic Session (Multi-Turn Conversation)

## What You'll Learn

How `AgentSession` turns a stateless LLM into a stateful, multi-turn conversation partner.

## Key Concept

Every `RunAsync` call is independent by default — the agent has no memory of previous messages. An `AgentSession` is the container that links turns together. Pass it to every `RunAsync` call and the agent builds up a conversation history.

```csharp
// Create a session once
AgentSession session = await agent.CreateSessionAsync();

// Each call with the same session adds to the conversation history
await agent.RunAsync("My order #1234 is missing a blue widget.", session);
await agent.RunAsync("What order number did I mention?", session); // ✅ recalls #1234
```

Compare with calls **without** a session:

```csharp
await agent.RunAsync("My order #5678 is missing a red widget."); // no session
await agent.RunAsync("What order number did I mention?");        // ❌ has no idea
```

## Architecture

```
User message → RunAsync(message, session)
                         ↓
              session holds chat history
                         ↓
              LLM sees: [all previous messages] + [new message]
                         ↓
              Response added back to session history
```

## Key Points

- Sessions are **agent-specific** — a session created by one agent cannot be passed to a differently configured agent.
- The session stores history **locally** (in-memory by default), not on the OpenAI service.
- Sessions are lightweight value objects — create one per conversation, not per message.
- `CreateSessionAsync()` is always async because some backends (like Azure AI Foundry) create a remote conversation on construction.

## Running This Sample

```
Enter sample number: 1200
```
