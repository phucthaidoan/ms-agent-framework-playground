using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Labs.ConversationMemory.V1_BasicSession;

// V1: Basic Session — Multi-Turn Conversation
//
// KEY CONCEPT: AgentSession is the stateful container that enables multi-turn conversations.
// Without a session, each RunAsync call is completely independent — the agent has no memory
// of previous messages.
//
// What this sample shows:
//   1. Creating an agent and session
//   2. Passing the session to each RunAsync call — agent remembers context across turns
//   3. WITHOUT a session — the agent cannot recall anything from a previous call
//
// This is the foundation: every conversation memory feature builds on AgentSession.

public static class SupportBotV1
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V1 — Basic Session (Multi-Turn Conversation)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new(apiKey);

        AIAgent agent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a helpful customer support agent. Be concise.",
                name: "SupportBot");

        // ─── Part 1: WITH a session — agent remembers context ───────────────────────
        Output.Yellow("PART 1: WITH a session — agent remembers context across turns");
        Output.Separator(false);

        // KEY: CreateSessionAsync() creates the stateful container
        AgentSession session = await agent.CreateSessionAsync();

        // Turn 1: provide information
        Output.Gray("User: My order #1234 is missing a blue widget.");
        string response1 = (await agent.RunAsync("My order #1234 is missing a blue widget.", session)).Text;
        Output.Green($"Bot:  {response1}");
        Console.WriteLine();

        // Turn 2: reference earlier context — the agent remembers because of the session
        Output.Gray("User: What order number did I mention?");
        string response2 = (await agent.RunAsync("What order number did I mention?", session)).Text;
        Output.Green($"Bot:  {response2}");

        Output.Separator();

        // ─── Part 2: WITHOUT a session — each call is independent ─────────────────────
        Output.Yellow("PART 2: WITHOUT a session — each call is independent (no memory)");
        Output.Separator(false);

        // Turn 1: provide information (no session parameter)
        Output.Gray("User: My order #5678 is missing a red widget.");
        string noSessionResponse1 = (await agent.RunAsync("My order #5678 is missing a red widget.")).Text;
        Output.Green($"Bot:  {noSessionResponse1}");
        Console.WriteLine();

        // Turn 2: agent has no memory — this is a brand-new conversation
        Output.Gray("User: What order number did I mention?");
        string noSessionResponse2 = (await agent.RunAsync("What order number did I mention?")).Text;
        Output.Green($"Bot:  {noSessionResponse2}");

        Output.Separator();
        Output.Yellow("KEY LEARNING: The session is what links turns together. No session = no memory.");
    }
}
