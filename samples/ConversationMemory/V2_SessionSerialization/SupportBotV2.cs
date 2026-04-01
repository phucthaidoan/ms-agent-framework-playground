using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;

namespace Samples.ConversationMemory.V2_SessionSerialization;

// V2: Session Serialization / Restoration
//
// KEY CONCEPT: AgentSession can be serialized to JSON and restored later.
// This enables sessions to survive:
//   - Application restarts
//   - Horizontal scaling (session stored in Redis, database, etc.)
//   - Passing session state between microservices
//
// What this sample shows:
//   1. Build up conversation history in a session
//   2. Serialize the session to JSON (SerializeSessionAsync → JsonElement → string)
//   3. Simulate an "app restart" by creating a NEW agent
//   4. Deserialize the saved JSON back into a session (DeserializeSessionAsync)
//   5. Continue the conversation — the new agent recalls everything

public static class SupportBotV2
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V2 — Session Serialization & Restoration");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        static AIAgent CreateAgent(string apiKey) =>
            new OpenAIClient(apiKey)
                .GetChatClient("gpt-4.1-nano")
                .AsAIAgent(
                    instructions: "You are a helpful customer support agent. Be concise.",
                    name: "SupportBot");

        // ─── Phase 1: Build up conversation history ──────────────────────────────────
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

        // ─── Phase 2: Serialize the session ──────────────────────────────────────────
        Output.Yellow("PHASE 2: Serialize session to JSON (for durable storage)");
        Output.Separator(false);

        // KEY: SerializeSessionAsync returns a JsonElement — serialize it to string for storage
        JsonElement serializedElement = await agent.SerializeSessionAsync(session);
        string serializedJson = JsonSerializer.Serialize(serializedElement, new JsonSerializerOptions { WriteIndented = false });

        Output.Gray($"Serialized session ({serializedJson.Length} chars):");
        // Show a truncated preview so learners can see it's real JSON
        Output.Blue(serializedJson.Length > 200 ? serializedJson[..200] + "..." : serializedJson);

        Output.Separator();

        // ─── Phase 3: Simulate app restart ────────────────────────────────────────────
        Output.Yellow("PHASE 3: Simulating app restart — creating brand new agent...");
        Output.Separator(false);

        // KEY: A completely new agent instance — same configuration as before
        AIAgent newAgent = CreateAgent(apiKey);

        // KEY: Deserialize the JSON back into a JsonElement, then restore the session
        JsonElement restoredElement = JsonSerializer.Deserialize<JsonElement>(serializedJson);
        AgentSession restoredSession = await newAgent.DeserializeSessionAsync(restoredElement);

        Output.Gray("New agent created. Session restored from JSON.");
        Output.Separator();

        // ─── Phase 4: Continue conversation with restored session ─────────────────────
        Output.Yellow("PHASE 4: Continue conversation — agent recalls history from before restart");
        Output.Separator(false);

        Output.Gray("User: Can you summarize what I told you so far?");
        string r3 = (await newAgent.RunAsync("Can you summarize what I told you so far?", restoredSession)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Separator();
        Output.Yellow("KEY LEARNING: SerializeSessionAsync/DeserializeSessionAsync enable cross-restart memory.");
        Output.Gray("IMPORTANT: Always restore with the SAME agent configuration that created the session.");
    }
}
