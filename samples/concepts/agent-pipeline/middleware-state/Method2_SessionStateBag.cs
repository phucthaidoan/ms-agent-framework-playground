// Method 2: AgentSession.StateBag
//
// STATE DIRECTION: Session-wide — persists across multiple RunAsync calls on the same session
// KEY API: AgentSession.StateBag.SetValue<T>(key, value) / TryGetValue<T>(key, out value)
//
// SubscriptionContextMiddleware runs on every turn. On Turn 1 it finds no StateBag entry,
// scans the user message for the word "Premium", and writes a SubscriptionInfo record.
// On Turn 2 the user asks a new question without mentioning their tier — the middleware
// reads the StateBag hit and injects a system-role context hint before calling the agent.
// The caller makes no changes between turns; the middleware handles everything.
//
// IMPORTANT: AgentSession.StateBag only accepts reference types.
// Wrap primitives in a small sealed class (SubscriptionInfo below).
//
// Pipeline (same for both turns):
//   SubscriptionContextMiddleware  ← reads/writes StateBag, may inject context hint
//   SupportAgent (LLM)

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Samples.AgentPipeline.MiddlewareState;

public static class SessionStateBag
{
    // Reference-type wrapper required by AgentSessionStateBag
    private sealed class SubscriptionInfo
    {
        public string Tier { get; set; } = "free";
    }

    private const string StateBagKey = "subscriptionInfo";

    // ── Middleware ───────────────────────────────────────────────────────────

    private static async Task<AgentResponse> SubscriptionContextMiddleware(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        IList<ChatMessage> messageList = messages as IList<ChatMessage> ?? messages.ToList();

        if (session is not null)
        {
            if (session.StateBag.TryGetValue<SubscriptionInfo>(StateBagKey, out SubscriptionInfo? info) && info is not null)
            {
                // StateBag hit — inject context hint for premium users
                Output.Gray($"[SUBSCRIPTION] StateBag hit — tier={info.Tier}");
                if (info.Tier.Equals("premium", StringComparison.OrdinalIgnoreCase))
                {
                    Output.Gray("[SUBSCRIPTION] Injecting Premium context hint into messages.");
                    messageList = [
                        new ChatMessage(ChatRole.System, "[CONTEXT: User is a Premium subscriber — skip upsell messaging, prioritise resolution speed]"),
                        ..messageList
                    ];
                }
            }
            else
            {
                // StateBag miss — scan message for tier mention
                Output.Gray("[SUBSCRIPTION] StateBag miss — scanning message for tier mention.");
                string lastText = messageList.LastOrDefault()?.Text ?? string.Empty;

                if (lastText.Contains("Premium", StringComparison.OrdinalIgnoreCase))
                {
                    Output.Gray("[SUBSCRIPTION] Found \"Premium\" — writing tier=premium to StateBag.");
                    session.StateBag.SetValue(StateBagKey, new SubscriptionInfo { Tier = "premium" });
                }
            }
        }

        return await innerAgent.RunAsync(messageList, session, options, cancellationToken);
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("Method 2 — AgentSession.StateBag (session-scoped, cross-turn)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        AIAgent baseAgent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are a helpful customer support agent. Keep responses concise (2-3 sentences).",
                name: "SupportAgent");

        AIAgent agent = baseAgent
            .AsBuilder()
            .Use(SubscriptionContextMiddleware, null)
            .Build();

        AgentSession session = await agent.CreateSessionAsync();

        // Turn 1 — user mentions Premium; middleware writes StateBag
        string turn1 = "Hi, I'm a Premium subscriber and I can't access the advanced analytics dashboard. It just shows a blank page.";
        Output.Yellow("TURN 1 — User mentions Premium subscription");
        Output.Gray($"User: {turn1}");
        Console.WriteLine();

        AgentResponse response1 = await agent.RunAsync(turn1, session);
        Output.Green($"Agent: {response1.Text}");
        Output.Separator();

        // Turn 2 — no "Premium" keyword; middleware reads StateBag and injects hint
        string turn2 = "I already cleared my browser cache and tried a different browser. Still blank.";
        Output.Yellow("TURN 2 — No tier keyword; StateBag hit injects context hint");
        Output.Gray($"User: {turn2}");
        Console.WriteLine();

        AgentResponse response2 = await agent.RunAsync(turn2, session);
        Output.Green($"Agent: {response2.Text}");
        Output.Separator();

        Output.Gray("KEY LEARNING: AgentSession.StateBag survives across turns in the same session.");
        Output.Gray("Middleware wrote tier=premium on Turn 1, read it on Turn 2 — no caller change needed.");
        Output.Gray("StateBag only accepts reference types; SubscriptionInfo wraps the string.");
        Output.Separator();
    }
}
