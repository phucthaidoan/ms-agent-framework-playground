#pragma warning disable MAAI001  // Compaction API is experimental

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.Labs.ConversationMemory.V4a_CustomHistoryProvider_File;
using Samples.Labs.ConversationMemory.V5_CustomContextProvider;
using Samples.SampleUtilities;
using System.Text.Json;

namespace Samples.Labs.ConversationMemory.V7_Integration;

// V7: Integration — All Concepts Combined
//
// This sample shows all the Conversations & Memory concepts working together in a realistic
// Customer Support Bot that persists across simulated restarts.
//
// What's wired in:
//   ✅ AgentSession           — multi-turn conversation state (V1)
//   ✅ Session serialization  — survive restarts (V2)
//   ✅ FileBackedChatHistoryProvider — durable storage (V4a)
//   ✅ UserPreferenceContextProvider — memory injection (V5)
//   ✅ PipelineCompactionStrategy   — token management (V6)
//
// Three-phase demo:
//   Phase 1 — First session: establish preferences, report issue, serialize at end
//   Phase 2 — Restore session: history + preferences intact after "restart"
//   Phase 3 — Long conversation: show compaction managing LLM token costs while storage grows normally
//
// The goal: see how each building block from V1–V6 contributes to the complete picture.

public static class SupportBotV7
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V7 — Integration (All Concepts Combined)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new(apiKey);
        IChatClient chatClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();
        IChatClient summarizerClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        // ─────────────────────────────────────────────────────────────────────────────
        // PHASE 1: First session — establish context, serialize at end
        // ─────────────────────────────────────────────────────────────────────────────
        Output.Blue("═══════════════════════════════════════");
        Output.Blue(" PHASE 1: First conversation session");
        Output.Blue("═══════════════════════════════════════");
        Console.WriteLine();

        FileBackedChatHistoryProvider fileProvider = new();
        UserPreferenceContextProvider preferenceProvider = new();

        // Pipeline compaction — keeps history bounded even in long conversations
        PipelineCompactionStrategy compactionPipeline = new(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(200), 2, null),
            new SummarizationCompactionStrategy(summarizerClient, CompactionTriggers.TokensExceed(800), 4, null, null),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(10), 1, null),
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(2000), 2, null),
        ]);

        // Wire everything together
        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(compactionPipeline))  // V6: compaction
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new()
                {
                    Instructions = "You are a helpful customer support agent. Be concise and personalized."
                },
                ChatHistoryProvider = fileProvider,        // V4a: file-backed storage
                AIContextProviders = [preferenceProvider]  // V5: preference injection
            });

        AgentSession session = await agent.CreateSessionAsync();  // V1: session

        // Turn 1: state preference
        Output.Gray("User: Hi, I'm David. I prefer to be contacted via email.");
        string p1r1 = (await agent.RunAsync("Hi, I'm David. I prefer to be contacted via email.", session)).Text;
        Output.Green($"Bot:  {p1r1}");
        Console.WriteLine();

        // Turn 2: report issue
        Output.Gray("User: My internet speed dropped significantly after last night's maintenance.");
        string p1r2 = (await agent.RunAsync("My internet speed dropped significantly after last night's maintenance.", session)).Text;
        Output.Green($"Bot:  {p1r2}");
        Console.WriteLine();

        // Turn 3: additional detail
        Output.Gray("User: I'm getting 5 Mbps but my plan is 100 Mbps.");
        string p1r3 = (await agent.RunAsync("I'm getting 5 Mbps but my plan is 100 Mbps.", session)).Text;
        Output.Green($"Bot:  {p1r3}");
        Console.WriteLine();

        // Show what the preference provider captured
        UserPreferenceContextProvider.Preferences prefs = preferenceProvider.GetPreferences(session);
        FileBackedChatHistoryProvider.State fileState = fileProvider.GetState(session);
        Output.Blue($"[Preference provider stored]: contact method = '{prefs.ContactMethod}'");
        Output.Blue($"[File history provider]: history at {fileState.FilePath}");

        // V2: Serialize the session for the simulated restart
        JsonElement serialized = await agent.SerializeSessionAsync(session);
        string serializedJson = JsonSerializer.Serialize(serialized);

        Output.Gray($"\nSession serialized ({serializedJson.Length} chars) — simulating app restart...");

        Output.Separator();

        // ─────────────────────────────────────────────────────────────────────────────
        // PHASE 2: Restore session after simulated restart
        // ─────────────────────────────────────────────────────────────────────────────
        Output.Blue("═══════════════════════════════════════");
        Output.Blue(" PHASE 2: After restart — restore session");
        Output.Blue("═══════════════════════════════════════");
        Console.WriteLine();

        // New instances — simulates a fresh process start
        FileBackedChatHistoryProvider fileProvider2 = new();
        UserPreferenceContextProvider preferenceProvider2 = new();

        PipelineCompactionStrategy compactionPipeline2 = new(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(200), 2, null),
            new SummarizationCompactionStrategy(summarizerClient, CompactionTriggers.TokensExceed(800), 4, null, null),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(10), 1, null),
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(2000), 2, null),
        ]);

        AIAgent agent2 = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(compactionPipeline2))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new() { Instructions = "You are a helpful customer support agent. Be concise and personalized." },
                ChatHistoryProvider = fileProvider2,
                AIContextProviders = [preferenceProvider2]
            });

        // V2: Restore session from serialized JSON
        JsonElement restoredElement = JsonSerializer.Deserialize<JsonElement>(serializedJson);
        AgentSession restoredSession = await agent2.DeserializeSessionAsync(restoredElement);

        Output.Gray("User: Can you remind me of my issue and how your team will contact me?");
        string p2r1 = (await agent2.RunAsync("Can you remind me of my issue and how your team will contact me?", restoredSession)).Text;
        Output.Green($"Bot:  {p2r1}");

        Output.Separator();
        Output.Gray("✅ Agent recalled: conversation history (from file) + contact preference (from provider state)");

        Output.Separator();

        // ─────────────────────────────────────────────────────────────────────────────
        // PHASE 3: Long conversation — compaction keeps history bounded
        // ─────────────────────────────────────────────────────────────────────────────
        Output.Blue("═══════════════════════════════════════");
        Output.Blue(" PHASE 3: Long conversation — compaction in action");
        Output.Blue("═══════════════════════════════════════");
        Console.WriteLine();

        string[] additionalTurns =
        [
            "I've tried restarting my router three times.",
            "My neighbor has the same issue with the same ISP.",
            "The problem started at exactly 11 PM last night.",
            "I work from home so this is urgent for me.",
            "Can you escalate this to your network team?",
            "What's the expected resolution time?",
        ];

        Console.WriteLine();

        foreach (string msg in additionalTurns)
        {
            Output.Gray($"User: {msg}");
            string r = (await agent2.RunAsync(msg, restoredSession)).Text;
            Output.Green($"Bot:  {r}");
        }

        Console.WriteLine();
        await ShowCompactionSummary(fileProvider2, restoredSession, compactionPipeline2);

        Output.Separator();
        Output.Yellow("INTEGRATION COMPLETE: All 6 building blocks working together:");
        Output.Gray("  V1 — AgentSession        → multi-turn conversation state");
        Output.Gray("  V2 — Serialization       → survive the simulated restart");
        Output.Gray("  V4a — FileHistoryProvider → durable storage across runs");
        Output.Gray("  V5 — ContextProvider     → preference injected automatically");
        Output.Gray("  V6 — Compaction          → LLM's context stayed bounded in Phase 3 (storage grew normally)");
    }

    private static async Task ShowCompactionSummary(
        FileBackedChatHistoryProvider provider,
        AgentSession session,
        PipelineCompactionStrategy strategy)
    {
        FileBackedChatHistoryProvider.State state = provider.GetState(session);
        if (!File.Exists(state.FilePath))
            return;

        List<ChatMessage> stored = JsonSerializer.Deserialize<List<ChatMessage>>(
            File.ReadAllText(state.FilePath), AgentChatMessageJson.DefaultOptions) ?? [];

        // Re-apply compaction to see what the LLM actually received on the last call
        List<ChatMessage> compacted = [.. await CompactionProvider.CompactAsync(strategy, stored)];

        Output.Blue($"Stored in file: {stored.Count} messages (always grows — compaction does not modify storage)");
        Output.Blue($"LLM received on last call: {compacted.Count} / {stored.Count} messages");
        if (stored.Count > compacted.Count)
            Output.Magenta($"  → {stored.Count - compacted.Count} messages hidden from LLM by compaction");
        Output.Gray("  NOTE: Storage is always preserved in full. Compaction only trims what the LLM sees per call.");
    }
}
