using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.ConversationMemory.V3_InMemoryHistory;

// V3: InMemoryChatHistoryProvider — Accessing Raw History
//
// KEY CONCEPT: The built-in InMemoryChatHistoryProvider stores the full conversation
// as a List<ChatMessage>. You can retrieve these messages at any time for:
//   - Auditing / compliance logging
//   - Displaying conversation history in a UI
//   - Debugging and inspection
//   - Feeding into another system
//
// What this sample shows:
//   1. The default agent uses InMemoryChatHistoryProvider automatically
//   2. After several turns, retrieve the raw ChatMessage list via GetService<T>
//   3. Inspect roles (User / Assistant) and message content
//   4. Observe how message count grows with each turn

public static class SupportBotV3
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V3 — InMemory History Provider (Raw Message Access)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // The default AsAIAgent() wires up InMemoryChatHistoryProvider automatically
        // when using OpenAI Chat Completion (which doesn't manage history server-side)
        AIAgent agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a helpful customer support agent. Be concise.",
                name: "SupportBot");

        AgentSession session = await agent.CreateSessionAsync();

        // ─── Run a multi-turn conversation ────────────────────────────────────────────
        Output.Yellow("Running multi-turn support conversation...");
        Output.Separator(false);

        string[] userMessages =
        [
            "Hi, I need help with my internet connection.",
            "It keeps dropping every 30 minutes.",
            "I've already restarted the router twice today.",
        ];

        foreach (string message in userMessages)
        {
            Output.Gray($"User: {message}");
            string response = (await agent.RunAsync(message, session)).Text;
            Output.Green($"Bot:  {response}");
            Console.WriteLine();
        }

        Output.Separator();

        // ─── Retrieve raw message history ─────────────────────────────────────────────
        Output.Yellow("Retrieving raw conversation history from InMemoryChatHistoryProvider...");
        Output.Separator(false);

        // KEY: GetService<T>() retrieves the provider attached to the agent
        InMemoryChatHistoryProvider? historyProvider = agent.GetService<InMemoryChatHistoryProvider>();

        if (historyProvider is null)
        {
            Output.Red("No InMemoryChatHistoryProvider found — this shouldn't happen with the default agent setup.");
            return;
        }

        // KEY: GetMessages(session) returns the raw ChatMessage list for that session
        IList<ChatMessage> messages = historyProvider.GetMessages(session) ?? [];

        Output.Blue($"Total messages stored: {messages.Count}");
        Output.Gray("(Each turn = 1 user message + 1 assistant message = 2 messages per turn)");
        Console.WriteLine();

        // Print each message with its role
        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage msg = messages[i];
            string roleLabel = msg.Role == ChatRole.User ? "USER     " : "ASSISTANT";
            string content = msg.Text ?? "(no text content)";
            string preview = content.Length > 100 ? content[..100] + "..." : content;

            Output.Gray($"  [{i + 1:D2}] {roleLabel}: {preview}");
        }

        Output.Separator();
        Output.Yellow("KEY LEARNING: GetService<InMemoryChatHistoryProvider>().GetMessages(session)");
        Output.Gray("gives you the raw List<ChatMessage> for auditing, display, or export.");
    }
}
