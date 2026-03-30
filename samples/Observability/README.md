# Observability — Jaeger Tracing (Sample 1100)

This sample demonstrates how to add **distributed tracing** to an agent using [OpenTelemetry](https://opentelemetry.io/) and visualize the traces in [Jaeger](https://www.jaegertracing.io/).

## What it shows

- Starting a local Jaeger instance automatically via [Testcontainers](https://dotnet.testcontainers.org/)
- Configuring an OpenTelemetry `TracerProvider` with an OTLP exporter
- Instrumenting the `IChatClient` pipeline with `.UseOpenTelemetry()` so every LLM call is captured as a trace span
- Creating a root span manually with `ActivitySource` to wrap the entire agent run

## Prerequisites

- **Docker** must be running (Testcontainers pulls and starts the Jaeger image automatically)
- An OpenAI API key stored in [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) under the key `OpenAIApiKey`

## How to run

Select sample **1100** from the interactive menu, or set `Sample.JaegerTracing` in `Program.cs`.

```
Running sample: JaegerTracing
Starting Jaeger for trace visualization...
Jaeger UI: http://localhost:16686
Ask the agent a question (traces will appear in Jaeger):
> What is the capital of France?
```

Once the agent responds, open your browser at **http://localhost:16686**, select the service **JaegerTracingDemo**, and click **Find Traces** to see the spans.

Press **Enter** to stop the Jaeger container and exit.

## Key packages

| Package | Purpose |
|---|---|
| `OpenTelemetry` | Core OTel SDK |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | OTLP exporter (gRPC to Jaeger) |
| `Testcontainers` | Spin up the Jaeger Docker container in-process |
