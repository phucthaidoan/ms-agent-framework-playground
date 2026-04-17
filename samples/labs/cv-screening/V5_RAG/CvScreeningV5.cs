// ReSharper disable ClassNeverInstantiated.Local

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text;

namespace Samples.Labs.CV_Screening.V5_RAG;

// V5: RAG-Powered CV Screening
// New concept: Retrieval-Augmented Generation (RAG).
// Instead of hardcoding job requirements in the agent's system prompt, role profiles are
// stored in a vector store and retrieved semantically at runtime. The LLM evaluates the
// CV against retrieved facts — not baked-in text. This makes the system scalable:
// add new roles to the store without touching agent code.

public static class CvScreeningV5
{
    public static async Task RunSample()
    {
        Output.Title("CV Screening V5 — RAG-Powered Screening");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // KEY CONCEPT: embedding generator turns text into vectors for semantic search
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = client
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator();

        // Same DB as rag samples — separate collection keeps data isolated
        string connectionString = $"Data Source={Path.GetTempPath()}\\af-course-vector-store.db";
        VectorStore vectorStore = new SqliteVectorStore(connectionString, new SqliteVectorStoreOptions
        {
            EmbeddingGenerator = embeddingGenerator
        });

        VectorStoreCollection<Guid, JobProfileRecord> profileCollection =
            vectorStore.GetCollection<Guid, JobProfileRecord>("cv_job_profiles");

        await profileCollection.EnsureCollectionExistsAsync();

        // --- Phase 1: Ingest job role profiles ---
        // KEY CONCEPT: embed domain knowledge into vector store at startup.
        // In production this runs once (or on a schedule); here we offer a choice.
        Console.Write("Import/refresh job role profiles into vector store? (Y/N): ");
        ConsoleKeyInfo key = Console.ReadKey();
        Console.WriteLine();

        if (key.Key == ConsoleKey.Y)
        {
            await profileCollection.EnsureCollectionDeletedAsync();
            await profileCollection.EnsureCollectionExistsAsync();

            List<JobProfileRecord> profiles =
            [
                new JobProfileRecord
                {
                    Id = Guid.NewGuid(),
                    Title = "Senior C# Developer",
                    Requirements = "5+ years C#, .NET 6+, Azure, REST APIs required. Bonus: AI/ML experience.",
                    CompetencyAreas = "System design, cloud architecture, API design, code quality, mentoring",
                    InterviewTopics = "Azure services, REST API design patterns, SOLID principles, async/await, CI/CD",
                    RedFlags = "No cloud experience, only frontend history, short tenures under 1 year, no REST API work"
                },
                new JobProfileRecord
                {
                    Id = Guid.NewGuid(),
                    Title = "QA Engineer",
                    Requirements = "3+ years test automation, Selenium or Playwright, CI/CD pipelines required. Bonus: performance testing.",
                    CompetencyAreas = "Test strategy, automation frameworks, CI/CD pipeline integration, defect lifecycle",
                    InterviewTopics = "Test pyramid, flaky test handling, pipeline integration, BDD/TDD, performance testing tools",
                    RedFlags = "Only manual testing history, no version control usage, no CI experience, no automation code samples"
                },
                new JobProfileRecord
                {
                    Id = Guid.NewGuid(),
                    Title = "Product Manager",
                    Requirements = "3+ years PM experience, Agile/Scrum, stakeholder management required. Bonus: technical background.",
                    CompetencyAreas = "Roadmap planning, backlog prioritization, cross-functional alignment, metrics and OKRs",
                    InterviewTopics = "Prioritization frameworks (RICE, MoSCoW), OKRs, handling competing stakeholder demands, product discovery",
                    RedFlags = "No delivery track record, purely waterfall background, no data-driven decisions, no customer interaction"
                }
            ];

            int count = 0;
            foreach (JobProfileRecord profile in profiles)
            {
                count++;
                Console.Write($"\rEmbedding profile {count}/{profiles.Count}: {profile.Title}...");
                await profileCollection.UpsertAsync(profile);
            }

            Console.WriteLine();
            Output.Green("Job profiles embedded successfully.");
        }

        Output.Separator();

        // --- Phase 2: Collect CV ---
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

        // --- Phase 3: Screen with RAG ---
        Output.Separator();
        Output.Title("Step 1: RAG-Powered Screening...");

        // KEY CONCEPT: RAG = search tool wrapping vector store.
        // The agent decides when and what to query — it calls search_job_profiles
        // autonomously based on what it reads in the CV.
        ProfileSearchTool profileSearchTool = new ProfileSearchTool(profileCollection);

        ChatClientAgent screenerAgent = client
            .GetChatClient("gpt-4.1-nano")
            // KEY CONCEPT: agent grounded by retrieval, not by hardcoded instructions.
            // Notice: no job requirements are hardcoded here — the agent retrieves them.
            .AsAIAgent(
                instructions:
                    "You are a Talent Acquisition specialist. " +
                    "Use the search_job_profiles tool to retrieve the most relevant job profile for this CV. " +
                    "Then evaluate the candidate strictly against the retrieved criteria. " +
                    "Extract the candidate's full name from the CV. " +
                    "In MatchedRole, put the title of the retrieved role.",
                tools:
                [
                    AIFunctionFactory.Create(
                        profileSearchTool.Search,
                        "search_job_profiles",
                        "Search the job role knowledge base for profiles matching a query. Returns requirements, competency areas, and red flags.")
                ]);

        // KEY CONCEPT: LLM calls search_job_profiles before evaluating — grounds its answer
        // in retrieved facts from the vector store, not in its own training data or hardcoded text.
        AgentResponse<ScreeningResult> screeningResponse = await screenerAgent.RunAsync<ScreeningResult>(cvText);
        ScreeningResult result = screeningResponse.Result;

        // --- Phase 4: Print verdict ---
        Output.Separator();
        Console.WriteLine($"Candidate    : {result.CandidateName}");
        Console.WriteLine($"Matched Role : {result.MatchedRole}");
        Console.WriteLine($"Qualified    : {(result.IsQualified ? "YES" : "NO")}");
        Console.WriteLine($"Summary      : {result.Summary}");

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

        // --- Phase 5: Coordinate interview with RAG-grounded topics ---
        Output.Separator();
        Output.Title("Step 2: Coordinating Interview...");

        // KEY CONCEPT: coordinator also uses RAG — retrieves interview topics from the
        // same vector store so notifications are grounded in role-specific content.
        InterviewTopicSearchTool topicSearchTool = new InterviewTopicSearchTool(profileCollection);

        ChatClientAgent coordinatorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
                    "You are an interview coordinator. " +
                    "First, use the search_interview_questions tool to fetch relevant interview topics for the role. " +
                    "Then notify ALL of the following interviewers: Alice (Tech Lead), Bob (HR), Carol (CTO). " +
                    "Call notify_interviewer exactly once per interviewer. " +
                    "Personalize each message to their role and include 1-2 retrieved interview topics relevant to them. " +
                    "Do not skip any interviewer.",
                tools:
                [
                    AIFunctionFactory.Create(
                        topicSearchTool.Search,
                        "search_interview_questions",
                        "Retrieve interview topics and question areas for a given job role title."),
                    AIFunctionFactory.Create(
                        NotifyInterviewer,
                        "notify_interviewer",
                        "Send an interview preparation notification to an interviewer")
                ]);

