# Freight Claims Triage — Problem Statement

## Background

A logistics company receives 200–400 freight damage claims per day via email. Each claim contains:
- A damage description
- Shipment ID
- Cargo type (electronics, perishables, chemicals, general)
- Declared value (USD)

Currently 5 human agents spend 8–15 minutes per claim manually:
1. Looking up the shipment in an internal system
2. Classifying the damage type (weather, handling, packaging, carrier liability)
3. Deciding: auto-approve / escalate to senior adjuster / reject
4. Drafting a response to the claimant

**Goal:** Automate to <30 seconds, with a human reviewer gate for high-value claims (>$10,000), and a token
budget gate to protect against oversized claim submissions.

---

## What Goes Wrong Without Middleware

| Scenario | What fails | Which concept prevents it |
|----------|-----------|--------------------------|
| No middleware: $15k claim auto-approved | Loss for company, compliance breach | V3 ValueGuardrailMiddleware |
| No audit middleware: claim disputed later | No trail — company cannot prove what decision was made | V2 AuditMiddleware |
| No function tools: agent guesses cargo type | Misclassification — wrong payout decision | V1 FunctionTools |
| No function calling middleware: ApproveClaim fires without consent | Rejected claim approved in system without reviewer | V4 ApprovalGateMiddleware |
| No IChatClient middleware: 2000-token claim sent to LLM | Unexpected token cost + possible truncation errors | V5 TokenBudgetMiddleware |

---

## Lab Versions

| Version | New Concept | What It Adds |
|---------|-------------|-------------|
| V1_FunctionTools | Function Tools | Agent looks up real shipment data; classifies damage; records approval |
| V2_AuditMiddleware | Agent Pipeline + Agent Run Middleware | Timestamped audit log wraps every claim run |
| V3_ValueGuardrail | Stacked Middleware + Early-Exit | High-value claims intercepted before LLM is called |
| V4_ApprovalGate | Function Calling Middleware | Human-in-the-loop gate at tool invocation level |
| V5_TokenBudget | IChatClient Middleware | Token budget enforcer at the innermost pipeline layer |

---

## Deep Dive Questions

**Q1: Why does middleware registration order matter? What if GuardrailMiddleware is registered before AuditMiddleware?**

Middleware wraps like Russian dolls — the first `.Use()` is the outermost layer. If guardrail is outermost,
it short-circuits before audit can fire on its post-run leg, meaning escalated claims have no audit log.
Audit must be outermost to guarantee it always captures both the pre-run and post-run sides.

**Q2: What is the difference between Agent Run Middleware and Function Calling Middleware?**

Agent Run Middleware intercepts `RunAsync` — it sees the full input/output message list and can block or
replace an entire agent turn. Function Calling Middleware intercepts individual tool invocations mid-turn
— it fires inside the agent's tool-calling loop, after the LLM has already decided to call a tool. They
operate at different pipeline depths and serve different purposes.

**Q3: IChatClient middleware sits at the innermost layer. What does it see that Agent Run Middleware cannot?**

It sees the *raw* `IList<ChatMessage>` that will actually be sent to OpenAI — including the system prompt
injected by the agent, all tool definitions serialized as JSON schema, and the full conversation history.
Agent Run Middleware sees only the user-supplied messages; the system prompt and tool list are added later
by the context layer, below the agent middleware layer.

**Q4: Can you add function calling middleware that only intercepts one specific tool and ignores others?**

Yes — check `context.Function.Name` inside the middleware and call `next()` immediately if it doesn't
match. The V4 `ApprovalGateMiddleware` does exactly this: it only pauses for `ApproveClaim` and passes
all other function calls (like `LookupShipment`) through without interruption.

**Q5: If ValueGuardrailMiddleware short-circuits and never calls innerAgent.RunAsync, does AuditMiddleware still log the post-run entry?**

Yes — because AuditMiddleware is the outer wrapper. Its post-run code runs after ValueGuardrailMiddleware
*returns* (whether that return came from calling innerAgent or from early-exit). The audit log will show
`[AUDIT POST]` with the escalation response, proving the guardrail fired.
