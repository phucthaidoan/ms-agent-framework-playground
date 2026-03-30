using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.CV_Screening.V1_AgentAsTool;

// V1: Agent-as-Tool
// New concept: screenerAgent.AsAIFunction() — wrapping one agent as a tool for another.
// Instead of two sequential RunAsync calls in host code, a single coordinator agent
// orchestrates everything: it calls screen_cv tool, then decides whether to notify.

public static class CvScreeningV1
{
    public static async Task RunSample()
    {
        Output.Title("CV Screening V1 — Agent as Tool");
        Output.Separator();

        Output.Title("Job Description");
        Console.WriteLine("Role    : Senior C# Developer");
        Console.WriteLine("Requires: 5+ years experience, Azure, .NET, REST APIs");
        Console.WriteLine("Bonus   : AI/ML experience");
        Output.Separator();

        // --- Step 1: Collect CV input ---
        Console.WriteLine("Paste the candidate's CV below. Press Enter twice (empty line) when done:");
        Console.WriteLine();

        var lines = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = Console.ReadLine()))
            lines.Add(line);

        string cvText = string.Join("\n", lines);

        if (string.IsNullOrWhiteSpace(cvText))
        {
            Output.Red("No CV text provided. Exiting.");
            return;
        }

        // --- Step 2: Create screener agent and wrap it as a tool ---
        Output.Separator();
        Output.Title("Setting up agents...");

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        ChatClientAgent screenerAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                name: "screen_cv",   // name is used as the tool name when calling AsAIFunction()
                instructions:
                    "You are a Talent Acquisition specialist. " +
                    "Evaluate the provided CV against this job description: " +
                    "'Senior C# Developer — 5+ years experience, Azure, .NET, REST APIs required. Bonus: AI/ML experience.' " +
                    "Be strict but fair. Extract the candidate's full name. " +
                    "Respond with: candidate name, qualified (yes/no), summary, matched criteria, missing criteria.");

        // KEY CONCEPT: wrap the screener agent as an AIFunction tool — no params, name comes from agent.Name
        AIFunction screenerTool = screenerAgent.AsAIFunction();

        // --- Step 3: Create coordinator with both tools ---
        ChatClientAgent coordinatorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
                    "You are an interview coordinator. Your job: " +
                    "1. Call screen_cv with the CV text to get the screening result. " +
                    "2. If the candidate is NOT qualified, report that and stop. " +
                    "3. If the candidate IS qualified, call notify_interviewer exactly once for each of: " +
                    "   Alice (Tech Lead), Bob (HR), Carol (CTO). " +
                    "   Write a short personalized message per interviewer relevant to their role. " +
                    "4. Finally, summarise what you did.",
                tools:
                [
                    screenerTool,
                    AIFunctionFactory.Create(
                        NotifyInterviewer,
                        "notify_interviewer",
                        "Send an interview preparation notification to an interviewer")
                ]);

        // --- Step 4: Single entry point — coordinator does everything ---
        Output.Title("Running coordinator (single entry point)...");
        Output.Separator();

        AgentSession session = await coordinatorAgent.CreateSessionAsync();
        AgentResponse response = await coordinatorAgent.RunAsync(cvText, session);

        Output.Separator();
        Output.Gray("Coordinator summary:");
        Console.WriteLine(response.Text);
    }

    private static void NotifyInterviewer(string interviewerName, string candidateName, string message)
    {
        Output.Green($"[NOTIFICATION] {interviewerName}: {message} (Candidate: {candidateName})");
    }
}
