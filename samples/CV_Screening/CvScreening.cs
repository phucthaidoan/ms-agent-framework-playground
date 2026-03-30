using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.CV_Screening;

public static class CvScreening
{
    public static async Task RunSample()
    {
        Output.Title("CV Screening & Interview Coordinator");
        Output.Separator();

        Output.Title("Job Description");
        Console.WriteLine("Role    : Senior C# Developer");
        Console.WriteLine("Requires: 5+ years experience, Azure, .NET, REST APIs");
        Console.WriteLine("Bonus   : AI/ML experience");
        Output.Separator();

        // --- Step 1: Collect CV input from user ---
        Console.WriteLine("Paste the candidate's CV below. Press Enter twice (empty line) when done:");
        Console.WriteLine();

        var lines = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = Console.ReadLine()))
        {
            lines.Add(line);
        }
        string cvText = string.Join("\n", lines);

        if (string.IsNullOrWhiteSpace(cvText))
        {
            Output.Red("No CV text provided. Exiting.");
            return;
        }

        // --- Step 2: Screen the CV ---
        Output.Separator();
        Output.Title("Step 1: Screening CV...");

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        ChatClientAgent screenerAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                "You are a Talent Acquisition specialist. " +
                "Evaluate the provided CV against this job description: " +
                "'Senior C# Developer — 5+ years experience, Azure, .NET, REST APIs required. Bonus: AI/ML experience.' " +
                "Be strict but fair. Extract the candidate's full name and return a structured evaluation.");

        AgentResponse<ScreeningResult> screeningResponse = await screenerAgent.RunAsync<ScreeningResult>(cvText);
        ScreeningResult result = screeningResponse.Result;

        // --- Step 3: Print screening result ---
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

        // --- Step 4: Gate on qualification ---
        if (!result.IsQualified)
        {
            Output.Separator();
            Output.Yellow("Candidate is not suitable for this role. No further action.");
            return;
        }

        // --- Step 5: Notify interviewers ---
        Output.Separator();
        Output.Title("Step 2: Notifying interviewers...");

        ChatClientAgent coordinatorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
                    "You are an interview coordinator. " +
                    "You must notify ALL of the following interviewers about the upcoming interview: " +
                    "Alice (Tech Lead), Bob (HR), Carol (CTO). " +
                    "Call the notify_interviewer tool exactly once per interviewer with a short, personalized message " +
                    "relevant to their role. Do not skip any interviewer.",
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
            $"Summary: {result.Summary} " +
            $"Please notify all three interviewers now.";

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
