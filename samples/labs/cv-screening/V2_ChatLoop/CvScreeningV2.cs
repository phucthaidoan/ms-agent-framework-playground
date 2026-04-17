using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Labs.CV_Screening.V2_ChatLoop;

// V2: Multi-Turn Chat Loop
// New concept: AgentSession — passing a session across multiple RunAsync calls so the
// agent remembers the full conversation history. The user can ask follow-up questions
// about the screening result interactively.

public static class CvScreeningV2
{
    public static async Task RunSample()
    {
        Output.Title("CV Screening V2 — Multi-Turn Chat Loop");
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

        // --- Step 2: Screen the CV (one-shot, no session needed) ---
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

        // --- Step 4: Start multi-turn advisor session ---
        Output.Separator();
        Output.Title("Step 2: Q&A about screening result (type 'exit' to quit)");

        ChatClientAgent advisorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                "You are a Talent Acquisition advisor. " +
                "You help hiring teams understand CV screening results and make informed decisions. " +
                "Be concise, practical, and objective.");

        // KEY CONCEPT: create a session to maintain conversation history
        AgentSession session = await advisorAgent.CreateSessionAsync();

        // Pre-seed the session with the screening context
        string context =
            $"Here is the screening result for candidate '{result.CandidateName}': " +
            $"Qualified: {(result.IsQualified ? "yes" : "no")}. " +
            $"Summary: {result.Summary} " +
            $"Matched criteria: {string.Join(", ", result.MatchedCriteria)}. " +
            $"Missing criteria: {string.Join(", ", result.MissingCriteria)}. " +
            $"Use this as the basis for answering follow-up questions.";

        await advisorAgent.RunAsync(context, session);

        Output.Gray("Screening context loaded. Ask your questions below.");
        Console.WriteLine();

        // KEY CONCEPT: loop — each turn passes the same session, so history is preserved
        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine() ?? string.Empty;

            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            AgentResponse response = await advisorAgent.RunAsync(input, session);
            Console.WriteLine(response.Text);
            Output.Separator(false);
        }

        Output.Separator();
        Output.Gray("Session ended.");
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
