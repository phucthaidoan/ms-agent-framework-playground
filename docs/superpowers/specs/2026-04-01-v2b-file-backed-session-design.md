# V2b — File-Backed Session Persistence: Design Spec

**Date:** 2026-04-01
**Status:** Approved

---

## Problem

The official Microsoft Agent Framework `Agent_Step03_PersistedConversations` sample and the existing `V2_SessionSerialization` sample both serialize a session to JSON but never write it to durable storage. The comment in `Agent_Step03` explicitly says:

> "In a real application, you would typically write the serialized session to a file or database for persistence, and read it back when resuming the conversation."

V2b closes this gap by actually doing that — writing to a file on disk and prompting the user to type the path back, making the "app restart" simulation tangible rather than theoretical.

---

## Goal

Add a new sample **V2b** that demonstrates the full persistence round-trip:

1. Build a conversation and serialize the session
2. Write the JSON to a real file on disk
3. Simulate a process exit
4. Prompt the user to type the file path (as they would retrieve a key from a DB in production)
5. Read the file, deserialize, resume the conversation
6. Handle failure cases (missing file, corrupt JSON) with clear guidance

---

## Location

- **New file:** `samples/ConversationMemory/V2b_FileBackedSession/SupportBotV2b.cs`
- **PRD.md:** Add V2b row to the learning path table (between V2 and V3)
- **Sample runner:** Register as sample 1209 (appended after V8), or renumbered to fit between 1201–1202 during implementation

---

## Architecture

A single `public static async Task RunSample()` method following the exact conventions of V1–V8:
- Uses `Output.Title`, `Output.Yellow`, `Output.Green`, `Output.Gray`, `Output.Blue`, `Output.Separator`
- Customer Support Bot domain: Alice, ticket #42, missing shipment
- Same `CreateAgent(apiKey)` local factory function pattern as V2

---

## Phase Structure

### Phase 1 — Build conversation
- Create agent via `CreateAgent(apiKey)`
- Create session via `agent.CreateSessionAsync()`
- Two turns: Alice introduces herself and her ticket, then adds shipment details
- Identical domain setup to V2 (continuity for learners moving through the series)

### Phase 2 — Serialize and write to file
- `SerializeSessionAsync` → `JsonElement` → `JsonSerializer.Serialize` → string
- Write to `Path.Combine(Path.GetTempPath(), $"supportbot-session-{Guid.NewGuid():N}.json")`
- Print the full file path prominently (so user can type it back)
- Print file size in bytes for concreteness

### Phase 3 — Simulated process exit
- Print a clear visual separator: `"=== SIMULATED PROCESS EXIT ==="`
- Print guidance: "In a real app, this is where your process would stop. The session lives only in the file above."
- Prompt: `"Type the session file path to resume: "`

### Phase 4 — Read, validate, deserialize
Two explicit failure cases caught before any agent call:
- **`FileNotFoundException`** → print: "File not found. Ensure the path is correct and the session was saved before the process exited."  → `return`
- **`JsonException`** → print: "Invalid JSON. The file may be corrupt or from a different session format." → `return`

Happy path:
- `File.ReadAllText(path)` → `JsonSerializer.Deserialize<JsonElement>` → `newAgent.DeserializeSessionAsync`
- Create a **new `AIAgent` instance** (same config) before deserializing — this is the key pedagogical point

### Phase 5 — Continue conversation
- Ask "Can you summarize what I told you so far?" with the restored session
- Agent recalls everything from before the simulated restart

---

## Key Learning Comment (end of sample)

```
KEY LEARNING: The file path here stands in for any durable key —
a database row ID, a Redis key, a blob storage path.
The serialized JSON is the portable session state.
IMPORTANT: Always restore with the SAME agent configuration that created the session.
```

---

## What This Does NOT Cover

- Retry loops on bad input (kept simple — Option A)
- File cleanup / deletion after demo (out of scope)
- Encryption of session JSON (out of scope — noted as a production concern in comments)
- Multiple sessions or session listing

---

## Relationship to Other Samples

| Sample | What it adds |
|--------|-------------|
| V2 | In-memory serialization only — no disk I/O |
| **V2b** | Actual file I/O + user-driven path input + error handling |
| V4a | Custom `ChatHistoryProvider` with file-backed message storage (different concept) |

V2b sits conceptually between V2 (serialization) and V3 (history inspection). It does not replace V4a — V4a stores individual messages per turn; V2b stores the entire session blob.

---

## References

- [Persisting and Resuming Agent Conversations — Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/tutorials/agents/persisted-conversation)
- [Storage — Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage)
- [Persistence Patterns for AI Agents That Survive Restarts — DEV Community](https://dev.to/aureus_c_b3ba7f87cc34d74d49/persistence-patterns-for-ai-agents-that-survive-restarts-59ck)
- [How To Add Persistence and Long-Term Memory to AI Agents — The New Stack](https://thenewstack.io/how-to-add-persistence-and-long-term-memory-to-ai-agents/)
