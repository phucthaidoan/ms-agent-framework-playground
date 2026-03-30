using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.CV_Screening.V3_Streaming;

// V3: Streaming Output
// New concept: RunStreamingAsync + AgentResponseUpdate — instead of waiting for a complete
// response, tokens stream to the console as they arrive (like ChatGPT typing effect).
// This sample shows both: streaming for the narrative, then RunAsync<T> for structured data.

public static class CvScreeningV3
{
    public static async Task RunSample()
    {
        Output.Title("CV Screening V3 — Streaming Output");
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

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // --- Step 2: Stream narrative analysis ---
        Output.Separator();
        Output.Title("Step 1: Streaming narrative analysis...");
        Console.WriteLine();

        ChatClientAgent narrativeAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                "You are a Talent Acquisition specialist. " +
                "Write a detailed narrative analysis of this CV against the job description: " +
                "'Senior C# Developer — 5+ years experience, Azure, .NET, REST APIs required. Bonus: AI/ML experience.' " +
                "Cover: candidate background, strengths, gaps, and overall impression. " +
                "Write in flowing prose, 3-5 paragraphs.");

        // KEY CONCEPT: RunStreamingAsync — each AgentResponseUpdate is a chunk of tokens
        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in narrativeAgent.RunStreamingAsync(cvText))
        {
            updates.Add(update);
            Console.Write(update);  // tokens printed as they arrive
        }
        Console.WriteLine();

        // --- Step 3: Structured verdict (separate call, same CV) ---
        Output.Separator();
        Output.Title("Step 2: Extracting structured verdict...");

        ChatClientAgent structuredAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                "You are a Talent Acquisition specialist. " +
                "Evaluate the provided CV against this job description: " +
                "'Senior C# Developer — 5+ years experience, Azure, .NET, REST APIs required. Bonus: AI/ML experience.' " +
                "Be strict but fair. Extract the candidate's full name and return a structured evaluation.");

        AgentResponse<ScreeningResult> screeningResponse = await structuredAgent.RunAsync<ScreeningResult>(cvText);
        ScreeningResult result = screeningResponse.Result;

        // --- Step 4: Print structured verdict ---
        Output.Separator();
        Console.WriteLine($"Candidate : {result.CandidateName}");
        Console.WriteLine($"Qualified : {(result.IsQualified ? "YES" : "NO")}");
        Console.WriteLine($"Summary   : {result.Summary}");

        if (result.MatchedCriteria.Count > 0)
        {
            Output.Green("Matched criteria:");
            foreach (string criteria in result.MatchedCriteria)
                Output.Green($"  + {criteria}");
        }

        if (result.MissingCriteria.Count > 0)
        {
            Output.Red("Missing criteria:");
            foreach (string criteria in result.MissingCriteria)
                Output.Red($"  - {criteria}");
        }

        // --- Step 5: Notify if qualified ---
        if (!result.IsQualified)
        {
            Output.Separator();
            Output.Yellow("Candidate is not suitable for this role. No further action.");
            return;
        }

        Output.Separator();
        Output.Title("Step 3: Notifying interviewers...");

        ChatClientAgent coordinatorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
                    "You are an interview coordinator. " +
                    "You must notify ALL of the following interviewers: Alice (Tech Lead), Bob (HR), Carol (CTO). " +
                    "Call the notify_interviewer tool exactly once per interviewer with a short personalized message. " +
                    "Do not skip any interviewer.",
                tools:
                [
                    AIFunctionFactory.Create(
                        NotifyInterviewer,
                        "notify_interviewer",
                        "Send an interview preparation notification to an interviewer")
                ]);

        AgentSession session = await coordinatorAgent.CreateSessionAsync();
        string coordinatorPrompt =
            $"The candidate {result.CandidateName} has passed screening and is qualified for interview. " +
            $"Summary: {result.Summary} Please notify all three interviewers now.";

        await coordinatorAgent.RunAsync(coordinatorPrompt, session);

        Output.Separator();
        Output.Green("All interviewers have been notified.");
    }

    private static void NotifyInterviewer(string interviewerName, string candidateName, string message)
    {
        Output.Green($"[NOTIFICATION] {interviewerName}: {message} (Candidate: {candidateName})");
    }

    private class ScreeningResult
    {
        public required bool IsQualified { get; set; }
        public required string CandidateName { get; set; }
        public required string Summary { get; set; }
        public required List<string> MatchedCriteria { get; set; }
        public required List<string> MissingCriteria { get; set; }
    }
}
