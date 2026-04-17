using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Samples.SampleUtilities;
using System.ComponentModel;

namespace Samples.Labs.ConversationMemory.V8_ToolCallHistory;

// V8: Tool Calls in Conversation History
//
// KEY CONCEPT: When an agent invokes a tool, the framework records multiple message
// types in the conversation history — not just user and assistant messages.
// A single tool-calling turn produces up to FOUR message types:
//
//   1. ChatRole.User      — the user's original message
//   2. ChatRole.Assistant — the LLM's decision to call a tool (with call ID + arguments)
//   3. ChatRole.Tool      — the tool's return value (keyed to the call ID)
//   4. ChatRole.Assistant — the LLM's final response after seeing the tool result
//
// This sample makes the invisible visible:
//   Part 1 — Run a tool-calling conversation and print the RAW history, showing every
//             message type, role, and content (including tool call arguments and results)
//   Part 2 — Show how ChatHistoryProvider.StoreChatHistoryAsync receives these messages
//             and how ToolResultCompactionStrategy collapses old tool call groups
//
// WHY THIS MATTERS:
//   - Custom ChatHistoryProviders must handle ALL message roles, not just User/Assistant
//   - Compaction's "group" concept maps to these multi-message tool-calling turns
//   - Understanding this structure is essential for building reliable history storage

