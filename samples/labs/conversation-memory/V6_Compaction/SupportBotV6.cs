#pragma warning disable MAAI001  // Compaction API is experimental

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;

namespace Samples.Labs.ConversationMemory.V6_Compaction;

// V6: Compaction Strategies
//
// KEY CONCEPT: As conversations grow, the token count of chat history can exceed model
// context windows or drive up costs. Compaction strategies reduce history size while
// preserving important context.
//
// This sample demonstrates all five strategies:
//   1. TruncationCompactionStrategy  — drops oldest groups when token limit exceeded
//   2. SlidingWindowCompactionStrategy — keeps last N user turns
//   3. ToolResultCompactionStrategy  — collapses old tool call results
//   4. SummarizationCompactionStrategy — LLM-based summary of older messages
//   5. PipelineCompactionStrategy    — composes all four in a layered pipeline
//
// IMPORTANT:
//   - Compaction is EXPERIMENTAL → requires #pragma warning disable MAAI001
//   - Register CompactionProvider via AsBuilder().UseAIContextProviders() — NOT via
//     ChatClientAgentOptions.AIContextProviders — to ensure it runs inside the tool-calling loop
//   - Compaction only applies to LOCAL (in-memory) history providers,
//     not to service-managed history (OpenAI Responses API, Azure AI Foundry)

public static class SupportBotV6
{
    // Use very low token thresholds to trigger compaction with just a few turns
    private const int ToolResultTokenThreshold = 50;   // collapse tool results after 50 tokens
    private const int SummarizationThreshold = 200;    // summarize after 200 tokens
    private const int TurnsThreshold = 4;              // keep last 4 turns
    private const int TruncationThreshold = 50;        // hard backstop at 50 tokens

    private static readonly string[] SupportTopics =
    [
        "My internet is slow today.",
        "Can you check my account status?",
        "I received an unexpected charge.",
        "My last ticket was closed without resolution.",
        "I need to update my billing address.",
        "Is there a service outage in my area?",
        "I want to upgrade my plan.",
    ];

    public static async Task RunSample()
    {
        Output.Title("Support Bot V6 — Compaction Strategies");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new(apiKey);
        IChatClient chatClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        Output.Yellow("This sample demonstrates 5 compaction strategies with low token thresholds.");
        Output.Gray("Strategies are demonstrated individually then combined into a pipeline.");
        Output.Separator();

        // ─── Demo 1: TruncationCompactionStrategy ────────────────────────────────────
        //await DemoTruncation(chatClient, apiKey);

        // ─── Demo 2: SlidingWindowCompactionStrategy ─────────────────────────────────
        //await DemoSlidingWindow(chatClient, apiKey);

        //// ─── Demo 3: ToolResultCompactionStrategy ────────────────────────────────────
        //await DemoToolResult(chatClient, apiKey);

        //// ─── Demo 4: SummarizationCompactionStrategy ─────────────────────────────────
        //await DemoSummarization(client, chatClient, apiKey);

        //// ─── Demo 5: PipelineCompactionStrategy (all combined) ───────────────────────
        await DemoPipeline(client, chatClient, apiKey);
    }

