# V2b — File-Backed Session Persistence

## What You'll Learn

How to write a serialized `AgentSession` to a real file on disk, simulate a process exit, and resume the conversation by reading the file back — making the "app restart" scenario concrete rather than theoretical. Includes error handling for missing and corrupt session files.

## Key Concept

V2 shows *that* you can serialize a session. V2b shows *where* to put it. The serialized JSON is written to a temp file; the file path is the durable key you'd store in a database, Redis, or any other lookup mechanism in a real application.

```csharp
// Run 1 — Phase 2: Serialize and write to disk
JsonElement element = await agent.SerializeSessionAsync(session);
string json = JsonSerializer.Serialize(element);

string filePath = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid():N}.json");
await File.WriteAllTextAsync(filePath, json);
// → paste this path when you re-run

// Run 2 — upfront prompt: paste the path to resume
string saved = await File.ReadAllTextAsync(inputPath);
JsonElement restored = JsonSerializer.Deserialize<JsonElement>(saved);

// Must use a brand-new agent with identical configuration
AIAgent newAgent = CreateAgent(apiKey);
AgentSession restoredSession = await newAgent.DeserializeSessionAsync(restored);

// Phase 5: Continue exactly where you left off
string reply = (await newAgent.RunAsync("What did I tell you?", restoredSession)).Text;
```

## How to Run This Sample End-to-End

**Run 1 — build the conversation:**
1. Start the sample (number 1209) and press **Enter** at the file path prompt (blank = start fresh)
2. The bot builds a 2-turn conversation with Alice
3. The session is serialized and saved — the file path is printed
4. Phase 3 prints "SIMULATED PROCESS EXIT" and tells you to re-run

**Run 2 — resume:**
1. Start the sample again
2. Paste the file path printed in Run 1 at the prompt
3. A brand-new agent deserializes the session and recalls Alice's full conversation

## Architecture

```
                     Run 1
──────────────────────────────────────────────
[Prompt: blank → start fresh]
        ↓
  Build conversation (Phase 1)
        ↓
  SerializeSessionAsync()  →  JSON string  →  File on disk
        ↓
  [Phase 3: SIMULATED PROCESS EXIT]

                     Run 2
──────────────────────────────────────────────
[Prompt: paste file path]
        ↓
  File.ReadAllTextAsync()
        ↓
  DeserializeSessionAsync()  →  Restored session in new agent
        ↓
  Continue conversation as before (Phase 5)
```

The file path is the durable key — swap in a database row ID, a Redis key, or a blob path and only the storage layer changes.

## Error Handling

Caught upfront in Run 2, before the agent is created:

| Exception | Meaning | Message shown |
|-----------|---------|---------------|
| `FileNotFoundException` / `DirectoryNotFoundException` / `UnauthorizedAccessException` | Path is wrong, missing, or inaccessible | "File not found — check the path and ensure the session was saved" |
| `JsonException` | File is corrupt or from a different session format | "Invalid JSON — the file may be corrupt or from a different session format" |

## Critical Rule

> **Restore with the SAME agent configuration.** `DeserializeSessionAsync` must be called on a brand-new agent built with identical provider configuration (same `ChatHistoryProvider` type, same `AIContextProviders`). Missing or renamed providers silently lose their state.
>
> **Production note:** Session JSON contains full conversation history. Consider encrypting it at rest before writing to disk or a database.

## Running This Sample

```
Enter sample number: 1209
```
