# V6 — Stacked IChatClient Middleware (Rate Limiter + Token Budget)

## What You'll Learn

How to stack multiple `IChatClient` middleware layers and why construction order determines pipeline order. V6 adds a `RateLimitingChatClient` as the outer IChatClient layer (fires first on every LLM call) wrapping the V5 token budget as the inner layer (closest to OpenAI). You'll also learn the `DelegatingChatClient` class pattern — the right approach when middleware is stateful and owns resources that need disposal.

## Key Concept

Two `IChatClient` middlewares are composed. Construction order is what determines which fires first:

```csharp
// Step 1 — Rate limiter owns a RateLimiter instance → DelegatingChatClient (class pattern)
private sealed class RateLimitingChatClient(IChatClient innerClient, RateLimiter rateLimiter)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using RateLimitLease lease = await rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);

        if (!lease.IsAcquired)
            throw new InvalidOperationException("Rate limit exceeded: retry after the window resets.");

        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) rateLimiter.Dispose();
        base.Dispose(disposing);
    }
}

// Step 2 — Wrap raw IChatClient with rate limiter (outer IChatClient layer, fires first)
IChatClient rateLimitedClient = new RateLimitingChatClient(
    new OpenAIClient(apiKey).GetChatClient("gpt-4.1-nano").AsIChatClient(),
    new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        Window = TimeSpan.FromSeconds(10),
        PermitLimit = 2,      // 2 LLM calls per window
        QueueLimit  = 0,      // fail fast — no queuing
        AutoReplenishment = true
    }));

// Step 3 — Wrap rate-limited client with token budget (inner IChatClient layer, closest to OpenAI)
// ChatClientBuilder.Build() applies factories in reverse — first .Use() is outermost → TokenBudget fires FIRST
IChatClient budgetedClient = rateLimitedClient
    .AsBuilder()
    .Use(getResponseFunc: TokenBudgetMiddleware, getStreamingResponseFunc: null)
    .Build();

// Step 4 — Build AIAgent from double-wrapped IChatClient
AIAgent baseAgent = budgetedClient.AsAIAgent(instructions, "ClaimsTriageAgent", tools);

// Step 5 — Wrap AIAgent with agent-level middleware (unchanged from V5)
AIAgent agent = baseAgent.AsBuilder()
    .Use(AuditMiddleware, null)
    .Use(ValueGuardrailMiddleware, null)
    .Use(ApprovalGateMiddleware)
    .Build();
```

## Architecture

```
Claim text ──► RunAsync()
                    │
          ┌─────────▼───────────┐
          │   AuditMiddleware   │  (agent run) — logs PRE/POST/ERR
          │  ┌──────────────────┴──┐
          │  │ ValueGuardrailMW    │  (agent run) — high-value → early exit
          │  │  ┌──────────────────┴──┐
          │  │  │  ClaimsTriageAgent  │
          │  │  │  ┌────────────────┐ │
          │  │  │  │ ApprovalGateMW │ │  (function invocation)
          │  │  │  └────────────────┘ │
          │  │  │          ↓          │
          │  │  │  ┌────────────────┐ │
          │  │  │  │ TokenBudgetMW  │ │  ← inner IChatClient layer
          │  │  │  │  > 400 tokens? │ │     fires FIRST on every LLM call
          │  │  │  │  Yes → reject  │ │     no permit consumed on rejection
          │  │  │  └───────┬────────┘ │
          │  │  │  ┌───────▼────────┐ │
          │  │  │  │ RateLimiterMW  │ │  ← outer IChatClient layer (NEW)
          │  │  │  │ permit avail?  │ │     DelegatingChatClient pattern
          │  │  │  │ No  → throw    │ │     stateful: holds RateLimiter
          │  │  │  │ Yes → OpenAI ──┼─┼─► wire
          │  │  │  └────────────────┘ │
          │  │  └─────────────────────┘
          │  └─────────────────────────
          └───────────────────────────
```

## How the Rate Limiter Works

### The Window

```
t=0s ──────────────────────── t=10s ──────────────────────── t=20s
│         Window 1            │         Window 2            │
│  2 permits available        │  2 permits refilled         │
└─────────────────────────────┴─────────────────────────────┘
```

- The window resets every 10 seconds on a **clock boundary** — not from the time of the first request
- At each reset, **2 permits are refilled** to the full `PermitLimit` — unused permits do NOT carry over
- `QueueLimit = 0` — if no permits are available, the request is **immediately rejected** with no waiting

