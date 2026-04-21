# HR Document Review Assistant — Microsoft Agent Framework Lab

## Problem Statement

A 120-person law firm's HR department uses an LLM-based agent to help paralegal staff review employee documents: onboarding forms, disciplinary records, employment contracts, and compensation summaries. Paralegals paste raw document text into the agent and ask questions — "Does this contract include a non-compete clause?", "What is the probationary period?", "Flag any policy violations in this disciplinary note."

The problem: these documents contain personally identifiable information (PII). SSNs, dates of birth, bank account numbers, personal email addresses, and phone numbers appear inline in the pasted text. Without interception, this raw PII flows directly to the OpenAI API — a violation of the firm's data-handling policy, their client data agreement, and potentially GDPR Article 25. The agent also occasionally echoes PII back in its responses ("The employee's SSN is 123-45-6789"), compounding the exposure. No audit trail exists of what information was sent to the model.

The firm needs the agent to redact PII from the message stream *before* it reaches the model, detect any PII that leaks into the model's response, and maintain a per-session compliance audit log. These are not optional enhancements — they are data governance requirements. A stateless agent with no interceptor fails all three checks on the first document paste.

---

## Hardcoded Document Fixtures (no cloud infra required)

```
DOC-001 (Employment Contract — Alice Chen)
  "...employee SSN: 423-11-9988, DOB: 1989-03-14, bank routing: 021000021,
   account: 8834421190, personal email: alice.chen@gmail.com..."

DOC-002 (Disciplinary Record — Bob Marsh)
  "...incident reported by manager on 2024-11-12. Employee phone: 415-555-0192.
   No SSN on file. Verbal warning issued..."

DOC-003 (Compensation Summary — Carol Ray)
  "...annual base $124,000. SSN: 501-88-3341. Bank: Chase, routing 021000021,
   account: 7712009834. Next review: 2025-06-01..."

DOC-004 (Clean Policy Document — no PII)
  "...Section 3: Remote Work Policy. Employees must maintain VPN connection
   when accessing firm systems outside the office. See IT policy IT-2024-07..."
```

Three documents contain PII across multiple types (SSN, phone, email, bank account). One is clean. This ensures every test scenario can exercise the redaction logic and confirm clean documents pass through unmodified.

---

## Concept Mapping

