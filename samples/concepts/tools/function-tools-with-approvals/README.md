# Function Tools with Approvals (Human-in-the-Loop)

Demonstrates how to require human approval before the agent executes a tool call, using `ApprovalRequiredAIFunction` with a `ChatClientAgent` backed by the OpenAI API.

## What You'll Learn

How to implement a **Human-in-the-Loop (HITL)** approval gate so that the agent must ask the user before invoking any tool.

## Key Concept

Wrap any `AIFunction` with `ApprovalRequiredAIFunction`. Instead of invoking the function immediately, the framework intercepts the tool call and returns a `FunctionApprovalRequestContent` item in the response. The host must then send back an approval or rejection before the agent can proceed.

```csharp
// Without approval — function fires automatically
tools: [AIFunctionFactory.Create(GetWeather)]

// With approval — agent pauses and waits for human input
tools: [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather))]
```

## Approval Flow

```
User query
    ↓
agent.RunAsync(query, session)
    ↓
Response contains FunctionApprovalRequestContent
    ↓
Host prompts: "Approve GetWeather? (Y/N)"
    ↓
    ├── Y → CreateResponse(approved: true)  → agent executes the function → final answer
    └── N → CreateResponse(approved: false) → agent is told it was rejected → explains why
```

The loop repeats if the agent requests multiple tool calls across turns.

## Code Walk-Through

```csharp
// 1. Wrap the tool with an approval gate
ChatClientAgent agent = client
    .GetChatClient("gpt-4.1-nano")
    .AsAIAgent(
        instructions: "...",
        tools: [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GetWeather))]);

// 2. Run — response will contain approval requests instead of results
AgentResponse response = await agent.RunAsync("What is the weather like in Amsterdam?", session);

// 3. Collect pending approvals
List<FunctionApprovalRequestContent> approvalRequests = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<FunctionApprovalRequestContent>()
    .ToList();

// 4. Prompt the user and pass responses back to the agent
while (approvalRequests.Count > 0)
{
    List<ChatMessage> userInputResponses = approvalRequests.ConvertAll(request =>
    {
        bool approved = /* ask user */;
        return new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);
    });

    response = await agent.RunAsync(userInputResponses, session);
    approvalRequests = /* check for more */;
}
```

## Key Types

| Type | Purpose |
|---|---|
| `ApprovalRequiredAIFunction` | Wraps an `AIFunction` to require human approval before execution |
| `FunctionApprovalRequestContent` | Content item in the agent response carrying a pending approval request |
| `request.FunctionCall.Name` | The name of the tool the agent wants to call |
| `request.CreateResponse(bool)` | Creates the approval (`true`) or rejection (`false`) response to send back |
| `AgentSession` | Maintains conversation history so the approval exchange is part of the same turn |

## Relation to AgentMiddleware Sample

This sample shows the approval pattern applied directly in the host loop. The [AgentMiddleware sample (1300)](../AgentPipeline/Middleware/README.md) shows the same approval logic encapsulated inside a reusable middleware layer (`ConsolePromptingApprovalMiddleware`), which is the preferred approach for production code.

## Prerequisites

An OpenAI API key stored in user secrets under the key expected by `SecretManager.GetOpenAIApiKey()`.

## Running This Sample

```
Enter sample number: 506
```
