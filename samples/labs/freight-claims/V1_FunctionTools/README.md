# V1 — Function Tools (No Middleware)

## What You'll Learn

How `AIFunctionFactory.Create()` connects an agent to real backend data — and what happens when there is no middleware to control what the agent does with that data.

## Key Concept

`AIFunctionFactory.Create()` wraps a plain C# method and exposes it to the LLM as a callable tool. It uses reflection to read `[Description]` attributes on the method and its parameters to auto-generate the JSON schema the model receives — no manual schema writing needed.

```csharp
AIAgent agent = new OpenAIClient(apiKey)
    .GetChatClient("gpt-4.1-nano")
    .AsAIAgent(
        instructions:
            "For every claim: call LookupShipment to get cargo details, " +
            "then classify the damage, then call ApproveClaim with decision " +
            "approve, escalate, or reject and a concise reason.",
        tools:
        [
            AIFunctionFactory.Create(LookupShipment, name: nameof(LookupShipment)),
            AIFunctionFactory.Create(ApproveClaim,   name: nameof(ApproveClaim)),
        ]);
```

The agent autonomously decides when to call tools based on the system prompt and the incoming claim text.

## Architecture

```
Claim text ──► RunAsync()
                   │
                   ▼
            ┌─────────────┐
            │     LLM     │  ◄── system prompt + claim + tool schemas
            └──────┬──────┘
                   │ tool call requested?
           ┌───────▼────────┐
           │ Yes → invoke   │
           │ No  → reply    │
           └───────┬────────┘
                   │ tool result appended to context
                   ▼
            ┌─────────────┐
            │     LLM     │  ◄── loop repeats until LLM emits text reply
            └─────────────┘
```

The loop has no deduplication layer — every tool call the LLM requests is executed.

## Observed Behavior: Duplicate Tool Calls

### What was observed (Scenario B — SHP-2002, $15,000 electronics)

```
[TOOL] LookupShipment(SHP-2002) → cargo=Electronics, value=$15000, San Jose→Austin
[TOOL] LookupShipment(SHP-2002) → cargo=Electronics, value=$15000, San Jose→Austin
[TOOL] LookupShipment(SHP-2002) → cargo=Electronics, value=$15000, San Jose→Austin
[TOOL] ApproveClaim(SHP-2002, reject)
DECISION [SHP-2002]: REJECTED — Damage occurred during handling...
[TOOL] ApproveClaim(SHP-2002, escalate)
DECISION [SHP-2002]: ESCALATED — Potential carrier liability due to improper securing...
```

`LookupShipment` fired 3 times. `ApproveClaim` fired twice with contradictory decisions. Both side-effectful calls wrote to the system.

### Why it happens

Small, cost-optimized models like `gpt-4.1-nano` have weaker instruction-following than larger models and are more likely to re-invoke tools within a single turn. The LLM has no explicit "already called this tool" state — it must infer that from re-reading its own prior tool-call messages in the context window. When it is uncertain, or when the system prompt phrase "call LookupShipment first" is interpreted as a standing rule rather than a one-time instruction, it re-issues the call. The framework executes every tool call the LLM requests; there is no built-in deduplication at this layer by design.

### Why it matters

`LookupShipment` is read-only, so duplicate calls are wasteful but harmless. `ApproveClaim` writes a decision to the claims system — a duplicate call with a *different* decision corrupts the record. This is the specific danger V2 and V4 address.

### Further Reading

