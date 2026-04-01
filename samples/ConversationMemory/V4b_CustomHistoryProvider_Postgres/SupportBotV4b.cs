using DotNet.Testcontainers.Builders;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Npgsql;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testcontainers.PostgreSql;

namespace Samples.ConversationMemory.V4b_CustomHistoryProvider_Postgres;

// V4b: Custom ChatHistoryProvider — PostgreSQL via Testcontainers
//
// KEY CONCEPT: Same ChatHistoryProvider interface as V4a — the ONLY difference is
// the storage backend (file system → real PostgreSQL database).
//
// This sample shows how Testcontainers' ContainerBuilder (PostgreSqlBuilder) can
// spin up a Docker container programmatically:
//   - WithReuse(true): container persists after process exits — data survives restarts!
//   - Named container: "af-support-bot-postgres" is reused on subsequent runs
//   - No manual docker run needed — container lifecycle is code-driven
//
// Prerequisites: Docker Desktop must be running.
// On first run: pulls the postgres:16 image (~100MB). Subsequent runs: instant.
//
// Persisted JSON is normalized on write so each contents[] object has $type first (STJ polymorphic rule).
// Rows saved before that (or from other code) may throw on load — run: DELETE FROM chat_history;
// (or docker stop/rm the container). This sample does not repair legacy JSON on read.

public static class SupportBotV4b
{
    private const string ContainerName = "af-support-bot-postgres";
    private const string DbPassword = "password";

    public static async Task RunSample()
    {
        Output.Title("Support Bot V4b — Custom History Provider (PostgreSQL via Testcontainers)");
        Output.Separator();

        Output.Yellow("Starting (or reusing) PostgreSQL Docker container...");
        Output.Gray($"Container name: {ContainerName}  |  WithReuse: true  →  data persists between runs");

        // ─── Start / reuse the Docker container ──────────────────────────────────────
        // KEY: PostgreSqlBuilder is Testcontainers' ContainerBuilder for PostgreSQL.
        // WithReuse(true) tells Testcontainers NOT to destroy the container on dispose —
        // the container keeps running after the process exits, data intact.
        PostgreSqlContainer container = new PostgreSqlBuilder("postgres:15")
            .WithName(ContainerName)
            .WithPassword(DbPassword)
            .WithReuse(true)          // KEY: persistent container — survives process restarts
            .Build();

        await container.StartAsync();   // No-op if container is already running

        string connectionString = container.GetConnectionString();
        Output.Green($"PostgreSQL ready. Connection: {connectionString[..connectionString.IndexOf("Password")]}...");
        Output.Separator();

        // ─── Initialize the database table ────────────────────────────────────────────
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        await EnsureTableExistsAsync(dataSource);

        // ─── Create agent with PostgresChatHistoryProvider ───────────────────────────
        PostgresChatHistoryProvider postgresProvider = new(dataSource);

        static AIAgent CreateAgent(string apiKey, PostgresChatHistoryProvider provider) =>
            new OpenAIClient(apiKey)
                .GetChatClient("gpt-4.1-nano")
                .AsAIAgent(new ChatClientAgentOptions
                {
                    ChatOptions = new() { Instructions = "You are a helpful customer support agent. Be concise." },
                    ChatHistoryProvider = provider
                });

        string apiKey = SecretManager.GetOpenAIApiKey();

        // ─── Run 1: First conversation — saves to PostgreSQL ──────────────────────────
        Output.Yellow("RUN 1: First conversation (messages stored in PostgreSQL)");
        Output.Separator(false);

        AIAgent agent1 = CreateAgent(apiKey, postgresProvider);
        AgentSession session1 = await agent1.CreateSessionAsync();

        Output.Gray("User: Hi, I'm Carol. My case ID is C-555 — my printer won't connect to WiFi.");
        string r1 = (await agent1.RunAsync("Hi, I'm Carol. My case ID is C-555 — my printer won't connect to WiFi.", session1)).Text;
        Output.Green($"Bot:  {r1}");
        Console.WriteLine();

        Output.Gray("User: I've already tried restarting both devices.");
        string r2 = (await agent1.RunAsync("I've already tried restarting both devices.", session1)).Text;
        Output.Green($"Bot:  {r2}");

        // Get the session key stored in PostgreSQL
        PostgresChatHistoryProvider.State dbState = postgresProvider.GetState(session1);
        Output.Separator();
        Output.Blue($"Session key in PostgreSQL: {dbState.SessionKey}");
        Output.Gray($"Run: SELECT messages FROM chat_history WHERE session_key = '{dbState.SessionKey}';");

        // Serialize the session (session key travels inside the serialized session)
        JsonElement serialized = await agent1.SerializeSessionAsync(session1);
        string serializedJson = JsonSerializer.Serialize(serialized);

        Output.Separator();

        // ─── Run 2: Restore session — history loads from PostgreSQL ───────────────────
        Output.Yellow("RUN 2: Restore session (history loads from PostgreSQL)");
        Output.Gray("This simulates an app restart — the container is still running, data intact.");
        Output.Separator(false);

        PostgresChatHistoryProvider postgresProvider2 = new(dataSource);
        AIAgent agent2 = CreateAgent(apiKey, postgresProvider2);

        JsonElement restoredElement = JsonSerializer.Deserialize<JsonElement>(serializedJson);
        AgentSession session2 = await agent2.DeserializeSessionAsync(restoredElement);

        Output.Gray("User: Summarize my issue and what troubleshooting I've done.");
        string r3 = (await agent2.RunAsync("Summarize my issue and what troubleshooting I've done.", session2)).Text;
        Output.Green($"Bot:  {r3}");

        Output.Separator();
        Output.Yellow("KEY LEARNING: Same ChatHistoryProvider interface as V4a — only storage backend changed.");
        Output.Gray("The container keeps running after this process exits (WithReuse=true).");
        Output.Gray($"Stop it manually: docker stop {ContainerName}");
    }

