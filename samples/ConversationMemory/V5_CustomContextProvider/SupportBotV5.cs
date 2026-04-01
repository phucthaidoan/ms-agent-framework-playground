using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json.Serialization;

namespace Samples.ConversationMemory.V5_CustomContextProvider;

// V5: Custom AIContextProvider — Memory Injection
//
// KEY CONCEPT: AIContextProvider runs around EVERY agent invocation:
//   - BEFORE the LLM call: ProvideAIContextAsync() — inject additional instructions/messages
//   - AFTER the LLM call:  StoreAIContextAsync()   — extract and persist facts from the exchange
//
// This decouples memory from orchestration: the orchestrator doesn't need to know what
// the agent remembers — the provider handles it transparently.
//
// This sample's UserPreferenceContextProvider:
//   - STORES: Watches for messages like "I prefer email" → saves contact preference
//   - INJECTS: On each turn, prepends stored preferences as context instructions
//   - EFFECT:  The agent uses the preference in turn 3 without being re-told in turn 3
//
// Compare this to ChatHistoryProvider (V4): that stores ALL messages.
// AIContextProvider stores EXTRACTED FACTS — selective, semantic memory.

public static class SupportBotV5
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V5 — Custom AIContextProvider (Memory Injection)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        UserPreferenceContextProvider preferenceProvider = new();

        AIAgent agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new() { Instructions = "You are a helpful customer support agent. Be concise and personalized." },
                // KEY: Register the context provider — it runs around every RunAsync call
                AIContextProviders = [preferenceProvider]
            });

        AgentSession session = await agent.CreateSessionAsync();

        // ─── Turn 1: State a preference ───────────────────────────────────────────────
        Output.Yellow("Turn 1: User states a preference");
        Output.Separator(false);

        Output.Gray("User: Hi, I prefer to be contacted via email rather than phone.");
        string r1 = (await agent.RunAsync("Hi, I prefer to be contacted via email rather than phone.", session)).Text;
        Output.Green($"Bot:  {r1}");

        PrintProviderState(preferenceProvider, session);

        Output.Separator();

        // ─── Turn 2: Unrelated message ─────────────────────────────────────────────────
        Output.Yellow("Turn 2: Unrelated support request");
        Output.Separator(false);

        Output.Gray("User: I have a problem with my billing this month.");
        string r2 = (await agent.RunAsync("I have a problem with my billing this month.", session)).Text;
        Output.Green($"Bot:  {r2}");

        Output.Separator();

        // ─── Turn 3: Ask about contact — agent uses preference WITHOUT being reminded ─
        Output.Yellow("Turn 3: Ask about follow-up — agent uses stored preference automatically");
        Output.Separator(false);

        Output.Gray("User: How will your team follow up with me about this billing issue?");
        string r3 = (await agent.RunAsync("How will your team follow up with me about this billing issue?", session)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Gray("(Notice: agent mentions email — the preference was injected by the provider, not re-stated by user)");

        Output.Separator();
        Output.Yellow("KEY LEARNING: AIContextProvider injects extracted memory before each LLM call.");
        Output.Gray("ProvideAIContextAsync() = inject  |  StoreAIContextAsync() = persist");
    }

    private static void PrintProviderState(UserPreferenceContextProvider provider, AgentSession session)
    {
        UserPreferenceContextProvider.Preferences prefs = provider.GetPreferences(session);
        if (prefs.ContactMethod is not null)
            Output.Blue($"  [Provider state] Stored contact preference: '{prefs.ContactMethod}'");
        else
            Output.Gray("  [Provider state] No preference stored yet.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────
// Custom AIContextProvider: Extracts and injects user preferences
// ─────────────────────────────────────────────────────────────────────────────────────────

public sealed class UserPreferenceContextProvider : AIContextProvider
{
    // KEY: Per-session preferences stored INSIDE the session — not in this instance field
    private readonly ProviderSessionState<Preferences> _sessionState = new(
        stateInitializer: _ => new Preferences(),
        stateKey: nameof(UserPreferenceContextProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // BEFORE each LLM call: inject stored preferences as additional context
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        Preferences prefs = _sessionState.GetOrInitializeState(context.Session);

        if (prefs.ContactMethod is null)
        {
            // Nothing stored yet — return empty context (no extra instructions)
            return new ValueTask<AIContext>(new AIContext());
        }

        // Inject the preference as an instruction the LLM will follow
        string injectedInstruction = $"IMPORTANT: This user has stated they prefer to be contacted via {prefs.ContactMethod}. " +
                                     $"Always mention {prefs.ContactMethod} as the follow-up channel when relevant.";

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = injectedInstruction
        });
    }

    // AFTER each LLM call: scan user messages for preference keywords and store them
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        Preferences prefs = _sessionState.GetOrInitializeState(context.Session);

        // Scan the request messages (user input) for preference statements
        foreach (ChatMessage message in context.RequestMessages)
        {
            string text = message.Text?.ToLowerInvariant() ?? string.Empty;

            if (text.Contains("prefer email") || text.Contains("via email") || text.Contains("by email"))
            {
                prefs.ContactMethod = "email";
            }
            else if (text.Contains("prefer phone") || text.Contains("via phone") || text.Contains("by phone") || text.Contains("call me"))
            {
                prefs.ContactMethod = "phone";
            }
            else if (text.Contains("prefer chat") || text.Contains("via chat") || text.Contains("live chat"))
            {
                prefs.ContactMethod = "live chat";
            }
        }

        // Persist updated preferences back into the session
        _sessionState.SaveState(context.Session, prefs);
        return default;
    }

    // Helper to read state for display purposes
    public Preferences GetPreferences(AgentSession session) => _sessionState.GetOrInitializeState(session);

    public sealed class Preferences
    {
        [JsonPropertyName("contactMethod")]
        public string? ContactMethod { get; set; }
    }
}
