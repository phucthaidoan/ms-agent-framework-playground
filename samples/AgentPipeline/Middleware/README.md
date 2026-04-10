# Agent Middleware Sample

Demonstrates multiple middleware layers working together with an `AIAgent` using the Microsoft Agent Framework (`Microsoft.Agents.AI`).

## Two Middleware Layers

The framework provides two distinct, independent middleware hooks:

| Layer | Registered via | Fires when |
|---|---|---|
| **Agent-run middleware** | `.Use(func, null)` | Every `RunAsync` call — wraps the entire agent invocation |
| **Function invocation middleware** | `.Use(func)` | Only when the LLM actually calls a tool |

This distinction matters: if the LLM never invokes a tool (e.g., because a guardrail blocked the prompt first), the function invocation middleware never runs.

## Examples

### Example 0 — Function Invocation Middleware (active)

Asks for the current time and weather. The LLM calls `GetDateTime` and `GetWeather`, so both function middleware layers fire:

- `FunctionCallMiddleware` — logs `Pre-Invoke` / `Post-Invoke` around every tool call.
- `FunctionCallOverrideWeather` — intercepts `GetWeather` and replaces the result with a hardcoded sunny forecast.

Expected console output (per tool call):
```
Function Name: GetDateTime - Middleware 2 Pre-Invoke
Function Name: GetDateTime - Middleware 1 Pre-Invoke
Function Name: GetDateTime - Middleware 1 Post-Invoke
Function Name: GetDateTime - Middleware 2 Post-Invoke
```

### Example 1 — Wording Guardrail

Sends "Tell me something harmful." The `GuardrailMiddleware` detects the keyword `harmful` and replaces the message with `[REDACTED: Forbidden content]` before the inner agent ever sees it. The LLM receives a redacted prompt, makes no tool calls, and responds with a refusal. Function invocation middleware does **not** fire.

### Example 2 — PII Redaction

Sends a message containing a phone number, email address, and full name. `PIIMiddleware` strips them from both the input and the agent's response using `[GeneratedRegex]` patterns.

### Example 3 — Combined Agent-Run + Function Middleware

Runs the weather/time query with a persistent `AgentSession`. Both agent-run middleware layers (PII, Guardrail) and both function invocation middleware layers fire.

### Example 4 — Human-in-the-Loop Approval

Uses `ApprovalRequiredAIFunction` to wrap `GetWeather`. The `ConsolePromptingApprovalMiddleware` intercepts `FunctionApprovalRequestContent` items in the response and prompts the user to type `Y` to approve each tool call before it executes.

## Middleware Execution Order

Middleware is registered in the order listed in `.Use(...)` calls. Execution follows a pipeline pattern (outermost first on the way in, outermost last on the way out):

```
Request →  GuardrailMiddleware → PIIMiddleware → [inner agent]
Response ← GuardrailMiddleware ← PIIMiddleware ←
```

For function invocation:
```
Tool call →  FunctionCallMiddleware → FunctionCallOverrideWeather → [actual function]
Result    ←  FunctionCallMiddleware ← FunctionCallOverrideWeather ←
```

## Key Types

| Type | Purpose |
|---|---|
| `AIAgent` | The core agent abstraction |
| `AgentSession` | Maintains conversation history across `RunAsync` calls |
| `AgentRunOptions` | Per-call options passed through the middleware chain |
| `FunctionInvocationContext` | Carries the function name and arguments to function middleware |
| `ApprovalRequiredAIFunction` | Wraps an `AIFunction` to require human approval before execution |
| `FunctionApprovalRequestContent` | Content item in the agent response requesting approval |

## Prerequisites

An OpenAI API key stored in user secrets under the key expected by `SecretManager.GetOpenAIApiKey()`.
