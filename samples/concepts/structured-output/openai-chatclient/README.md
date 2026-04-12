# Structured Output (OpenAI Step02)

This sample ports the Agent Framework Structured Output step to this playground's OpenAI setup.

## What this sample demonstrates

### ResponseFormat with non-generic `RunAsync`

Structured output is configured with `ChatResponseFormat.ForJsonSchema<CityInfo>()` and consumed via the non-generic `RunAsync` method, then optionally deserialized from `response.Text`.

This approach is useful when:

- Structured output is used for inter-agent communication, where one agent produces structured output and passes it as text to another agent as input, without the need for the caller to directly work with the structured output.
- The type of the structured output is not known at compile time, so the generic `RunAsync<T>` method cannot be used.
- The type of the structured output is represented by JSON schema only, without a corresponding class or type in the code.

### Generic `RunAsync<T>`

The caller uses `RunAsync<CityInfo>()` and reads the typed result from `response.Result`.

This approach is useful when the caller needs to directly work with the structured output in the code via an instance of the corresponding class or type and the type is known at compile time.

### `RunStreamingAsync`

The agent streams tokens; updates are assembled with `ToAgentResponseAsync()`, and structured JSON is read from `Text` and deserialized when needed.

### Structured output middleware (`UseStructuredOutput`)

Adds structured output support by transforming text responses into structured JSON using a chat client. This is useful when working with agents that do not support structured output natively, or models that cannot produce structured output directly, so you still get structured data by post-processing text.

This demo removes `ResponseFormat` in middleware to emulate an agent that does not natively support structured output (see `ResponseFormatRemovalMiddleware` in code).

## See also

Upstream Azure OpenAI variant: `dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput` in the [Agent Framework](https://github.com/microsoft/agent-framework) repository.

## Prerequisites

- .NET 8 SDK or later.
- OpenAI API key configured via user secrets or environment configuration used by `SecretManager`.

## Required configuration

Set `OpenAIApiKey` in user secrets for **Learn.Shared** (shared by all samples):

```powershell
dotnet user-secrets set "OpenAIApiKey" "your-openai-api-key" --project src/Learn.Shared/Learn.Shared.csproj
```

## Run this project

From the repository root:

```powershell
dotnet run --project samples/concepts/structured-output/openai-chatclient/StructuredOutputOpenAI.csproj
```

## Expected behavior

The console demonstrates four approaches and prints structured city info for the capital of France, including JSON text and typed-object output.