**Model provider documentation**
- [OpenAI Function Calling guide](https://platform.openai.com/docs/guides/function-calling) — covers the `parallel_tool_calls` parameter and model behaviour differences
- [Anthropic Tool Use Overview](https://docs.anthropic.com/en/docs/agents-and-tools/tool-use/overview) — the full tool invocation lifecycle from the model's perspective
- [Anthropic Implement Tool Use](https://docs.anthropic.com/en/docs/agents-and-tools/tool-use/implement-tool-use) — includes idempotency design guidance, the recommended countermeasure

**Community and practitioner reports**
- [OpenAI Community: Duplicate Tool Calls](https://community.openai.com/t/issues-with-unstable-natural-language-invocation-and-duplicate-tool-calls/1370573) — practitioner-reported confirmation of this exact behaviour with proposed workarounds
- [How to Build Reliable Tool Calls for AI Agents (Unified.to)](https://unified.to/blog/how_to_build_reliable_tool_calls_for_ai_agents) — covers idempotency via unique request IDs, the consensus industry fix
- [The Unreasonable Effectiveness of an LLM Agent Loop (sketch.dev)](https://sketch.dev/blog/agent-loop) — technical analysis of agent loop reliability patterns across models

**Research**
- [AgentIF: Benchmarking Instruction Following in Agentic Scenarios (arXiv 2505.16944)](https://arxiv.org/html/2505.16944v1) — shows how smaller models degrade on instruction-following constraints in agentic loops
- [Agentic Reasoning for Large Language Models — survey (arXiv 2601.12538)](https://arxiv.org/abs/2601.12538) — covers tool use reliability and reasoning loop stability across model sizes

## Observed Behavior: Non-Deterministic Decisions

### What was observed

Running Scenario B multiple times against the same claim produces different `ApproveClaim` outcomes:

```
Run 1:  DECISION [SHP-2002]: REJECTED   — damage occurred during handling
Run 2:  DECISION [SHP-2002]: ESCALATED  — potential carrier liability
Run 3:  DECISION [SHP-2002]: APPROVED   — standard handling damage, covered
```

Same input. Same code. Different decision every time.

### Why it happens: temperature sampling

LLMs do not produce deterministic output by default. At each step of text generation, the model computes a probability distribution over all possible next tokens and *samples* from it. **Temperature** controls how spread that sampling is:

| Temperature | Effect |
|-------------|--------|
| `0.0` | Always picks the highest-probability token |
| `1.0` (OpenAI default) | Samples proportionally — lower-probability tokens appear occasionally |
| `> 1.0` | Flattens the distribution — increases randomness |

`gpt-4.1-nano` runs at `temperature=1.0` because no override is set in the agent construction. Every run re-samples the full reasoning chain from scratch.

**Important nuance:** Even `temperature=0` does not fully guarantee determinism. Floating-point arithmetic differences and variable batch sizes under concurrent load can still produce different outputs. See the references below.

### Why Scenario B is especially unstable

The damage description — *"pallet dropped during unloading"* — sits at the boundary of two legitimate classifications:

- **Handling damage** → carrier not liable → `reject`
- **Carrier liability** (improperly secured pallet) → carrier responsible → `approve` or `escalate`

The system prompt gives the model three valid decisions but provides **no rule** mapping a classification to a specific decision. With a small model and an ambiguous input, probability mass is spread across multiple valid paths — whichever is sampled becomes the output. A larger model resolves this more consistently, but is still not deterministic at `temperature=1.0`.

### Three levers to control this

| Lever | How | Limitation |
|-------|-----|------------|
| Set `temperature=0` | Pass `new ChatCompletionOptions { Temperature = 0 }` to the chat client | Reduces but does not fully eliminate non-determinism (float arithmetic, batch size) |
| Add explicit decision rules to the system prompt | `"If declared value > $10,000, always escalate"` removes model discretion for known cases | Still model-executed — prompt changes or model updates can shift behaviour |
| Move the decision to code (middleware) | V3 `ValueGuardrailMiddleware` intercepts before the LLM is called — a code branch, not a model choice | Architecturally safest: rule is explicit, testable, and version-controlled |

For consequential decisions in production, only lever 3 is safe.

### Further Reading

**Temperature and non-determinism**
- [Building LLM Applications for Production — Chip Huyen](https://huyenchip.com/2023/04/11/llm-engineering.html) — temperature, reproducibility challenges, and majority voting for consistency
- [Why Temperature=0 Doesn't Guarantee Determinism — Michael Brenndoerfer](https://mbrenndoerfer.com/writing/why-llms-are-not-deterministic) — floating-point arithmetic and implementation details behind residual randomness
- [Does Temperature=0 Guarantee Deterministic Outputs? — Vincent Schmalbach](https://www.vincentschmalbach.com/does-temperature-0-guarantee-deterministic-llm-outputs/) — technical analysis of why the guarantee is weaker than expected
- [Defeating Nondeterminism in LLM Inference — Simon Willison](https://simonwillison.net/2025/Sep/11/defeating-nondeterminism/) — root cause (variable batch sizes under load) and PyTorch-level fixes
- [Zero Temperature Randomness — Martynas Šubonis](https://martynassubonis.substack.com/p/zero-temperature-randomness-in-llms) — mathematical explanation of persistent randomness at minimum temperature
- [Temperature Parameter Reference — Vellum AI](https://www.vellum.ai/llm-parameters/temperature) — range comparison across OpenAI, Anthropic, and Google; interaction with top-p and top-k
- [Consistent and Reproducible LLM Outputs in 2025 — Keywords AI](https://www.keywordsai.co/blog/llm_consistency_2025) — temperature + seeding + structured prompts across OpenAI, Claude, Gemini, and vLLM

**Prompt engineering for consistency**
- [Prompt Engineering — Lilian Weng (Lil'Log)](https://lilianweng.github.io/posts/2023-03-15-prompt-engineering/) — authoritative overview of chain-of-thought, few-shot, and temperature's role in reducing variance
- [Self-Consistency — Prompting Guide](https://www.promptingguide.ai/techniques/consistency) — sampling multiple reasoning paths and using majority voting for reliable answers
- [Controlling Randomness: Temperature and Seed — Dylan Castillo](https://dylancastillo.co/posts/seed-temperature-llms.html) — practical guide to combining temperature and seed parameters
- [Non-Determinism and Prompt Optimization in LLMs — Future AGI](https://futureagi.com/blogs/non-deterministic-llm-prompts-2025) — structured prompts and explicit decision rules to reduce output variance
- [Prompt Engineering for Consistency — PMC/NIH](https://pmc.ncbi.nlm.nih.gov/articles/PMC10879172/) — peer-reviewed research on structured prompt techniques improving consistency across reasoning tasks

## Key Points

- `AIFunctionFactory.Create()` uses reflection on `[Description]` attributes to generate tool JSON schema automatically — no manual schema authoring needed.
- Tool registration is fixed at agent construction time; tools cannot be added or removed per turn in V1.
- The agent loop is fully LLM-driven — the framework executes any tool call the model requests, including repeated ones.
- Small models are more likely to issue redundant tool calls than larger models; this is model behaviour, not a framework bug.
- Side-effectful tools (writes, approvals) are dangerous in this architecture — duplicate calls have real consequences and there is no middleware to intercept them yet.
- Each `RunAsync` call uses no session, so Scenario A and Scenario B are independent turns with no cross-claim memory.
- The default `temperature=1.0` means the model re-samples its reasoning on every run — identical inputs can produce different decisions.
- Setting `temperature=0` reduces but does not eliminate non-determinism; moving consequential decisions to middleware (V3) is the only architecturally safe fix.

## What's Missing (Leads to V2)

V1 has no audit trail. When `ApproveClaim` fires twice with contradictory decisions, there is no timestamped log of which call came first or what the context was — the company cannot prove what decision was actually made. V2 wraps each `RunAsync` call with `AuditMiddleware` to record pre- and post-run entries. V3 then adds `ValueGuardrailMiddleware` to intercept high-value claims before the LLM is called at all.

## Running This Sample

```bash
cd samples/labs/freight-claims/V1_FunctionTools
dotnet run
```
