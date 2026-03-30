using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.CV_Screening.V4_MultiRole;

// V4: Multiple Job Roles — Dynamic Instructions
// New concept: instructions composed at runtime from data.
// The same agent is created with different system prompts depending on which role
// the user selects, demonstrating that agent behaviour is driven by instructions-as-data.

public static class CvScreeningV4
{
    // KEY CONCEPT: role definition as a data record — instructions are built from this
    private record JobRole(string Title, string Requirements, string[] Interviewers);

    public static async Task RunSample()
    {
        Output.Title("CV Screening V4 — Multiple Job Roles (Dynamic Instructions)");
        Output.Separator();

        // --- Step 1: Pick a job role ---
        JobRole[] roles =
        [
            new("Senior C# Developer",
                "5+ years C#, Azure, .NET, REST APIs required. Bonus: AI/ML experience.",
                ["Alice (Tech Lead)", "Bob (HR)", "Carol (CTO)"]),

            new("QA Engineer",
                "3+ years test automation, Selenium or Playwright, CI/CD pipelines required. Bonus: performance testing.",
                ["Dave (QA Lead)", "Bob (HR)", "Eve (Engineering Manager)"]),

            new("Product Manager",
                "3+ years PM experience, Agile/Scrum, stakeholder management required. Bonus: technical background.",
                ["Frank (CPO)", "Bob (HR)", "Grace (UX Lead)"])
        ];

        Console.WriteLine("Select a job role to screen against:");
        for (int i = 0; i < roles.Length; i++)
        {
            Console.WriteLine($"  {i + 1}. {roles[i].Title}");
            Console.WriteLine($"     Requires: {roles[i].Requirements}");
        }

        Console.WriteLine();
        Console.Write("Enter role number (1-3): ");
        string? choice = Console.ReadLine();

        if (!int.TryParse(choice, out int roleIndex) || roleIndex < 1 || roleIndex > roles.Length)
        {
            Output.Red("Invalid selection. Exiting.");
            return;
        }

        JobRole selectedRole = roles[roleIndex - 1];

        Output.Separator();
        Output.Title("Selected Job Description");
        Console.WriteLine($"Role    : {selectedRole.Title}");
        Console.WriteLine($"Requires: {selectedRole.Requirements}");
        Console.WriteLine($"Panel   : {string.Join(", ", selectedRole.Interviewers)}");
        Output.Separator();

        // --- Step 2: Collect CV input ---
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

        // --- Step 3: Build instructions dynamically from selected role ---
        Output.Separator();
        Output.Title("Step 1: Screening CV...");

        // KEY CONCEPT: instructions assembled from data at runtime
        string screenerInstructions =
            $"You are a Talent Acquisition specialist. " +
            $"Evaluate the provided CV against this role: '{selectedRole.Title}'. " +
            $"Requirements: {selectedRole.Requirements} " +
            $"Be strict but fair. Extract the candidate's full name and return a structured evaluation.";

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        ChatClientAgent screenerAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions: screenerInstructions);

        AgentResponse<ScreeningResult> screeningResponse = await screenerAgent.RunAsync<ScreeningResult>(cvText);
        ScreeningResult result = screeningResponse.Result;

        // --- Step 4: Print verdict ---
        Output.Separator();
        Console.WriteLine($"Candidate : {result.CandidateName}");
        Console.WriteLine($"Role      : {selectedRole.Title}");
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

        if (!result.IsQualified)
        {
            Output.Separator();
            Output.Yellow("Candidate is not suitable for this role. No further action.");
            return;
        }

        // --- Step 5: Notify role-specific interview panel ---
        Output.Separator();
        Output.Title("Step 2: Notifying interviewers...");

        // KEY CONCEPT: coordinator instructions also built dynamically from the selected role
        string coordinatorInstructions =
            $"You are an interview coordinator for the role of {selectedRole.Title}. " +
            $"You must notify ALL of the following interviewers: {string.Join(", ", selectedRole.Interviewers)}. " +
            $"Call the notify_interviewer tool exactly once per interviewer with a short personalized message " +
            $"relevant to their role in the hiring process. Do not skip any interviewer.";

        ChatClientAgent coordinatorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: coordinatorInstructions,
                tools:
                [
                    AIFunctionFactory.Create(
                        NotifyInterviewer,
                        "notify_interviewer",
                        "Send an interview preparation notification to an interviewer")
                ]);

        AgentSession session = await coordinatorAgent.CreateSessionAsync();
        string coordinatorPrompt =
            $"Candidate {result.CandidateName} has passed screening for the {selectedRole.Title} role. " +
            $"Summary: {result.Summary} Please notify all interviewers now.";

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
