# Structured Output (OpenAI Step02)

This sample ports the Agent Framework Structured Output step to this playground's OpenAI setup.

## What this sample demonstrates

- Response format with JSON schema using `ChatResponseFormat.ForJsonSchema<T>()`.
- Strongly typed output with `RunAsync<T>()`.
- Structured output while streaming with `RunStreamingAsync()`.
- Structured output middleware for agents/models that do not natively support response formats.

## Prerequisites

- .NET 8 SDK or later.
- OpenAI API key configured via user secrets or environment configuration used by `SecretManager`.

## Required configuration

Set `OpenAIApiKey` in user secrets for the `samples` project:

```powershell
cd samples
dotnet user-secrets set "OpenAIApiKey" "your-openai-api-key"
```

## Run via SampleConsoleRunner

```powershell
cd SampleConsoleRunner
dotnet run
```

Then select:

- `602: Structured Output (OpenAI Step02)`

## Expected behavior

The console demonstrates four approaches and prints structured city info for the capital of France, including JSON text and typed-object output.
