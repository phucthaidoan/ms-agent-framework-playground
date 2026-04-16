# V2 — Agent Pipeline + Audit Middleware

## What You'll Learn

How Agent Run Middleware wraps every `RunAsync` call so cross-cutting concerns — like audit logging — execute before and after the entire agent turn without touching business logic.

## Key Concept

`AIAgent.AsBuilder().Use()` inserts an Agent Run Middleware into the pipeline. The middleware receives the same arguments as `RunAsync`, calls `innerAgent.RunAsync` to forward to the next layer, and can inspect or transform the request and response on both sides:

```csharp
private static async Task<AgentResponse> AuditMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    Output.Gray($"[AUDIT PRE]  {DateTimeOffset.Now:HH:mm:ss} — claim received");

    AgentResponse response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

    Output.Gray($"[AUDIT POST] {DateTimeOffset.Now:HH:mm:ss} — decision recorded: {response.Text}");
    return response;
}

// Registration
AIAgent agent = baseAgent
    .AsBuilder()
    .Use(AuditMiddleware, null)
    .Build();
```

The second `null` argument is the streaming counterpart — pass a streaming implementation or `null` to fall back to the non-streaming path.

## Architecture

```
Claim text ──► RunAsync()
                    │
          ┌─────────▼───────────┐
          │   AuditMiddleware   │  ← [AUDIT PRE]  fires here
          │                     │
          │   ┌─────────────┐   │
          │   │  LLM + Tools│   │  tool calls happen inside RunAsync
          │   └─────────────┘   │
          │                     │  ← [AUDIT POST] fires here (after ALL tool calls)
          └─────────────────────┘
```

## Key Points

- The Agent Run Middleware signature is `(IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent, CancellationToken) → Task<AgentResponse>`. The `AgentRunOptions?` carries per-request settings; `null` is the common case.
- `[AUDIT POST]` fires after the **entire** turn — including every tool invocation inside the turn. It does not fire between tool calls.
- The first `.Use()` call produces the **outermost** wrapper. This means code before `innerAgent.RunAsync` is the first to run and code after is the last to run. Order matters when stacking multiple middlewares.
- `.Use(AuditMiddleware, null)` — the two-argument overload is the Agent Run Middleware overload. The single-argument overload is for Function Invocation Middleware and has a different delegate signature.
- High-value claims still reach the LLM in V2 — the middleware only observes, it does not block.

## What's Missing (Leads to V3)

Every claim, regardless of declared value, is forwarded to the LLM. A $15,000 claim is auto-approved with no additional safeguard. V3 inserts `ValueGuardrailMiddleware` between `AuditMiddleware` and the agent — it short-circuits the pipeline before the LLM is called when the declared value exceeds $10,000.

## Running This Sample

```bash
cd samples/labs/freight-claims/V2_AuditMiddleware
dotnet run
```