### Permits Are Per-LLM-Call, Not Per Agent Turn

The rate limiter sits at the `IChatClient` layer, so it fires on every `GetResponseAsync` call — not once per `agent.RunAsync()`. One successful agent turn makes **2 LLM round-trips**:

1. LLM decides to call `LookupShipment` → first `GetResponseAsync` → **1 permit consumed**
2. LLM decides to call `ApproveClaim` → second `GetResponseAsync` → **1 permit consumed**

With `PermitLimit = 2`, a single successful turn (Scenario A) exhausts the entire window, making every subsequent request fail until the window resets.

### What Consumes a Permit vs. What Doesn't

| Event | Permit consumed? | Why |
|-------|-----------------|-----|
| Guardrail fires (Scenario D) | **No** | Agent-run layer — `IChatClient` never reached |
| Token budget rejects (Scenario E) | **No** | Token budget fires first; rate limiter not reached |
| Normal LLM call (Scenario A) | **Yes × 2** | Two round-trips per turn, each hits `RateLimitingChatClient` |
| Rate limit rejected (Scenarios B, C) | **No** | Lease not acquired — OpenAI not called |

### `FixedWindow` vs. `SlidingWindow`

With `FixedWindowRateLimiter`, the 2 permits could be consumed at `t=9.9s`, then 2 more at `t=10.1s` — a burst of 4 calls in 0.2 seconds. A `SlidingWindowRateLimiter` divides the window into segments and spreads replenishment over time, preventing that burst at the cost of slightly more complex configuration.

---

## Scenario Walkthrough

The sample runs 5 scenarios in a specific order that tells a clear story. The rate limiter is configured to **2 permits per 10-second window**. Each successful agent turn consumes **2 permits** (one LLM call to decide to invoke `LookupShipment`, one to decide to invoke `ApproveClaim`). This means a single successful turn exhausts the window.

### Scenario D — High-value claim ($15,000 electronics)

The `ValueGuardrailMiddleware` fires at the **agent-run layer** and short-circuits before the `AIAgent` ever calls the `IChatClient`. The rate limiter is never reached — **no permit is consumed**.

```
[AgentMW][AUDIT PRE]  ...
[AgentMW][GUARDRAIL]  $15,000 exceeds $10,000 threshold. Escalating — IChatClient never reached.
[AgentMW][AUDIT POST] ...
```

> **Key point:** Agent-run middleware fires above the IChatClient layer. Even the outermost IChatClient middleware (token budget) is not reached.

### Scenario A — Normal claim ($450 general goods) — window exhausted

All layers pass. The agent calls the LLM twice: once to decide to invoke `LookupShipment`, once to decide to invoke `ApproveClaim`. Each LLM call hits `TokenBudgetMiddleware` (passes) then `RateLimitingChatClient` (acquires a permit). **Both permits are consumed.**

```
[AgentMW][AUDIT PRE]  ...
[ChatMW] [TOKEN BUDGET] ~25 tokens — within budget. Forwarding to rate limiter.
[ChatMW] [RATE LIMITER] Permit acquired. Forwarding to OpenAI.        ← permit 1 of 2
  [TOOL] LookupShipment(SHP-1001) → ...
[ChatMW] [TOKEN BUDGET] ~60 tokens — within budget. Forwarding to rate limiter.
[ChatMW] [RATE LIMITER] Permit acquired. Forwarding to OpenAI.        ← permit 2 of 2
[FuncMW] [APPROVAL GATE] Agent wants to APPROVE claim SHP-1001 ...
  Proceed with this decision? (Y/N): Y
  DECISION [SHP-1001]: APPROVED — ...
[AgentMW][AUDIT POST] ...
```

> **Key point:** One agent turn = multiple LLM round-trips = multiple permit acquisitions. The 10-second window is now exhausted.

### Scenario E — Oversized claim (~512 tokens)

The token budget check fires and rejects the request. The `RateLimitingChatClient` is **never reached** — no permit is consumed, even though the window is exhausted.

The budget estimates tokens by summing `m.Text` across all messages and dividing by 4. System prompt and tool schemas arrive as structured content with `null` `.Text`, so only the user message text is counted. ScenarioE's user message is ~2050 chars → ~512 estimated tokens, which exceeds the 400-token budget.

