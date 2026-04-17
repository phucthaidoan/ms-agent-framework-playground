# V4b — Custom ChatHistoryProvider (PostgreSQL via Testcontainers)

## What You'll Learn

> There is bug in deserialization:  $type discriminator must be first in JSON or deserialization throws

The same `ChatHistoryProvider` interface as V4a, but backed by a real PostgreSQL database managed by **Testcontainers** — with a persistent container that survives process restarts.

## Key Concept: Testcontainers ContainerBuilder

`PostgreSqlBuilder` is Testcontainers' `ContainerBuilder` for PostgreSQL. The key option is `WithReuse(true)`:

```csharp
PostgreSqlContainer container = new PostgreSqlBuilder()
    .WithName("af-support-bot-postgres")   // named → reusable across runs
    .WithPassword("password")
    .WithReuse(true)                        // 🔑 container stays alive after process exits
    .Build();

await container.StartAsync();              // no-op if already running
string connectionString = container.GetConnectionString();
```

With `WithReuse(true)`:
- First run: pulls the image and starts the container
- Subsequent runs: finds the existing named container, reuses it instantly
- Data in the database persists between runs

## Provider Implementation

```csharp
public sealed class PostgresChatHistoryProvider : ChatHistoryProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ProviderSessionState<State> _sessionState = new(
        _ => new State { SessionKey = Guid.NewGuid().ToString("N") },
        nameof(PostgresChatHistoryProvider));

    public override string StateKey => _sessionState.StateKey;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        // Load messages from PostgreSQL for this session's key
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken ct = default)
    {
        // UPSERT messages into PostgreSQL (append on each turn)
    }
}
```

## Table Schema

```sql
CREATE TABLE IF NOT EXISTS chat_history (
    session_key TEXT PRIMARY KEY,
    messages    JSONB NOT NULL DEFAULT '[]',
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

Messages are stored as a JSONB array — queryable with PostgreSQL JSON operators.

## Prerequisites

- Docker Desktop must be running
- First run pulls `postgres:16` (~100MB automatically)

## Inspect the Data

The sample prints the session key. Query it directly:

```sql
SELECT session_key, jsonb_array_length(messages) as msg_count, updated_at
FROM chat_history;
```

## Stop the Container

```bash
docker stop af-support-bot-postgres
docker rm af-support-bot-postgres
```

## Running This Sample

```
Enter sample number: 1204
```
