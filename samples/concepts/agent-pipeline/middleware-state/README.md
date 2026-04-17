# Middleware State Passing

Demonstrates the **3 ways to share state between middleware layers** in the Microsoft Agent Framework pipeline. All three methods use the same domain — a content moderation pipeline — so you can compare techniques side-by-side.

## Quick reference

| Method | Direction | API | Best for |
|--------|-----------|-----|----------|
| `AgentRunOptions.AdditionalProperties` | DOWN (caller → inner) | `options.AdditionalProperties["key"]` | Per-request metadata, feature flags, correlation IDs |
| `AgentSession.StateBag` | Session-wide | `session.StateBag.SetValue / TryGetValue` | Multi-turn state that persists across invocations |
| `AgentResponse.AdditionalProperties` | UP (inner → caller) | `response.AdditionalProperties["key"]` | Out-of-band response metadata, routing signals |

## Files

| File | Demonstrates |
|------|-------------|
| [Method1_RunOptionsProperties.cs](Method1_RunOptionsProperties.cs) | `AgentRunOptions.AdditionalProperties` — request ID, user tier injected by caller, read by two middleware layers |
| [Method2_SessionStateBag.cs](Method2_SessionStateBag.cs) | `AgentSession.StateBag` — subscription tier written on Turn 1, read and used on Turn 2 |
| [Method3_ResponseProperties.cs](Method3_ResponseProperties.cs) | `AgentResponse.AdditionalProperties` — moderation flag attached by inner middleware, read by outer for routing |

## How to run

```powershell
dotnet run --project samples/concepts/agent-pipeline/middleware-state/MiddlewareState.csproj
```

Runs all three methods in sequence. Each prints its own titled section.

## Prerequisites

OpenAI API key stored in user secrets (tied to `Learn.Shared`):

```powershell
dotnet user-secrets set "OpenAIApiKey" "sk-..." --project src/Learn.Shared/Learn.Shared.csproj
```

## Key concepts

**Pipeline direction matters.** `AgentRunOptions` flows request-time data *into* the pipeline before the LLM is called. `AgentResponse` flows result-time data *out* after the LLM responds. `AgentSession.StateBag` is orthogonal — it persists across multiple invocations and is not request/response scoped.

**`StateBag` requires reference types.** `AgentSessionStateBag.SetValue<T>` / `TryGetValue<T>` only accept reference types. Wrap primitive values (strings, ints) in a small sealed class.

**`AdditionalProperties` starts null.** Both `AgentRunOptions.AdditionalProperties` and `AgentResponse.AdditionalProperties` are `null` by default. Always initialize with `??= new AdditionalPropertiesDictionary()` before writing.