public static class SupportBotV8
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V8 — Tool Calls in Conversation History");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new(apiKey);
        IChatClient chatClient = client.GetChatClient("gpt-4.1-nano").AsIChatClient();

        // ─── Define tools ────────────────────────────────────────────────────────────
        // A simple ticket lookup tool that returns deterministic data
        AIFunction lookupTicket = AIFunctionFactory.Create(
            ([Description("The support ticket ID to look up, e.g. T-1001")] string ticketId) =>
            {
                Output.Gray($"  [tool invoked] LookupTicket({ticketId})");
                return ticketId switch
                {
                    "T-1001" => "Ticket T-1001: 'Laptop screen flicker' — Status: Open, Priority: High, Assigned: Alice",
                    "T-1002" => "Ticket T-1002: 'VPN connection drops' — Status: In Progress, Priority: Medium, Assigned: Bob",
                    _ => $"Ticket {ticketId}: Not found"
                };
            },
            name: "LookupTicket",
            description: "Look up the details of a support ticket by ID");

        AIFunction getQueueStatus = AIFunctionFactory.Create(
            () =>
            {
                Output.Gray("  [tool invoked] GetQueueStatus()");
                return "Current queue: 12 open tickets, avg response time 2.4 hours, 3 agents online";
            },
            name: "GetQueueStatus",
            description: "Get the current support queue status");

        // ─── Build agent with in-memory history so we can inspect it ─────────────────
        AIAgent agent = chatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SupportBot",
                ChatOptions = new()
                {
                    Instructions = "You are a helpful support agent. Use LookupTicket and GetQueueStatus tools when needed. Be concise.",
                    Tools = [lookupTicket, getQueueStatus]
                }
            });

        AgentSession session = await agent.CreateSessionAsync();

        // ─── PART 1: Run a conversation that triggers tool calls ──────────────────────
        Output.Yellow("PART 1: Running a conversation that triggers tool calls");
        Output.Separator(false);

        Output.Gray("User: Can you look up ticket T-1001 for me?");
        string r1 = (await agent.RunAsync("Can you look up ticket T-1001 for me?", session)).Text;
        Output.Green($"Bot:  {r1}");
        Console.WriteLine();

        Output.Gray("User: Also check T-1002 and tell me how busy the queue is.");
        string r2 = (await agent.RunAsync("Also check T-1002 and tell me how busy the queue is.", session)).Text;
        Output.Green($"Bot:  {r2}");
        Console.WriteLine();

        Output.Gray("User: Based on their priorities, which should I handle first?");
        string r3 = (await agent.RunAsync("Based on their priorities, which should I handle first?", session)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Separator();

        // ─── PART 2: Inspect the raw conversation history ─────────────────────────────
        Output.Yellow("PART 2: Raw conversation history — every message including tool calls");
        Output.Separator(false);

        // KEY: GetService<InMemoryChatHistoryProvider>() retrieves the history provider
        InMemoryChatHistoryProvider? historyProvider = agent.GetService<InMemoryChatHistoryProvider>();
        IList<ChatMessage> allMessages = historyProvider?.GetMessages(session) ?? [];

        Output.Blue($"Total messages stored: {allMessages.Count}");
        Output.Gray("(A single tool-calling turn produces: User → Assistant[tool_call] → Tool[result] → Assistant[final])");
        Console.WriteLine();

        // Print each message with full detail
        for (int i = 0; i < allMessages.Count; i++)
        {
            ChatMessage msg = allMessages[i];
            PrintMessage(i + 1, msg);
        }

        Output.Separator();

        // ─── PART 3: Explain the turn structure ────────────────────────────────────────
        Output.Yellow("PART 3: Message role breakdown");
        Output.Separator(false);

        var byRole = allMessages
            .GroupBy(m => m.Role.ToString())
            .Select(g => (Role: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count);

        foreach (var (role, count) in byRole)
        {
            string explanation = role switch
            {
                "user"      => "← user input",
                "assistant" => "← LLM response (may contain tool_call requests OR final answer)",
                "tool"      => "← tool return value (paired to an assistant tool_call by ID)",
                _           => ""
            };
            Output.Blue($"  {role,-12} {count,2} messages  {explanation}");
        }

        Console.WriteLine();
        Output.Yellow("KEY LEARNING: Tool calls produce a 4-message pattern per tool:");
        Output.Gray("  1. User      — the original request");
        Output.Gray("  2. Assistant — the tool_call decision (with arguments)");
        Output.Gray("  3. Tool      — the tool result (matched by call ID)");
        Output.Gray("  4. Assistant — the final answer using the tool result");
        Console.WriteLine();
        Output.Gray("Custom ChatHistoryProviders must persist ALL four roles.");
        Output.Gray("ToolResultCompactionStrategy collapses messages 2+3 into '[Tool calls: LookupTicket]'");
        Output.Gray("when the history grows large — preserving the answer (message 4) but dropping the raw data.");
    }

    private static void PrintMessage(int index, ChatMessage msg)
    {
        string roleLabel = msg.Role.ToString().ToUpperInvariant();
        ConsoleColor color = msg.Role.ToString() switch
        {
            "user"      => ConsoleColor.Cyan,
            "assistant" => ConsoleColor.Green,
            "tool"      => ConsoleColor.Yellow,
            _           => ConsoleColor.Gray
        };

        Console.ForegroundColor = color;
        Console.Write($"  [{index:D2}] {roleLabel,-10}");
        Console.ResetColor();

        // Show content — tool messages and assistant tool_call messages have structured content
        string? text = msg.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            string preview = text.Length > 120 ? text[..120] + "..." : text;
            Console.WriteLine(preview);
        }
        else
        {
            // Assistant message with tool calls (no text content — just structured call request)
            IEnumerable<AIContent> toolCallContents = msg.Contents.OfType<FunctionCallContent>();
            foreach (FunctionCallContent toolCall in toolCallContents.Cast<FunctionCallContent>())
            {
                string args = toolCall.Arguments is null
                    ? "(no args)"
                    : string.Join(", ", toolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[tool_call] {toolCall.Name}({args})  callId={toolCall.CallId}");
                Console.ResetColor();
            }

            // Tool result messages
            IEnumerable<AIContent> toolResultContents = msg.Contents.OfType<FunctionResultContent>();
            foreach (FunctionResultContent toolResult in toolResultContents.Cast<FunctionResultContent>())
            {
                string result = toolResult.Result?.ToString() ?? "(null)";
                string preview = result.Length > 120 ? result[..120] + "..." : result;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[tool_result] callId={toolResult.CallId}  result={preview}");
                Console.ResetColor();
            }

            if (!toolCallContents.Any() && !toolResultContents.Any())
                Console.WriteLine("(empty)");
        }
    }
}