        AgentSession session = await coordinatorAgent.CreateSessionAsync();

        string coordinatorPrompt =
            $"Candidate '{result.CandidateName}' has passed screening for the '{result.MatchedRole}' role. " +
            $"Summary: {result.Summary} " +
            $"Search for interview questions for this role, then notify all three interviewers.";

        await coordinatorAgent.RunAsync(coordinatorPrompt, session);

        Output.Separator();
        Output.Green("All interviewers have been notified with role-relevant topics.");
    }

    private static void NotifyInterviewer(string interviewerName, string candidateName, string message)
    {
        Output.Green($"[NOTIFICATION] {interviewerName}: {message} (Candidate: {candidateName})");
    }

    // KEY CONCEPT: search tool wraps VectorStoreCollection.SearchAsync.
    // This is the RAG retrieval step — the agent calls this; we don't call it explicitly.
    private class ProfileSearchTool(VectorStoreCollection<Guid, JobProfileRecord> collection)
    {
        public async Task<string> Search(string query)
        {
            StringBuilder result = new StringBuilder();
            Output.Gray($"[RAG] Searching job profiles for: \"{query}\"");

            await foreach (VectorSearchResult<JobProfileRecord> hit in collection.SearchAsync(query, 2))
            {
                Output.Gray($"  → Retrieved: {hit.Record.Title} (score: {hit.Score:F3})");
                result.AppendLine($"Role: {hit.Record.Title}");
                result.AppendLine($"Requirements: {hit.Record.Requirements}");
                result.AppendLine($"Competency Areas: {hit.Record.CompetencyAreas}");
                result.AppendLine($"Red Flags: {hit.Record.RedFlags}");
                result.AppendLine();
            }

            return result.ToString();
        }
    }

    private class InterviewTopicSearchTool(VectorStoreCollection<Guid, JobProfileRecord> collection)
    {
        public async Task<string> Search(string roleTitle)
        {
            StringBuilder result = new StringBuilder();
            Output.Gray($"[RAG] Fetching interview topics for: \"{roleTitle}\"");

            await foreach (VectorSearchResult<JobProfileRecord> hit in collection.SearchAsync(roleTitle, 1))
            {
                Output.Gray($"  → Retrieved: {hit.Record.Title} (score: {hit.Score:F3})");
                result.AppendLine($"Role: {hit.Record.Title}");
                result.AppendLine($"Interview Topics: {hit.Record.InterviewTopics}");
                result.AppendLine($"Competency Areas: {hit.Record.CompetencyAreas}");
            }

            return result.ToString();
        }
    }

    private class ScreeningResult
    {
        public required bool IsQualified { get; set; }
        public required string CandidateName { get; set; }
        public required string MatchedRole { get; set; }
        public required string Summary { get; set; }
        public required List<string> MatchedCriteria { get; set; }
        public required List<string> MissingCriteria { get; set; }
    }

    // KEY CONCEPT: vector record schema — [VectorStoreKey], [VectorStoreData], [VectorStoreVector]
    // The Vector property is a computed string that represents the document in embedding space.
    // Richer text in the vector = better semantic search results.
    private class JobProfileRecord
    {
        [VectorStoreKey]
        public required Guid Id { get; set; }

        [VectorStoreData]
        public required string Title { get; set; }

        [VectorStoreData]
        public required string Requirements { get; set; }

        [VectorStoreData]
        public required string CompetencyAreas { get; set; }

        [VectorStoreData]
        public required string InterviewTopics { get; set; }

        [VectorStoreData]
        public required string RedFlags { get; set; }

        [VectorStoreVector(1536)]
        public string Vector => $"{Title}: {Requirements}. Key competencies: {CompetencyAreas}. Red flags: {RedFlags}";
    }
}
