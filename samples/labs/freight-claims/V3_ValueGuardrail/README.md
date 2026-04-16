# V3 — Value Guardrail (Stacked Middleware + Early-Exit)

## What You'll Learn

How to stack multiple Agent Run Middlewares and use the early-exit pattern to short-circuit the pipeline — stopping execution before the LLM is ever called.

## Key Concept

A middleware can return a response **without** calling `innerAgent.RunAsync`. This is the early-exit pattern. The guardrail extracts the declared value from the claim text, and if it exceeds the threshold it returns a hardcoded escalation response immediately:

```csharp
private static async Task<AgentResponse> ValueGuardrailMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    if (TryExtractDeclaredValue(messages, out int value) && value > 10_000)
    {
        Output.Yellow($"[GUARDRAIL] ${value:N0} exceeds threshold. Escalating — LLM not called.");

        // Early-exit: never calls innerAgent.RunAsync
        return new AgentResponse(
        [
            new ChatMessage(ChatRole.Assistant,
                $"Claim escalated to senior adjuster: declared value ${value:N0} exceeds threshold.")
        ]);
    }

    return await innerAgent.RunAsync(messages, session, options, cancellationToken);
}

// Registration — order is pipeline order
AIAgent agent = baseAgent
    .AsBuilder()
    .Use(AuditMiddleware, null)           // outermost — always fires
    .Use(ValueGuardrailMiddleware, null)  // second — may short-circuit
    .Build();
```

## Architecture

```
Claim text ──► RunAsync()
                    │
          ┌─────────▼───────────┐
          │   AuditMiddleware   │  ← [AUDIT PRE] always fires
          │                     │
          │  ┌──────────────────┴──┐
          │  │ ValueGuardrail      │
          │  │  value > $10k?      │
          │  │  Yes → return early │  ← LLM never called
          │  │  No  → forward ──► LLM + Tools
          │  └──────────────────┬──┘
          │                     │  ← [AUDIT POST] always fires (even on early-exit)
          └─────────────────────┘
```

The key insight: `[AUDIT POST]` fires on **both** paths. `AuditMiddleware` is the outer wrapper — its post-run code executes when `ValueGuardrailMiddleware` returns, whether that return came from calling the LLM or from early-exit. The audit log captures every case.

## Key Points

- The first `.Use()` call is the outermost wrapper. `AuditMiddleware` must be registered first to guarantee it captures the post-run side of every possible code path including early-exits from inner layers.
- Early-exit is simply a normal method return — `return new AgentResponse(...)` without calling `innerAgent.RunAsync`. The framework has no special early-exit mechanism; the middleware chain is just nested calls.
- `TryExtractDeclaredValue` searches for `$` in the last user message and parses the digits that follow. It works because the test prompts are structured; in production you would use a dedicated parser.
- `ValueGuardrailMiddleware` fires only on the normal path when `AuditMiddleware` calls `innerAgent.RunAsync`. If `AuditMiddleware` had its own early-exit, `ValueGuardrailMiddleware` would never run — outer middleware controls whether inner middleware executes.

## What's Missing (Leads to V4)

Low-value claims still reach `ApproveClaim` without any human review. The agent can call `ApproveClaim` autonomously, which writes to the claims system. V4 inserts `ApprovalGateMiddleware` at the **function invocation** level — it intercepts `ApproveClaim` specifically, mid-turn, before the tool executes.

## Running This Sample

```bash
cd samples/labs/freight-claims/V3_ValueGuardrail
dotnet run
```
