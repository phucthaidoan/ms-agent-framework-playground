# V5 — IChatClient Middleware (Token Budget Enforcer)

## What You'll Learn

How IChatClient Middleware intercepts at the transport layer — the innermost pipeline layer, closest to the wire — where it sees the complete message list that will actually be sent to OpenAI, including the system prompt and tool definitions that Agent Run Middleware never sees.

## Key Concept

`IChatClient.AsBuilder().Use(getResponseFunc: ...)` wraps the OpenAI client before the `AIAgent` is built from it. The middleware receives the full `IEnumerable<ChatMessage>` going to the API and can inspect, modify, or block it:

```csharp
private static async Task<ChatResponse> TokenBudgetMiddleware(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IChatClient innerClient,
    CancellationToken cancellationToken)
{
    IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();
    int estimatedTokens = messageList.Sum(m => m.Text?.Length ?? 0) / 4;  // chars ÷ 4 heuristic

    if (estimatedTokens > 800)
    {
        Output.Red($"[TOKEN BUDGET] ~{estimatedTokens} tokens exceeds budget. Rejecting.");
        return new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, "Claim rejected: submission too long.")
        ]);
    }

    return await innerClient.GetResponseAsync(messageList, options, cancellationToken);
}

// Construction order — IChatClient must be wrapped BEFORE building the AIAgent
IChatClient chatClientWithBudget = new OpenAIClient(apiKey)
    .GetChatClient("gpt-4.1-nano")
    .AsIChatClient()
    .AsBuilder()
    .Use(getResponseFunc: TokenBudgetMiddleware, getStreamingResponseFunc: null)
    .Build();

AIAgent baseAgent = chatClientWithBudget.AsAIAgent(instructions, name, tools);

// Agent-level middleware still wraps the outside
AIAgent agent = baseAgent
    .AsBuilder()
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
          │   AuditMiddleware   │  (agent run)
          │  ┌──────────────────┴──┐
          │  │ ValueGuardrailMW    │  (agent run)
          │  │  ┌──────────────────┴──┐
          │  │  │  ClaimsTriageAgent  │
          │  │  │  (LLM decides tool) │
          │  │  │  ┌────────────────┐ │
          │  │  │  │ ApprovalGateMW │ │  (function invocation)
          │  │  │  └────────────────┘ │
          │  │  │       ↓             │
          │  │  │  ┌────────────────┐ │
          │  │  │  │ TokenBudgetMW  │ │  ← innermost: sees full message list
          │  │  │  │  > 800 tokens? │ │     system prompt + tool schemas
          │  │  │  │  Yes → reject  │ │     + conversation history
          │  │  │  │  No  → OpenAI ─┼─┼─► wire
          │  │  │  └────────────────┘ │
          │  │  └─────────────────────┘
          │  └─────────────────────────
          └───────────────────────────
```

## Key Points

- **What IChatClient middleware sees that Agent Run Middleware does not:** the system prompt injected by the agent, all tool definitions serialized as JSON schema, and the full conversation history. Agent Run Middleware only receives the messages passed by the caller; the framework adds the rest below it.
- **Construction order is load-bearing:** `AsIChatClient().AsBuilder().Use(...).Build()` must complete before `.AsAIAgent(...)` is called. The agent is built *on top of* the already-wrapped chat client; swapping the order silently builds a different pipeline.
- **`getResponseFunc:` is a named argument** to distinguish this overload from the single-delegate `.Use(sharedFunc)` overload, which handles both response and streaming and has a different delegate shape.
- **Token heuristic (`chars / 4`)** counts all message text including tool call messages and tool results accumulated during the turn. It is intentionally rough — the goal is a hard ceiling, not a precise count.
- **Scenario C** (the long claim, ~4,000 chars) exercises the token budget path. The rejection fires at the IChatClient layer on the first LLM call of the turn — before OpenAI is contacted.

## What's Complete

All five pipeline layers are now active. The full execution path for a normal claim is:

```
AuditMiddleware (pre)
  → ValueGuardrailMiddleware (passes through)
    → ClaimsTriageAgent sends messages to LLM
      → LLM decides to call LookupShipment
        → ApprovalGateMiddleware (passes LookupShipment through)
          → TokenBudgetMiddleware (within budget → forwards to OpenAI)
      → LLM decides to call ApproveClaim
        → ApprovalGateMiddleware (prompts reviewer)
          → TokenBudgetMiddleware (within budget → forwards to OpenAI)
AuditMiddleware (post)
```

## Running This Sample

```bash
cd samples/labs/freight-claims/V5_TokenBudget
dotnet run
```

Scenario C (the oversized claim) runs without any input and shows the token budget rejection. Scenario D prompts for `Y/N` reviewer approval.