| # | Concept | Official Doc | Where it fits | Why needed | What breaks without it |
|---|---------|-------------|--------------|------------|------------------------|
| 1 | `ProvideAIContextAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `HRPolicyContextProvider` — injects firm data-handling policy rules as instructions before each LLM call | LLM must know the redaction policy to follow injected redaction notices; also tells it never to echo bracketed placeholders verbatim | Agent ignores the `[REDACTED: SSN]` placeholder and tries to "helpfully" expand it — guessing or fabricating the original value |
| 2 | `StoreAIContextAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `HRPolicyContextProvider` — appends each turn's redaction summary to a session audit log after the LLM responds | Compliance team needs a reconstruction of what was redacted in each session without storing the actual PII | No audit trail — firm cannot prove compliance during a data-handling review |
| 3 | `ProviderSessionState<T>` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Inside both providers — typed per-session `RedactionLog` and `ComplianceViolationRecord` | Isolates each paralegal's session; provider instance is shared across all sessions | Two paralegals working simultaneously share one redaction log — paralegal B's audit report contains paralegal A's document SSNs |
| 4 | `InvokingCoreAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `PIIRedactionContextProvider` — intercepts the assembled message list, scans for PII patterns, rewrites messages in-place before the LLM call | `ProvideAIContextAsync` cannot see the assembled message list — only `InvokingCoreAsync` has access to `InvokingContext.Messages` | Raw SSN `423-11-9988` is sent to OpenAI API — data leak, policy violation, GDPR exposure |
| 5 | `InvokedCoreAsync` | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | `PIIRedactionContextProvider` — scans the LLM's response messages for PII that the model echoed back despite redaction | Response compliance scanning is only possible in `InvokedCoreAsync`, which receives both `RequestMessages` and `ResponseMessages` | LLM echoes "The employee SSN is 423-11-9988" in its summary — violation goes undetected, logged as clean |
| 6 | Message source stamping (`WithAgentRequestMessageSource` / `GetAgentRequestMessageSourceType`) | [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp) | Inside `InvokingCoreAsync` — injected redaction notices are stamped so the next turn's scan skips them | Without stamping, `[REDACTED: SSN]` in a prior injected message is re-scanned in turn 2 and triggers a phantom redaction event | Turn 2 flags `[REDACTED: SSN]` as a new SSN pattern — the audit log shows a phantom violation for a document that was already clean |

---

## Additional Concepts (mechanically required)

| # | Concept | Why added | What it enables |
|---|---------|-----------|----------------|
| 1 | Function Tools (`AIFunctionFactory.Create`) | V1 baseline requires tool calls for document parsing and policy lookup | Agent can query the hardcoded document store and retrieve firm policy text without hallucinating content |
| 2 | Context Compaction (`CompactionProvider`) | A real HR session involves reviewing 5–10 documents sequentially; each document paste is a large message — sessions exceed context windows without compaction | Bounds the LLM-visible token count; compliance audit log survives compaction because it lives in `ProviderSessionState<T>` |

---

## Failure Scenarios

| Scenario | What fails | Which concept prevents it |
|----------|-----------|--------------------------|
| Paralegal pastes DOC-001 containing SSN `423-11-9988` | Raw SSN sent to OpenAI API — data leak | V3: `InvokingCoreAsync` scans and redacts before LLM call |
| LLM responds "The employee SSN is 423-11-9988" | Response PII exposure goes undetected | V3: `InvokedCoreAsync` scans response and records compliance violation |
| Turn 2: provider re-scans its own injected `[REDACTED: SSN]` notice from turn 1 | Phantom redaction event — clean document flagged as new violation | V3: `GetAgentRequestMessageSourceType()` skips messages stamped as `AIContextProvider` |
| Provider stores redaction log in an instance field instead of `ProviderSessionState<T>` | Two parallel paralegal sessions share one log — session A's SSN appears in session B's audit report | V2: `ProviderSessionState<RedactionLog>` isolates state per `AgentSession` |
| No `ProvideAIContextAsync`: LLM has no policy instructions | Agent interprets `[REDACTED: SSN]` as a tag to "fill in" — echoes fabricated SSN values | V2: `ProvideAIContextAsync` injects "treat REDACTED placeholders as final — never expand or infer original values" |
| No audit logging (`StoreAIContextAsync` absent) | Compliance team has no record of what was reviewed; data subject access request cannot be answered | V2: `StoreAIContextAsync` appends per-turn redaction summary to session audit log |
| 12-document review session: each paste is 500+ tokens | Context window exceeded — `RunAsync` truncates or throws; early documents no longer visible | V4: `CompactionProvider` bounds LLM-visible history; session audit log lives in `ProviderSessionState<T>` and survives |

---

## Deep Dive Q&A

**Q1: `ProvideAIContextAsync` and `InvokingCoreAsync` both run before the LLM call. In this lab they are on DIFFERENT providers. What order do they fire, and why does that order matter for redaction?**

Answer: When multiple providers are registered in `AIContextProviders = [providerA, providerB]`, the framework calls each provider's pre-call hook in registration order. Within each provider, `InvokingCoreAsync` is the outermost hook — it fires before `ProvideAIContextAsync` (unless you override `InvokingCoreAsync` and explicitly call `base.InvokingCoreAsync()` to trigger `ProvideAIContextAsync` at a controlled point). In this lab, `PIIRedactionContextProvider` must be listed FIRST in `AIContextProviders` so its `InvokingCoreAsync` redacts the document text before `HRPolicyContextProvider`'s `ProvideAIContextAsync` adds the policy instructions. If reversed, the policy instructions are injected first, and the redaction scan then also runs over the policy text — harmless in this case, but the ordering principle matters: interceptors (that modify messages) must run before injectors (that add messages), because injectors may themselves contain tokens that look like PII patterns.

**Q2: `InvokingCoreAsync` receives `InvokingContext.Messages`. When you redact a message in-place (modify the `Content` of a `ChatMessage`), does this modify what goes into the LLM call, or is a copy made? What happens to the session history?**

Answer: The messages in `InvokingContext.Messages` are the live list that the framework will pass to the LLM. Modifying them in-place — replacing `"SSN: 423-11-9988"` with `"SSN: [REDACTED: SSN]"` — modifies what the LLM sees in this call. However, this does NOT modify the underlying `AgentSession` chat history. The session history is maintained separately and is not affected by in-place edits in `InvokingContext.Messages`. This means: in turn 2, the session history still contains the original unredacted text, and `InvokingCoreAsync` must redact it again. The redaction must happen every turn, not just once. This is a critical insight: `InvokingCoreAsync` is a per-call interceptor, not a one-time rewriter. If you want the session to never store raw PII, you must redact at ingestion time (in the application layer, before calling `RunAsync`) — the provider redacts what reaches the LLM, but it cannot retroactively clean session history.

**Q3: `InvokedCoreAsync` receives both `InvokedContext.RequestMessages` and `InvokedContext.ResponseMessages`. Why do you need `RequestMessages` in the post-call hook if you already processed them in `InvokingCoreAsync`?**

Answer: Two reasons. First, `RequestMessages` in `InvokedCoreAsync` are the messages that were actually sent to the LLM — including any modifications made during `InvokingCoreAsync`. They reflect the redacted state, not the original. Comparing them to the original (stored in `ProviderSessionState<T>`) lets you verify that redaction was applied correctly — useful for the audit log: "DOC-001: 2 SSNs redacted, 1 email redacted — request verified clean." Second, `ResponseMessages` contain the LLM's output. The compliance scan in `InvokedCoreAsync` checks whether the model echoed any PII pattern from the original document despite redaction — which can happen if the model's training data primed it to "complete" partial patterns. Without `RequestMessages` as a reference point, you cannot easily tell whether a pattern in the response came from the request or was hallucinated.

**Q4: Why does message source stamping prevent the phantom-redaction bug specifically, and what is the exact sequence of events that causes it without stamping?**

Answer: Without stamping, the sequence is: Turn 1 — paralegal pastes DOC-001 containing SSN `423-11-9988`. `InvokingCoreAsync` redacts it to `[REDACTED: SSN]` and injects a notice message: "Note: 1 SSN redacted in user message." This notice has no source stamp — it looks like a regular `User` or `System` message. Turn 2 — paralegal asks a follow-up question about DOC-001. `InvokingCoreAsync` receives the assembled message list, which now includes the prior turn's injected notice "Note: 1 SSN redacted in user message." The regex scanner matches the phrase "SSN" in that notice and classifies it as a new SSN pattern to redact. It replaces it with "[REDACTED: SSN]" and logs "1 SSN detected." The audit log now shows 2 SSN events for DOC-001 — one real, one phantom. With `WithAgentRequestMessageSource()` applied to the injected notice, `GetAgentRequestMessageSourceType()` returns `AIContextProvider` for that message on turn 2. The scanner's first step is: skip any message where source type is `AIContextProvider`. The phantom event never fires.

**Q5: The firm later asks: "Can we replay exactly what the LLM saw for any given session, without storing the raw PII?" Is this possible given how the redaction provider works?**

Answer: Yes — and this is precisely what `ProviderSessionState<RedactionLog>` enables. The audit log stored by `StoreAIContextAsync` records: for each turn, the redaction map (original pattern type → placeholder → position in message). It does not store the actual PII values. A session replay tool can reconstruct what the LLM saw by taking the original session history, applying the redaction map turn-by-turn, and producing the redacted view. Because the log lives in `AgentSession` via `ProviderSessionState<T>`, it can be serialized alongside the session using `agent.SerializeSessionAsync(session)` — the same mechanism used in the conversation-memory lab (V2_SessionSerialization). The compliance team gets a reproducible, PII-free reconstruction of the exact redacted messages the LLM processed, which is sufficient for a data subject access request audit.

**Q6: If `CompactionProvider` runs and truncates early-turn messages, does it truncate the redaction events stored in `ProviderSessionState<RedactionLog>` too? What is actually lost when messages are compacted?**

Answer: Compaction truncates the LLM-visible message list — the raw `ChatMessage` objects in the conversation history. `ProviderSessionState<RedactionLog>` is stored in `AgentSession`'s state bag, not in the message list. Compaction never touches it. What is actually lost: the LLM can no longer "see" the full text of early document pastes in its context window. What is preserved: the redaction audit log (every PII event from every turn), the compliance violation record (every response scan result), and any session-scoped counters. This distinction is the architectural payoff of the provider design: compliance data that must survive indefinitely goes in `ProviderSessionState<T>`; conversational content that only needs to be visible to the LLM for N recent turns goes in chat history and can be compacted. A 20-document review session compacts fine — the LLM's rolling context covers the last few documents, the audit log covers all 20.

---

## Lab Versions

| Version | Folder | New Concept | What it adds vs previous |
|---------|--------|-------------|--------------------------|
| V1 | `V1_AgentBaseline` | Function Tools (`AIFunctionFactory.Create`) | Runnable domain baseline: `FetchDocument`, `LookupPolicy`, `RecordReviewDecision` against hardcoded fixtures. No redaction — raw SSN from DOC-001 flows to LLM. This data leak is the explicit problem V3 solves. |
| V2 | `V2_SimpleContextProvider` | `ProvideAIContextAsync` + `StoreAIContextAsync` + `ProviderSessionState<T>` | Policy injection: LLM receives firm data-handling rules as instructions each turn. Session audit logging: redaction summaries appended after each response. Session isolation proof: two sessions on same provider have independent logs. |
| V3 | `V3_SafetyInterceptor` | `InvokingCoreAsync` + `InvokedCoreAsync` + `WithAgentRequestMessageSource` / `GetAgentRequestMessageSourceType` | Real-time PII redaction in assembled message list. Response compliance scan (detects LLM echo of PII). Feedback-loop prevention via source stamping (no phantom redaction of prior injected notices). |
| V4 | `V4_Compaction` | `CompactionProvider` + `AsBuilder().UseAIContextProviders()` | Bounded LLM context for 12-document review sessions. Audit log survives compaction because it lives in `ProviderSessionState<T>`. Construction order teaching point: `PIIRedactionContextProvider` must run after compaction in the `UseAIContextProviders` chain. |

---

## Test Scenarios

### V1 — AgentBaseline

**Scenario A — Clean document:**
`FetchDocument("DOC-004")` → clean policy text. Ask: "Does this apply to remote workers?"
Expected: Agent calls `LookupPolicy` + `FetchDocument`, returns correct answer. No PII present — baseline passes.

**Scenario B — PII document (the explicit bug):**
`FetchDocument("DOC-001")` → Alice Chen's contract with SSN `423-11-9988`.
Print the assembled request that would be sent to OpenAI (show SSN in plaintext).
Expected: SSN appears in the printed message — data leak confirmed. This is what V3 fixes.

**Scenario C — Multi-document review:**
Review DOC-002 then DOC-003 in sequence in the same session. Ask "What PII did you see across all documents?"
Expected: Agent recalls both documents' PII from session history. No redaction — demonstrates the scope of exposure.

**Scenario D — Tool roundtrip:**
Call `RecordReviewDecision("DOC-001", "approved", "No policy violations detected")`.
Expected: Decision recorded with correct document ID. Establishes tool flow for later versions.

---

### V2 — SimpleContextProvider

**Scenario A — Policy injection visible:**
Before any document paste, print the instructions the LLM receives from `ProvideAIContextAsync`.
Expected: LLM sees "Never expand REDACTED placeholders. Treat them as final. Do not infer original values."
Confirm instruction appears each turn even before `InvokingCoreAsync` exists (V2 can't redact yet, but policy is set).

**Scenario B — Audit log accumulation:**
Review DOC-001 then DOC-002. After each turn, print the session's `RedactionLog` from `ProviderSessionState<T>`.
Expected: Log grows turn-by-turn with turn number, document ID, and note "(no redaction performed — V3 adds this)".
Demonstrates that logging infrastructure works before the interceptor exists.

**Scenario C — Session isolation:**
Two `AgentSession` instances on the same `HRPolicyContextProvider` instance.
Session A reviews DOC-001. Session B reviews DOC-003.
Expected: Print both sessions' audit logs — Session A has DOC-001 only, Session B has DOC-003 only.
Proves `ProviderSessionState<T>` isolates state.

**Scenario D — StoreAIContextAsync timing:**
Show that `StoreAIContextAsync` fires AFTER the LLM responds (not before).
Print: "[PRE-CALL] audit log size: N" → LLM call → "[POST-CALL] audit log size: N+1".
Expected: Log entry appears only after the response, confirming the post-call hook.

---

### V3 — SafetyInterceptor

**Scenario A — PII redaction in-flight:**
`FetchDocument("DOC-001")` — Alice Chen's contract with SSN, bank account, email.
Print the message BEFORE and AFTER `InvokingCoreAsync` redaction.
Expected: BEFORE = raw SSN in plaintext. AFTER = `[REDACTED: SSN]`, `[REDACTED: BANK_ACCOUNT]`, `[REDACTED: EMAIL]`. LLM never sees original values.

**Scenario B — Response compliance scan:**
Simulate a scenario where the LLM echoes PII (use a test mock or prompt engineering to force it).
Expected: `InvokedCoreAsync` detects PII pattern in response messages, records a `ComplianceViolationRecord` in session state, prints "[COMPLIANCE] Response violation: SSN pattern detected in LLM output."

**Scenario C — Phantom-redaction prevention:**
Turn 1: Review DOC-001. `InvokingCoreAsync` injects redaction notice stamped with `WithAgentRequestMessageSource()`.
Turn 2: Ask a follow-up question. Print which messages are skipped by `GetAgentRequestMessageSourceType()` check.
Expected: Prior turn's injected notice is skipped. Audit log shows 1 SSN event (turn 1 only), not 2. Phantom event does not fire.

**Scenario D — Clean document passes through unmodified:**
`FetchDocument("DOC-004")` — no PII present.
Expected: `InvokingCoreAsync` finds no patterns, makes no modifications, injects no redaction notice. Print "(no redaction applied)" to confirm the scanner does not false-positive on regular text.

---

### V4 — Compaction

**Scenario A — Message count before vs after compaction:**
Review DOC-001 through DOC-004 across 8 turns. After each turn, print: stored message count vs LLM-visible message count.
Expected: Stored count grows to 16+. LLM-visible count stays bounded (≤8 messages after compaction kicks in).

**Scenario B — Audit log survives compaction:**
After 8 turns (early turns compacted away), print the full `RedactionLog` from `ProviderSessionState<T>`.
Expected: All 8 turns' redaction events are present — including events from turns whose messages were compacted away.

**Scenario C — Construction order: compaction vs redaction interceptor:**
Build agent with `PIIRedactionContextProvider` listed BEFORE `CompactionProvider` in `UseAIContextProviders`.
Run 3 turns. Show that redaction runs on un-compacted messages.
Rebuild with `CompactionProvider` listed FIRST. Show redaction runs on already-compacted messages.
Expected: Print message counts in both configurations. Document which order is correct for this use case and why (redaction after compaction means fewer messages to scan — more efficient, and compaction cannot "un-redact" anything since redaction is per-call not persistent).

**Scenario D — Compliance count across compacted session:**
After 12-turn session with compaction active, print: total documents reviewed, total PII events, total compliance violations.
Expected: All counts come from `ProviderSessionState<T>` — correct and complete despite early messages being compacted from LLM view.