```
[AgentMW][AUDIT PRE]  ...
[ChatMW] [TOKEN BUDGET] Estimated 512 tokens exceeds budget of 400. Rejecting — rate limiter not reached (no permit consumed).
[AgentMW][AUDIT POST] Claim rejected: submission too long ...
```

> **Key point:** The outer IChatClient layer (token budget) acts as a pre-filter. Cheap checks sit outside expensive ones — the rate limit quota is not wasted on requests that would have been rejected anyway.

### Scenario B — Normal claim ($2,500 perishables) — rate limit fires

The claim is within the token budget, but the 10-second window is exhausted from Scenario A. `RateLimitingChatClient` cannot acquire a permit — it throws `InvalidOperationException`, which propagates up through `AuditMiddleware`.

```
[AgentMW][AUDIT PRE]  ...
[ChatMW] [TOKEN BUDGET] ~25 tokens — within budget. Forwarding to rate limiter.
[ChatMW] [RATE LIMITER] Permit denied — 2 permits/10s window exhausted. OpenAI NOT called.
[AgentMW][AUDIT ERR]  Rate limit exceeded: more than 2 LLM calls within the 10-second window. Retry after the window resets.
Rate limit exception: Rate limit exceeded: ...
```

> **Key point:** The rate limiter throws — it doesn't return a polite `ChatResponse`. This is intentional: a rate limit violation is an **infrastructure failure**, not a business decision. The exception propagates and is caught by the caller's `try/catch`.

### Scenario C — Normal claim ($3,800 industrial) — rate limit fires again

Same result as Scenario B — the window has not yet reset. This confirms that window replenishment is time-based, not request-based.

## DelegatingChatClient vs. Inline Lambda

| | Inline lambda (`AsBuilder().Use(func)`) | `DelegatingChatClient` (class) |
|---|---|---|
| **State** | Stateless — no fields | Stateful — holds `RateLimiter` |
| **Disposal** | Not needed | Override `Dispose(bool)` to clean up |
| **Best for** | One-off, ephemeral checks (token count) | Middleware owning timers, semaphores, counters |
| **Example in this lab** | `TokenBudgetMiddleware` (V5) | `RateLimitingChatClient` (V6) |

## Key Points

- **Construction order = pipeline order.** `ChatClientBuilder.Build()` applies factories in reverse — the first `.Use()` call becomes outermost. `rateLimitedClient.AsBuilder().Use(TokenBudgetMiddleware)` makes `TokenBudgetMiddleware` the outer wrapper (fires first), while `RateLimitingChatClient` (passed as the inner client to the builder) fires second, just before OpenAI.
- **One agent turn = multiple LLM calls.** Each tool-calling round-trip hits every `IChatClient` middleware independently. A turn with `LookupShipment` + `ApproveClaim` consumes 2 permits.
- **Position cheap checks outside expensive ones.** The token budget (character counting) sits outside the rate limiter (permit acquisition). Oversized requests are rejected before consuming quota.
- **Rate limit errors are infrastructure failures.** `RateLimitingChatClient` throws rather than returning a polite response — callers must handle `InvalidOperationException`. Updated `AuditMiddleware` catches and logs exceptions before re-throwing, so the audit trail is never silently lost.
- **Token estimate counts only `m.Text`.** System prompt and tool schemas arrive as structured content with `null` `.Text` — they are not counted. The 400-token budget is calibrated against user message text only: normal 4-line claims (~37 tokens) pass; ScenarioE's long description (~512 tokens) is blocked.
- **`QueueLimit = 0` means fail fast.** Requests that cannot acquire a permit are immediately rejected. Setting `QueueLimit > 0` would queue excess requests and wait — appropriate for throughput-sensitive workloads, not real-time claim processing.

## Running This Sample

```bash
cd samples/labs/freight-claims/V6_RateLimiter
dotnet run
```

**During Scenario A**, the approval gate will prompt `Y/N` — enter `Y` to allow `ApproveClaim` to fire and exhaust both rate-limit permits, which makes Scenarios B and C fail cleanly.

To see a scenario where the rate limit **does not** fire (window resets): wait 10 seconds after Scenario A completes before running Scenarios B and C. You can do this by adding `await Task.Delay(TimeSpan.FromSeconds(11));` in `RunSample()` between Scenario E and Scenario B.