    private static async Task EnsureTableExistsAsync(NpgsqlDataSource dataSource)
    {
        await using NpgsqlCommand cmd = dataSource.CreateCommand(@"
            CREATE TABLE IF NOT EXISTS chat_history (
                session_key TEXT PRIMARY KEY,
                messages    JSONB NOT NULL DEFAULT '[]'::jsonb,
                updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )");
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────
// Custom ChatHistoryProvider: PostgreSQL-backed implementation
// ─────────────────────────────────────────────────────────────────────────────────────────

public sealed class PostgresChatHistoryProvider : ChatHistoryProvider
{
    private readonly NpgsqlDataSource _dataSource;

    // KEY: Session-specific state (the DB row key) lives IN the session, not in this field
    private readonly ProviderSessionState<State> _sessionState = new(
        stateInitializer: _ => new State { SessionKey = Guid.NewGuid().ToString("N") },
        stateKey: nameof(PostgresChatHistoryProvider));

    public PostgresChatHistoryProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // Load messages from the database row for this session
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        State state = _sessionState.GetOrInitializeState(context.Session);

        await using NpgsqlCommand cmd = _dataSource.CreateCommand(
            "SELECT messages FROM chat_history WHERE session_key = $1");
        cmd.Parameters.AddWithValue(state.SessionKey);

        string? json = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        return DeserializeMessages(json);
    }

    // Upsert full message list for this session (same pattern as V4a file provider)
    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        State state = _sessionState.GetOrInitializeState(context.Session);

        await using NpgsqlCommand selectCmd = _dataSource.CreateCommand(
            "SELECT messages FROM chat_history WHERE session_key = $1");
        selectCmd.Parameters.AddWithValue(state.SessionKey);
        string? existingJson = (string?)await selectCmd.ExecuteScalarAsync(cancellationToken);

        List<ChatMessage> existing = DeserializeMessages(existingJson);
        existing.AddRange(context.RequestMessages);
        existing.AddRange(context.ResponseMessages ?? []);
        string json = JsonSerializer.Serialize(existing, AgentChatMessageJson.DefaultOptions);
        json = ChatHistoryJsonNormalizer.EnsurePolymorphicDiscriminatorFirst(json, AgentChatMessageJson.DefaultOptions);

        // Do not use jsonb || here: concatenation preserves raw legacy elements (wrong $type order) forever.
        // Always replace messages with one round-tripped blob — matches V4a load/append/serialize.
        await using NpgsqlCommand cmd = _dataSource.CreateCommand(@"
            INSERT INTO chat_history (session_key, messages, updated_at)
            VALUES ($1, $2::jsonb, now())
            ON CONFLICT (session_key) DO UPDATE
                SET messages   = excluded.messages,
                    updated_at = now()");
        cmd.Parameters.AddWithValue(state.SessionKey);
        cmd.Parameters.AddWithValue(json);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _sessionState.SaveState(context.Session, state);
    }

    private static List<ChatMessage> DeserializeMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<ChatMessage>>(json, AgentChatMessageJson.DefaultOptions) ?? [];
    }

    // Helper to read the session key for display purposes
    public State GetState(AgentSession session) => _sessionState.GetOrInitializeState(session);

    public sealed class State
    {
        [JsonPropertyName("sessionKey")]
        public required string SessionKey { get; set; }
    }
}