    private static async Task DemoTruncation(IChatClient chatClient, string apiKey)
    {
        Output.Yellow("STRATEGY 1: TruncationCompactionStrategy — drops oldest groups");
        Output.Gray($"Trigger: token count exceeds {TruncationThreshold}. Drops oldest groups until below threshold.");
        Output.Separator(false);

        TruncationCompactionStrategy strategy = new(
            CompactionTriggers.TokensExceed(TruncationThreshold),
            minimumPreservedGroups: 2,  // always keep at least 2 recent groups
            target: null);  // stop when trigger no longer fires

        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(strategy))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new() { Instructions = "You are a support agent. Be very concise (one sentence)." }
            });

        AgentSession session = await agent.CreateSessionAsync();

        await RunTurnsAndShowHistory(agent, session, SupportTopics, "truncation", strategy);
        Output.Separator();
    }

    private static async Task DemoSlidingWindow(IChatClient chatClient, string apiKey)
    {
        Output.Yellow("STRATEGY 2: SlidingWindowCompactionStrategy — keeps last N user turns");
        Output.Gray($"Trigger: more than {TurnsThreshold} user turns. Removes older turns, keeps system + recent.");
        Output.Separator(false);

        SlidingWindowCompactionStrategy strategy = new(
            CompactionTriggers.TurnsExceed(TurnsThreshold),
            minimumPreservedTurns: 1,
            target: null);

        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(strategy))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new() { Instructions = "You are a support agent. Be very concise (one sentence)." }
            });

        AgentSession session = await agent.CreateSessionAsync();

        await RunTurnsAndShowHistory(agent, session, SupportTopics[..6], "sliding-window", strategy);
        Output.Separator();
    }

    private static async Task DemoToolResult(IChatClient chatClient, string apiKey)
    {
        Output.Yellow("STRATEGY 3: ToolResultCompactionStrategy — collapses old tool call results");
        Output.Gray($"Trigger: token count exceeds {ToolResultTokenThreshold}. Replaces old tool result groups with compact summaries.");
        Output.Separator(false);

        ToolResultCompactionStrategy strategy = new(
            CompactionTriggers.TokensExceed(ToolResultTokenThreshold),
            minimumPreservedGroups: 2,
            target: null);

        // Register a simple lookup tool so the agent has tool-call groups to compact
        AIFunction lookupTool = AIFunctionFactory.Create(
            ([System.ComponentModel.Description("The order ID to look up")] string orderId) =>
                $"Order {orderId}: 3 items, shipped 2024-01-10, expected 2024-01-15",
            name: "LookupOrder",
            description: "Look up an order by ID");

        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(strategy))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new()
                {
                    Instructions = "You are a support agent. Use LookupOrder tool when asked about orders. Be concise.",
                    Tools = [lookupTool]
                }
            });

        AgentSession session = await agent.CreateSessionAsync();

        Output.Gray("User: Can you look up order ORD-001?");
        string r1 = (await agent.RunAsync("Can you look up order ORD-001?", session)).Text;
        Output.Green($"Bot:  {r1}");
        Console.WriteLine();

        Output.Gray("User: Now look up order ORD-002 please.");
        string r2 = (await agent.RunAsync("Now look up order ORD-002 please.", session)).Text;
        Output.Green($"Bot:  {r2}");

        Console.WriteLine();
        await ShowHistoryTable(agent, session, "tool-result compaction", strategy);
        Output.Gray("(Old tool result groups are collapsed into compact summaries like '[Tool calls: LookupOrder]')");
        Output.Separator();
    }

    private static async Task DemoSummarization(OpenAIClient client, IChatClient chatClient, string apiKey)
    {
        Output.Yellow("STRATEGY 4: SummarizationCompactionStrategy — LLM-based summary of older turns");
        Output.Gray($"Trigger: token count exceeds {SummarizationThreshold}. Summarizes older messages into a single summary group.");
        Output.Separator(false);

        // KEY: Summarization requires a second LLM client (can use a smaller/cheaper model)
        IChatClient summarizerClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        SummarizationCompactionStrategy strategy = new(
            summarizerClient,
            CompactionTriggers.TokensExceed(SummarizationThreshold),
            minimumPreservedGroups: 2,
            summarizationPrompt: null,
            target: null);

        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(strategy))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new() { Instructions = "You are a support agent. Be very concise (one sentence)." }
            });

        AgentSession session = await agent.CreateSessionAsync();

        await RunTurnsAndShowHistory(agent, session, SupportTopics, "summarization", strategy);
        Output.Gray("(Older turns may be replaced by a [Summary] group — reduces tokens while preserving context)");
        Output.Separator();
    }

    private static async Task DemoPipeline(OpenAIClient client, IChatClient chatClient, string apiKey)
    {
        Output.Yellow("STRATEGY 5: PipelineCompactionStrategy — layered pipeline (all strategies combined)");
        Output.Gray("Strategies run in order: tool results → summarize → sliding window → truncate (backstop)");
        Output.Separator(false);

        IChatClient summarizerClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        // KEY: PipelineCompactionStrategy always triggers; each child evaluates its own trigger.
        // Order: gentlest first → most aggressive last (backstop).
        PipelineCompactionStrategy pipeline = new(
        [
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(ToolResultTokenThreshold), 2, null),
            new SummarizationCompactionStrategy(summarizerClient, CompactionTriggers.TokensExceed(SummarizationThreshold), 2, null, null),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(TurnsThreshold), 1, null),
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(TruncationThreshold), 2, null),
        ]);

        AIAgent agent = chatClient
            .AsBuilder()
            .UseAIContextProviders(new CompactionProvider(pipeline))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new() { Instructions = "You are a support agent. Be very concise (one sentence)." }
            });

        AgentSession session = await agent.CreateSessionAsync();

        await RunTurnsAndShowHistory(agent, session, SupportTopics, "pipeline", pipeline);
        Output.Separator();
        Output.Yellow("KEY LEARNING: PipelineCompactionStrategy = gentlest-first safety net.");
        Output.Gray("Register via AsBuilder().UseAIContextProviders() to run inside the tool-calling loop.");
    }

    // Helper: run through a topics array, show per-turn stored count, then print compacted view table.
    // NOTE: CompactionProvider does NOT mutate stored history — it only trims what the LLM sees per call.
    // Each turn always appends exactly 2 messages (user + assistant) to storage regardless of compaction.
    private static async Task RunTurnsAndShowHistory(AIAgent agent, AgentSession session, string[] topics, string label, CompactionStrategy strategy)
    {
        InMemoryChatHistoryProvider? provider = agent.GetService<InMemoryChatHistoryProvider>();

        for (int i = 0; i < topics.Length; i++)
        {
            Output.Gray($"User [{i + 1}]: {topics[i]}");
            string response = (await agent.RunAsync(topics[i], session)).Text;
            Output.Green($"Bot  [{i + 1}]: {response}");

            int count = provider?.GetMessages(session)?.Count ?? 0;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Turn {i + 1} → stored: {count} msgs");
            Console.ResetColor();
        }

        Console.WriteLine();
        await ShowHistoryTable(agent, session, label, strategy);
    }

    // Re-applies compaction on stored messages to show exactly what the LLM received on the last call.
    // Stored history is always the full original list — compaction is transient and never written back.
    private static async Task ShowHistoryTable(AIAgent agent, AgentSession session, string label, CompactionStrategy strategy)
    {
        InMemoryChatHistoryProvider? provider = agent.GetService<InMemoryChatHistoryProvider>();
        IList<ChatMessage>? stored = provider?.GetMessages(session);

        if (stored == null)
        {
            Output.Blue($"[{label}] (history unavailable)");
            return;
        }

        // Re-apply the same strategy to get the compacted view (what the LLM actually saw)
        IEnumerable<ChatMessage> compacted = await CompactionProvider.CompactAsync(strategy, stored);
        List<ChatMessage> compactedList = [.. compacted];

        int width = Math.Max(60, Console.WindowWidth);
        int hidden = stored.Count - compactedList.Count;

        // Header: compacted count / stored count makes the reduction immediately visible
        string headerCore = $" What the LLM sees after {label}: {compactedList.Count}/{stored.Count} messages ";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── " + headerCore + new string('─', Math.Max(0, width - headerCore.Length - 3)));
        Console.ResetColor();

        // Compacted message rows — this is what the LLM received
        for (int i = 0; i < compactedList.Count; i++)
            PrintCompactionMessage(i + 1, compactedList[i]);

        // Footer
        string footerCore = hidden > 0
            ? $" {hidden} message{(hidden == 1 ? "" : "s")} hidden from LLM by compaction "
            : " No messages hidden — compaction did not fire ";
        Console.ForegroundColor = hidden > 0 ? ConsoleColor.Magenta : ConsoleColor.DarkGray;
        Console.WriteLine("─── " + footerCore + new string('─', Math.Max(0, width - footerCore.Length - 4)));
        Console.ResetColor();

        Output.Gray("  NOTE: Storage is always preserved in full. Compaction only trims what the LLM sees per call.");
        Console.WriteLine();
    }

    private static void PrintCompactionMessage(int index, ChatMessage msg)
    {
        string roleStr = msg.Role.ToString();
        ConsoleColor color = roleStr switch
        {
            "system"    => ConsoleColor.DarkGray,
            "user"      => ConsoleColor.White,
            "assistant" => ConsoleColor.Green,
            "tool"      => ConsoleColor.Yellow,
            _           => ConsoleColor.Gray
        };

        Console.ForegroundColor = color;
        Console.Write($"  [{index:D2}] {roleStr.ToUpperInvariant(),-10}");
        Console.ResetColor();

        string? text = msg.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine(text.Length > 80 ? text[..80] + "..." : text);
            return;
        }

        bool wroteContent = false;

        foreach (FunctionCallContent toolCall in msg.Contents.OfType<FunctionCallContent>())
        {
            string args = toolCall.Arguments is null
                ? "(no args)"
                : string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[tool_call] {toolCall.Name}({args})");
            Console.ResetColor();
            wroteContent = true;
        }

        foreach (FunctionResultContent toolResult in msg.Contents.OfType<FunctionResultContent>())
        {
            string result = toolResult.Result?.ToString() ?? "(null)";
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[tool_result] {(result.Length > 80 ? result[..80] + "..." : result)}");
            Console.ResetColor();
            wroteContent = true;
        }

        if (!wroteContent)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("(empty)");
            Console.ResetColor();
        }
    }
}
