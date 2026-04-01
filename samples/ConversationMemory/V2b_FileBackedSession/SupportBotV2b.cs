using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;

namespace Samples.ConversationMemory.V2b_FileBackedSession;

// V2b: File-Backed Session Persistence
//
// KEY CONCEPT: In a real application, you write the serialized session to durable
// storage (file, database, Redis, blob) and read it back when resuming.
// V2 showed the API; V2b makes the restart simulation concrete by writing to disk.
//
// What this sample shows:
//   1. Build up conversation history in a session
//   2. Serialize the session to JSON and write to a temp file on disk
//   3. Simulate a process exit — the only thing that survives is the file
//   4. Prompt the user to type the file path back in (as they would look up a DB key)
//   5. Read the file, deserialize, continue — the new agent recalls everything
//
// Error handling:
//   - File not found: clear message explaining what happened
//   - Corrupt / invalid JSON: clear message about format mismatch

public static class SupportBotV2b
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V2b — File-Backed Session Persistence");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        static AIAgent CreateAgent(string apiKey) =>
            new OpenAIClient(apiKey)
                .GetChatClient("gpt-4.1-nano")
                .AsAIAgent(
                    instructions: "You are a helpful customer support agent. Be concise.",
                    name: "SupportBot");

        // ─── Phase 1: Build up conversation history ───────────────────────────────
        Output.Yellow("PHASE 1: Build up conversation history");
        Output.Separator(false);

        AIAgent agent = CreateAgent(apiKey);
        AgentSession session = await agent.CreateSessionAsync();

        Output.Gray("User: Hi, I'm Alice and my ticket #42 is about a missing shipment.");
        string r1 = (await agent.RunAsync("Hi, I'm Alice and my ticket #42 is about a missing shipment.", session)).Text;
        Output.Green($"Bot:  {r1}");
        Console.WriteLine();

        Output.Gray("User: The shipment was supposed to arrive last Monday.");
        string r2 = (await agent.RunAsync("The shipment was supposed to arrive last Monday.", session)).Text;
        Output.Green($"Bot:  {r2}");

        Output.Separator();

        // ─── Phase 2: Serialize and write to file ─────────────────────────────────
        Output.Yellow("PHASE 2: Serialize session and write to file");
        Output.Separator(false);

        JsonElement serializedElement = await agent.SerializeSessionAsync(session);
        string serializedJson = JsonSerializer.Serialize(serializedElement, new JsonSerializerOptions { WriteIndented = false });

        string filePath = Path.Combine(Path.GetTempPath(), $"supportbot-session-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(filePath, serializedJson);

        Output.Blue($"Session saved to file ({serializedJson.Length} chars):");
        Output.Blue(filePath);

        Output.Separator();

        // ─── Phase 3: Simulated process exit ──────────────────────────────────────
        Output.Yellow("PHASE 3: === SIMULATED PROCESS EXIT ===");
        Output.Gray("In a real app, this is where your process would stop.");
        Output.Gray("The conversation lives only in the file above.");
        Output.Gray("The file path stands in for any durable key — a DB row ID, a Redis key, a blob path.");
        Output.Separator();

        // ─── Phase 4: Prompt user, read file, deserialize ─────────────────────────
        Output.Yellow("PHASE 4: Resume from file");
        Output.Separator(false);
        Output.Gray("Type the session file path to resume:");
        Console.Write("> ");
        string inputPath = (Console.ReadLine() ?? string.Empty).Trim();

        string restoredJson;
        try
        {
            restoredJson = await File.ReadAllTextAsync(inputPath);
        }
        catch (FileNotFoundException)
        {
            Output.Red($"File not found: {inputPath}");
            Output.Gray("Ensure the path is correct and the session was saved before the process exited.");
            return;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            Output.Red($"Could not read file: {ex.Message}");
            return;
        }

        JsonElement restoredElement;
        try
        {
            restoredElement = JsonSerializer.Deserialize<JsonElement>(restoredJson);
        }
        catch (JsonException)
        {
            Output.Red("Invalid JSON. The file may be corrupt or from a different session format.");
            return;
        }

        // KEY: A completely new agent instance — same configuration as Phase 1
        AIAgent newAgent = CreateAgent(apiKey);
        AgentSession restoredSession = await newAgent.DeserializeSessionAsync(restoredElement);

        Output.Gray("New agent created. Session restored from file.");
        Output.Separator();

        // ─── Phase 5: Continue conversation with restored session ─────────────────
        Output.Yellow("PHASE 5: Continue conversation — agent recalls history from before restart");
        Output.Separator(false);

        Output.Gray("User: Can you summarize what I told you so far?");
        string r3 = (await newAgent.RunAsync("Can you summarize what I told you so far?", restoredSession)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Separator();
        Output.Yellow("KEY LEARNING: The file path stands in for any durable key —");
        Output.Yellow("a database row ID, a Redis key, a blob storage path.");
        Output.Yellow("The serialized JSON is the portable session state.");
        Output.Gray("IMPORTANT: Always restore with the SAME agent configuration that created the session.");
        Output.Gray("NOTE: In production, consider encrypting session JSON — it contains conversation history.");
    }
}
