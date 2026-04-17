# V4 — Function Calling Middleware (HITL Approval Gate)

## What You'll Learn

How Function Calling Middleware intercepts individual tool invocations mid-turn — a distinct pipeline layer from Agent Run Middleware, registered in the same builder chain but firing at a different depth.

## Key Concept

Function Calling Middleware has a different delegate signature from Agent Run Middleware. It receives a `FunctionInvocationContext` describing the specific tool call and a `next` delegate to forward execution:

```csharp
private static async ValueTask<object?> ApprovalGateMiddleware(
    AIAgent agent,
    FunctionInvocationContext context,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken cancellationToken)
{
    // Only intercept ApproveClaim — let LookupShipment pass through unmodified
    if (context.Function.Name != nameof(ApproveClaim))
        return await next(context, cancellationToken);

    Console.Write("Approve this decision? (Y/N): ");
    string input = Console.ReadLine() ?? "N";

    if (!input.Equals("Y", StringComparison.OrdinalIgnoreCase))
        return "REJECTED_BY_REVIEWER. Do not retry ApproveClaim. " +
               "Inform the user the claim requires human escalation and stop.";
               // stop instruction prevents the LLM from retrying on rejection

    return await next(context, cancellationToken);  // calls the real ApproveClaim
}

// Registration — both middleware types in one chain
AIAgent agent = baseAgent
    .AsBuilder()
    .Use(AuditMiddleware, null)            // agent run middleware (two-argument overload)
    .Use(ValueGuardrailMiddleware, null)   // agent run middleware
    .Use(ApprovalGateMiddleware)           // function invocation middleware (one-argument overload)
    .Build();
```

The one-argument `.Use()` overload registers Function Invocation Middleware. The two-argument overload registers Agent Run Middleware. The framework routes each to the correct depth automatically.

## Architecture

```
Claim text ──► RunAsync()
                    │
          ┌─────────▼───────────┐
          │   AuditMiddleware   │  [AUDIT PRE]
          │                     │
          │  ┌──────────────────┴──┐
          │  │ ValueGuardrailMW    │  value > $10k → early-exit
          │  │                     │
          │  │   ┌─────────────┐   │
          │  │   │     LLM     │   │  decides to call a tool
          │  │   └──────┬──────┘   │
          │  │          │          │
          │  │   ┌──────▼──────┐   │
          │  │   │ ApprovalGate│   │  ← fires per tool call
          │  │   │  ApproveClaim?  │  Y → next() → tool executes
          │  │   │             │   │  N → return "REJECTED"
          │  │   └─────────────┘   │
          │  └──────────────────┬──┘
          │                     │  [AUDIT POST]
          └─────────────────────┘
```

Two middleware layers intercept at different depths in the same pipeline. Agent Run Middleware wraps the entire `RunAsync` call. Function Calling Middleware wraps individual tool executions inside the agent's tool-calling loop.

## Key Points

- **Signatures differ:** Agent Run Middleware returns `Task<AgentResponse>`; Function Invocation Middleware returns `ValueTask<object?>` (the tool result).
- **Depth differs:** Agent Run Middleware fires once per `RunAsync` call. Function Invocation Middleware fires once per tool invocation — a single `RunAsync` with two tool calls fires it twice.
- **`context.Function.Name`** identifies the tool. Check it to target a specific tool and call `next()` immediately for all others.
- **Returning a value from middleware** substitutes it as the tool result — the LLM sees it as if the real function returned it. `next()` invokes the real function.
- **Small models re-issue rejected tool calls.** `gpt-4.1-nano` does not reliably track "I already called this tool" within a turn — the same weak instruction-following that causes `LookupShipment` to fire 3× in V1 also causes `ApproveClaim` to be retried after a rejection. The model re-enters its reasoning loop and resamples the same decision. Adding an explicit stop directive to the rejection string (e.g. `"Do not retry ApproveClaim. Inform the user the claim requires human escalation and stop."`) compensates for this by giving the model a clear branch to follow instead of looping.
- **Registration order for the same type:** within agent-run middlewares, first `.Use()` is outermost. `ApprovalGateMiddleware` is function-invocation, so it is routed to a different layer entirely.
- `context.Arguments` is a `IDictionary<string, object?>` keyed by parameter name — use it to read the decision and shipment ID before prompting the reviewer.

## What's Missing (Leads to V5)

There is no protection against oversized claim submissions. A 2,000-token claim is sent to the LLM without any check, incurring unexpected cost and potentially hitting context limits. V5 wraps the `IChatClient` — the transport layer beneath the agent — with `TokenBudgetMiddleware` that rejects oversized requests before they reach OpenAI.

## Running This Sample

```bash
cd samples/labs/freight-claims/V4_ApprovalGate
dotnet run
```

When prompted `Approve this decision? (Y/N):`, enter `Y` to let the decision through or `N` to reject it.
