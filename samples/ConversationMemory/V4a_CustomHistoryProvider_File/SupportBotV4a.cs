using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Samples.ConversationMemory.V4a_CustomHistoryProvider_File;

// V4a: Custom ChatHistoryProvider — File-backed Storage
//
// KEY CONCEPT: ChatHistoryProvider is the extension point for changing WHERE messages
// are stored. The interface has two core methods:
//   - ProvideChatHistoryAsync(): load history from storage before each LLM call
//   - StoreChatHistoryAsync(): save new messages to storage after each LLM call
//
// This sample uses a JSON file as the storage backend — the simplest possible real
// backend that requires no external services.
//
// KEY PATTERN: ProviderSessionState<T>
//   - The provider INSTANCE is shared across ALL sessions
//   - Session-specific data (like a file path) MUST go inside the session, not the provider
//   - ProviderSessionState<T> is a typed helper that reads/writes from the AgentSession

public static class SupportBotV4a
{
    public static async Task RunSample()
    {
        Output.Title("Support Bot V4a — Custom History Provider (File-backed)");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();

        // Create our custom file-backed provider
        FileBackedChatHistoryProvider fileProvider = new();

        AIAgent agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new() { Instructions = "You are a helpful customer support agent. Be concise." },
                // KEY: Replace the default InMemoryChatHistoryProvider with our custom one
                ChatHistoryProvider = fileProvider
            });

        // ─── Run 1: First conversation — creates the history file ─────────────────────
        Output.Yellow("RUN 1: First conversation (creates history file)");
        Output.Separator(false);

        AgentSession session1 = await agent.CreateSessionAsync();

        Output.Gray("User: My name is Bob and my ticket number is T-9999.");
        string r1 = (await agent.RunAsync("My name is Bob and my ticket number is T-9999.", session1)).Text;
        Output.Green($"Bot:  {r1}");
        Console.WriteLine();

        Output.Gray("User: I ordered a laptop that hasn't arrived yet.");
        string r2 = (await agent.RunAsync("I ordered a laptop that hasn't arrived yet.", session1)).Text;
        Output.Green($"Bot:  {r2}");

        // Get the file path from session state so we can print it
        FileBackedChatHistoryProvider.State state1 = fileProvider.GetState(session1);
        Output.Separator();
        Output.Blue($"History saved to: {state1.FilePath}");
        Output.Gray("You can open this file to inspect the stored JSON messages.");

        // Serialize the session so we can restore it in Run 2
        JsonElement serialized = await agent.SerializeSessionAsync(session1);
        string serializedJson = JsonSerializer.Serialize(serialized);

        Output.Separator();

        // ─── Run 2: Restore session — history loads from the same file ────────────────
        Output.Yellow("RUN 2: Restore session — history loads from file (simulating restart)");
        Output.Separator(false);

        // Recreate the agent with the same provider type
        AIAgent agent2 = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(new ChatClientAgentOptions
            {
                ChatOptions = new() { Instructions = "You are a helpful customer support agent. Be concise." },
                ChatHistoryProvider = new FileBackedChatHistoryProvider()
            });

        // Restore the session — the file path stored in session state points to the saved history
        JsonElement restoredElement = JsonSerializer.Deserialize<JsonElement>(serializedJson);
        AgentSession session2 = await agent2.DeserializeSessionAsync(restoredElement);

        Output.Gray("User: Can you remind me of my name and ticket number?");
        string r3 = (await agent2.RunAsync("Can you remind me of my name and ticket number?", session2)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Separator();
        Output.Yellow("KEY LEARNING: The ChatHistoryProvider abstraction decouples storage from agent logic.");
        Output.Gray("Same interface → swap backend (file, database, Redis) with zero agent code changes.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────
// Custom ChatHistoryProvider: File-backed implementation
// ─────────────────────────────────────────────────────────────────────────────────────────

public sealed class FileBackedChatHistoryProvider : ChatHistoryProvider
{
    // KEY: ProviderSessionState<T> stores per-session data INSIDE the session, not in this field.
    // The provider instance is shared — instance fields must NOT be session-specific.
    private readonly ProviderSessionState<State> _sessionState = new(
        stateInitializer: _ => new State
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"af-support-history-{Guid.NewGuid():N}.json")
        },
        stateKey: nameof(FileBackedChatHistoryProvider));

    // KEY: StateKeys must be overridden so the framework knows how to serialize/deserialize
    // this provider's portion of the session state
    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // Load history from the file before each LLM call
    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        State state = _sessionState.GetOrInitializeState(context.Session);

        if (!File.Exists(state.FilePath))
        {
            // First turn: no file yet — return empty history
            return new ValueTask<IEnumerable<ChatMessage>>([]);
        }

        string json = File.ReadAllText(state.FilePath);
        // KEY: Must use AgentChatMessageJson.DefaultOptions — ChatMessage.Contents is polymorphic
        // (TextContent, FunctionCallContent, FunctionResultContent, etc. all use $type discriminators).
        // Plain JsonSerializerOptions throws if $type is not first or contracts don't match.
        List<ChatMessage> messages = JsonSerializer.Deserialize<List<ChatMessage>>(json, AgentChatMessageJson.DefaultOptions) ?? [];
        return new ValueTask<IEnumerable<ChatMessage>>(messages);
    }

    // Append new messages to the file after each LLM call
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        State state = _sessionState.GetOrInitializeState(context.Session);

        // KEY: Load existing history first, then APPEND — never overwrite.
        // context.RequestMessages = only the new messages from THIS turn (not the full history)
        // context.ResponseMessages = the assistant's response(s) for this turn
        List<ChatMessage> existing = [];
        if (File.Exists(state.FilePath))
        {
            string existingJson = File.ReadAllText(state.FilePath);
            existing = JsonSerializer.Deserialize<List<ChatMessage>>(existingJson, AgentChatMessageJson.DefaultOptions) ?? [];
        }

        existing.AddRange(context.RequestMessages);
        existing.AddRange(context.ResponseMessages ?? []);
        File.WriteAllText(state.FilePath, JsonSerializer.Serialize(existing, AgentChatMessageJson.DefaultOptions));

        // Persist the updated state (file path) back into the session
        _sessionState.SaveState(context.Session, state);
        return default;
    }

    // Helper to let the sample read the file path for display purposes
    public State GetState(AgentSession session) => _sessionState.GetOrInitializeState(session);

    public sealed class State
    {
        [JsonPropertyName("filePath")]
        public required string FilePath { get; set; }
    }
}
