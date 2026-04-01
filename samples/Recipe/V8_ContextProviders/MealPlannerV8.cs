// ReSharper disable ClassNeverInstantiated.Local

using DotNet.Testcontainers.Builders;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;
using OpenAI.Chat;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Samples.SampleUtilities;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Samples.Recipe.V8_ContextProviders;

// V8: Context Providers Architecture
// Builds on V7's typed memory collections and replaces two manual patterns with
// the Agent Framework's built-in AIContextProvider pipeline:
//
//   HIGH FIT 1 — UserProfileContextProvider
//     Replaces the four Build*Memory() static helpers (BuildPlannerMemory,
//     BuildCriticMemory, BuildNarrativeMemory, BuildAdvisorMemory).
//     Runs ProvideAIContextAsync before every invocation to inject user profile
//     and semantic notes as a ChatMessage so context is always fresh.
//     Session state is stored via ProviderSessionState<T> — the provider instance
//     itself stays stateless across sessions.
//
//   HIGH FIT 2 — ConversationTurnContextProvider
//     Replaces the manual turnCollection.UpsertAsync call inside the advisor loop.
//     Runs StoreAIContextAsync after every advisor invocation to persist the turn
//     automatically — "one document per turn" without any boilerplate in RunAsync loop.
//
// Everything else (tools, consolidation pipeline, episodic recall, RAG) is unchanged from V7.

public static class MealPlannerV8
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int MaxIterations = 3;
    private const int DefaultCaloriesPerDay = 2000;
    private const double DefaultBudget = 50.0;
    private const double UserProfileScoreThreshold = 0.85;

    // ── Entry point ──────────────────────────────────────────────────────────

    private const string TracingServiceName = "MealPlannerV8";

    public static async Task RunSample()
    {
        Output.Title("Meal Planner V8 — Context Providers Architecture");
        Output.Separator();

        // ── Jaeger container + OpenTelemetry setup ────────────────────────────

        Output.Gray("Starting Jaeger for trace visualization...");
        var jaeger = new ContainerBuilder("jaegertracing/all-in-one:latest")
            .WithPortBinding(16686, 16686)
            .WithPortBinding(4317, 4317)
            .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(16686)))
            .Build();
        await jaeger.StartAsync();
        Output.Green("Jaeger UI: http://localhost:16686");
        Output.Separator();

        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(TracingServiceName))
            .AddSource(TracingServiceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri("http://localhost:4317"))
            .Build();

        var activitySource = new ActivitySource(TracingServiceName);

        try
        {
            using var rootSpan = activitySource.StartActivity("MealPlannerSession");

            // ── Infrastructure setup ─────────────────────────────────────────────

            string apiKey = SecretManager.GetOpenAIApiKey();
            OpenAIClient client = new OpenAIClient(apiKey);

            IChatClient chatClient = client
                .GetChatClient("gpt-4.1-nano")
                .AsIChatClient()
                .AsBuilder()
                .UseOpenTelemetry(sourceName: TracingServiceName, configure: c => c.EnableSensitiveData = true)
                .Build();

            IEmbeddingGenerator<string, Embedding<float>> embedder = client
                .GetEmbeddingClient("text-embedding-3-small")
                .AsIEmbeddingGenerator();

            string connectionString = $"Data Source={Path.GetTempPath()}\\af-course-vector-store.db";
            VectorStore store = new SqliteVectorStore(connectionString, new SqliteVectorStoreOptions
            {
                EmbeddingGenerator = embedder
            });

            var dietCollection  = store.GetCollection<Guid, DietKnowledgeRecord>("meal_diet_profiles");
            var mealCollection  = store.GetCollection<Guid, MealRecord>("meal_plan_meals");
            var profileCollection = store.GetCollection<Guid, UserProfileRecord>("meal_user_profile");
            var turnCollection    = store.GetCollection<Guid, ConversationTurnRecord>("meal_conversation_turns");
            var noteCollection    = store.GetCollection<Guid, SemanticNoteRecord>("meal_semantic_notes");

            await dietCollection.EnsureCollectionExistsAsync();
            await mealCollection.EnsureCollectionExistsAsync();
            await profileCollection.EnsureCollectionExistsAsync();
            await turnCollection.EnsureCollectionExistsAsync();
            await noteCollection.EnsureCollectionExistsAsync();

            string sessionId = Guid.NewGuid().ToString();

            // ── Phase 0: User identity + episodic recall ──────────────────────────

            Console.Write("Enter your name (new or returning user): ");
            string username = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(username))
            {
                Output.Red("No name provided. Exiting.");
                return;
            }

            Output.Gray($"[MEMORY] Searching user profile for: \"{username}\"");
            UserProfileRecord? profile = await LookupUserProfileAsync(profileCollection, username);

            bool isReturning = profile is not null;

            if (isReturning)
            {
                PrintWelcomeBack(profile!);
                await PrintEpisodicRecallAsync(turnCollection, username);
            }
            else
            {
                profile = NewProfile(username);
                Output.Green($"Welcome! This is your first session, {Capitalise(username)}.");
                Output.Gray("Let's set up your meal preferences.");
            }

            Console.WriteLine();

            // ── Phase 1: Diet knowledge ingestion ────────────────────────────────

            Output.Separator();
            Console.Write("Import/refresh diet knowledge base? (Y/N): ");
            if (Console.ReadKey().Key == ConsoleKey.Y)
            {
                Console.WriteLine();
                await RefreshDietKnowledgeAsync(dietCollection);
            }
            else
            {
                Console.WriteLine();
            }

            // ── Phase 2: Collect inputs ────────────────────────────────────────────

            Output.Separator();
            string dietDescription = ReadString(
                isReturning ? $"Diet? (Enter to keep '{profile!.PreferredDietType}'): "
                            : "Diet preference (e.g. 'Keto', 'Vegan', 'Mediterranean'): ",
                isReturning ? profile!.PreferredDietType : null);

            if (string.IsNullOrWhiteSpace(dietDescription))
            {
                Output.Red("No diet preference provided. Exiting.");
                return;
            }

            int numberOfDays = ReadInt(
                isReturning ? $"Days to plan? (Enter to keep {profile!.DefaultPlanDays}): "
                            : "Days to plan? (1–7): ",
                isReturning ? profile!.DefaultPlanDays : 3,
                min: 1, max: 7);

            int targetCalories = ReadInt(
                isReturning ? $"Daily calories? (Enter to keep {profile!.DefaultCalories}): "
                            : $"Daily calories? (Enter for {DefaultCaloriesPerDay}): ",
                isReturning ? profile!.DefaultCalories : DefaultCaloriesPerDay);

            double budget = ReadDouble(
                isReturning ? $"Budget USD? (Enter to keep ${profile!.DefaultBudget}): "
                            : $"Budget USD? (Enter for ${DefaultBudget}): ",
                isReturning ? profile!.DefaultBudget : DefaultBudget);

            if (string.IsNullOrWhiteSpace(profile!.FoodRestrictions))
            {
                Console.Write("Food restrictions / allergies? (Enter to skip): ");
                string? r = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(r))
                    profile.FoodRestrictions = r.Trim();
            }

            // ── Phase 3: Load semantic notes ───────────────────────────────────────

            // KEY CONCEPT (V8): semantic notes are loaded once here and passed to the
            // context providers via their constructor. Each provider then injects only
            // the notes it needs in ProvideAIContextAsync — no Build*Memory() helpers needed.
            List<SemanticNoteRecord> userNotes = await LoadSemanticNotesAsync(noteCollection, username);

            PrintSessionSummary(username, dietDescription, numberOfDays, targetCalories, budget, profile);

            // ── Phase 4: Narrative preview (streaming) ────────────────────────────

            Output.Title("Step 1: Streaming plan preview...");
            Console.WriteLine();

            // KEY CONCEPT (V8): NarrativeContextProvider replaces BuildNarrativeMemory().
            // It injects food restrictions and top preferences as a message before each invoke.
            var narrativeContextProvider = new UserProfileContextProvider(
                profile, userNotes, mode: UserProfileContextProvider.Mode.Narrative);

            ChatClientAgent narrativeAgent = chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions =
                            $"You are a professional meal planner. " +
                            $"Describe the {numberOfDays}-day meal plan you are about to create for a '{dietDescription}' diet. " +
                            $"Explain the theme, key ingredients, how you will hit {targetCalories} kcal/day, " +
                            $"and how you will stay within ${budget}. Write in flowing prose, 2–3 paragraphs."
                    },
                    AIContextProviders = [narrativeContextProvider]
                });

            await foreach (AgentResponseUpdate token in narrativeAgent.RunStreamingAsync(
                $"Preview a {numberOfDays}-day '{dietDescription}' plan at {targetCalories} kcal/day, ${budget} total."))
                Console.Write(token);

            Console.WriteLine();

            // ── Phase 5: Planner + critic agents ─────────────────────────────────

            var dietSearchTool = new DietKnowledgeSearchTool(dietCollection);

            // KEY CONCEPT (V8): PlannerContextProvider replaces BuildPlannerMemory().
            // Injects food restrictions, dislikes, preferences, past plans, weight goal.
            var plannerContextProvider = new UserProfileContextProvider(
                profile, userNotes, mode: UserProfileContextProvider.Mode.Planner);

            ChatClientAgent plannerAgent = chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions =
                            $"You are a professional meal planner. " +
                            $"Use search_diet_knowledge to retrieve dietary rules before generating the plan. " +
                            $"Generate a {numberOfDays}-day plan with exactly 3 meals per day (Breakfast, Lunch, Dinner). " +
                            $"Calorie target: {targetCalories} kcal/day (Breakfast ~25%, Lunch ~35%, Dinner ~40%). " +
                            $"Total budget: ${budget} across {numberOfDays} days (~${budget / numberOfDays:F2}/day). " +
                            $"Each meal needs: name, calories, protein/carbs/fat (g), cost (USD), 4–6 ingredients."
                    },
                    AIContextProviders = [plannerContextProvider],
                    // KEY CONCEPT (V8): tools are passed via ChatOptions alongside the provider
                    // because ChatClientAgentOptions does not have a separate tools parameter.
                });

            // Attach tools separately via AsAIAgent overload
            // Note: The clean pattern is to add tools via ChatOptions.Tools — however the
            // existing codebase uses AIFunctionFactory with AsAIAgent shorthand. We retain
            // that pattern by wrapping in the builder after option creation.

            // ── rebuild plannerAgent with tool support ─────────────────────────────
            // The ChatClientAgentOptions.ChatOptions.Tools accepts IList<AITool>, which is
            // the same type AIFunctionFactory.Create returns.
            plannerContextProvider = new UserProfileContextProvider(
                profile, userNotes, mode: UserProfileContextProvider.Mode.Planner);

            plannerAgent = chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions =
                            $"You are a professional meal planner. " +
                            $"Use search_diet_knowledge to retrieve dietary rules before generating the plan. " +
                            $"Generate a {numberOfDays}-day plan with exactly 3 meals per day (Breakfast, Lunch, Dinner). " +
                            $"Calorie target: {targetCalories} kcal/day (Breakfast ~25%, Lunch ~35%, Dinner ~40%). " +
                            $"Total budget: ${budget} across {numberOfDays} days (~${budget / numberOfDays:F2}/day). " +
                            $"Each meal needs: name, calories, protein/carbs/fat (g), cost (USD), 4–6 ingredients.",
                        Tools =
                        [
                            AIFunctionFactory.Create(dietSearchTool.Search, "search_diet_knowledge",
                                "Retrieve dietary rules, macro guidelines, typical ingredients, and foods to avoid.")
                        ]
                    },
                    AIContextProviders = [plannerContextProvider]
                });

            // KEY CONCEPT (V8): CriticContextProvider replaces BuildCriticMemory().
            // Injects food restrictions as an extra validation rule.
            var criticContextProvider = new UserProfileContextProvider(
                profile, userNotes, mode: UserProfileContextProvider.Mode.Critic);

            ChatClientAgent criticAgent = chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions =
                            $"You are a strict nutrition reviewer. Verify ALL of the following: " +
                            $"1. Each day has exactly 3 meals (Breakfast, Lunch, Dinner). " +
                            $"2. Each day totals ~{targetCalories} kcal (±200 kcal). " +
                            $"3. No meal exceeds 50% of the daily calorie target. " +
                            $"4. Total cost ≤ ${budget}. " +
                            $"5. Standard diet compliance (Vegan: no animal products; Keto: carbs <50g/day, fat ≥65%; Mediterranean: no processed food, red meat at most once). " +
                            $"6. Every meal has name, calories, macros, cost, and ingredients. " +
                            $"Approve only if ALL checks pass. List every violation."
                    },
                    AIContextProviders = [criticContextProvider]
                });

            // ── Phase 6: Refinement loop ──────────────────────────────────────────

            AgentSession plannerSession = await plannerAgent.CreateSessionAsync();

            Output.Separator();
            Output.Title("Step 2: Generating structured plan...");
            Output.Yellow("[PLANNER] Generating initial plan...");

            AgentResponse<MealPlan> initial = await plannerAgent.RunAsync<MealPlan>(
                $"Generate a {numberOfDays}-day '{dietDescription}' plan at {targetCalories} kcal/day, " +
                $"${budget} total. Use search_diet_knowledge first.",
                plannerSession);

            string planJson = initial.Text;
            MealPlan plan = initial.Result;
            Output.Green($"[PLANNER] Initial plan ready ({plan.Days.Count} days, est. ${plan.EstimatedTotalCost:F2})");

            for (int i = 1; i <= MaxIterations; i++)
            {
                Output.Separator();
                Output.Yellow($"[CRITIC] Iteration {i}/{MaxIterations}...");

                AgentResponse<NutritionCritique> critiqueResponse = await criticAgent.RunAsync<NutritionCritique>(planJson);
                NutritionCritique critique = critiqueResponse.Result;

                if (critique.Approved)
                {
                    Output.Green("[CRITIC] Approved ✓");
                    break;
                }

                Output.Red("[CRITIC] Not approved:");
                critique.DietViolations.ForEach(v => Output.Red($"  • {v}"));
                critique.MacroIssues.ForEach(m => Output.Red($"  • {m}"));
                critique.Suggestions.ForEach(s => Output.Yellow($"  → {s}"));

                if (i == MaxIterations)
                {
                    Output.Yellow("[PLANNER] Max iterations reached — keeping last plan.");
                    break;
                }

                Output.Yellow("[PLANNER] Refining...");
                string feedback =
                    $"Rejected. Fix ALL issues and return corrected JSON.\n" +
                    $"Violations: {string.Join("; ", critique.DietViolations)}\n" +
                    $"Macro issues: {string.Join("; ", critique.MacroIssues)}\n" +
                    $"Suggestions: {string.Join("; ", critique.Suggestions)}\n\nPrevious:\n{planJson}";

                AgentResponse<MealPlan> refined = await plannerAgent.RunAsync<MealPlan>(feedback, plannerSession);
                planJson = refined.Text;
                plan = refined.Result;
                Output.Green($"[PLANNER] Refined (est. ${plan.EstimatedTotalCost:F2})");
            }

            // ── Phase 7: Budget check + print ────────────────────────────────────

            Output.Separator();
            Output.Title("Step 3: Budget check");
            CheckBudget(plan.EstimatedTotalCost, budget);

            Output.Separator();
            Output.Title("Step 4: Final Meal Plan");
            PrintMealPlan(plan);

            // ── Phase 8: First memory write — profile + plan history note ─────────

            Output.Separator();
            profile.PreferredDietType = dietDescription;
            profile.DefaultCalories = targetCalories;
            profile.DefaultBudget = budget;
            profile.DefaultPlanDays = numberOfDays;
            profile.TotalSessionsCount++;
            profile.LastSessionDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            await profileCollection.UpsertAsync(profile);
            Output.Green($"[MEMORY] User profile saved for {username}.");

            await noteCollection.UpsertAsync(new SemanticNoteRecord
            {
                Id = Guid.NewGuid(),
                Username = username,
                NoteType = NoteType.PlanHistory,
                Content = $"{dietDescription}, {numberOfDays} days, ${plan.EstimatedTotalCost:F2} on {profile.LastSessionDate}",
                Source = "session",
                CreatedDate = profile.LastSessionDate
            });
            Output.Green("[MEMORY] Plan history note stored.");

            // ── Phase 9: Index meals for advisor ─────────────────────────────────

            Output.Separator();
            Output.Yellow("[RAG] Indexing approved meals...");
            await mealCollection.EnsureCollectionDeletedAsync();
            await mealCollection.EnsureCollectionExistsAsync();

            int mealCount = 0;
            foreach (DayPlan day in plan.Days)
            {
                foreach (Meal meal in day.Meals)
                {
                    mealCount++;
                    await mealCollection.UpsertAsync(new MealRecord
                    {
                        Id            = Guid.NewGuid(),
                        Username      = username,
                        DayNumber     = day.DayNumber,
                        MealType      = meal.MealType,
                        Name          = meal.Name,
                        Calories      = meal.Calories,
                        Macros        = $"P:{meal.ProteinGrams}g C:{meal.CarbsGrams}g F:{meal.FatGrams}g",
                        EstimatedCost = $"${meal.EstimatedCost:F2}",
                        Ingredients   = string.Join(", ", meal.Ingredients)
                    });
                }
            }

            Output.Green($"[RAG] {mealCount} meals indexed.");

            // ── Phase 10: Advisor session ─────────────────────────────────────────

            Output.Separator();
            Output.Title("Step 5: Meal Plan Advisor (type 'exit' to quit)");

            var mealSearchTool = new MealPlanSearchTool(mealCollection, username);
            var memorySearchTool = new UserMemorySearchTool(turnCollection, noteCollection, username);

            string planContext = $"{dietDescription}, {numberOfDays} days, ${budget}";

            // KEY CONCEPT (V8): AdvisorContextProvider replaces BuildAdvisorMemory().
            // It injects user name, diet, calorie target, budget, restrictions and dislikes
            // as a ChatMessage before each invocation.
            var advisorContextProvider = new UserProfileContextProvider(
                profile, userNotes, mode: UserProfileContextProvider.Mode.Advisor,
                username: username, targetCalories: targetCalories, budget: budget);

            // KEY CONCEPT (V8): ConversationTurnContextProvider replaces the manual
            // turnCollection.UpsertAsync call inside the advisor loop.
            // It stores every request+response pair automatically via StoreAIContextAsync.
            var turnContextProvider = new ConversationTurnContextProvider(
                turnCollection, username, sessionId, planContext);

            ChatClientAgent advisorAgent = chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        Instructions =
                            $"You are a friendly meal plan advisor for '{dietDescription}'. " +
                            $"Use search_meal_plan to look up meals. " +
                            $"Use search_user_memory when the user asks about past sessions, preferences, or history — always retrieve before answering. " +
                            $"Budget: ${budget}. Target: {targetCalories} kcal/day. Be concise.",
                        Tools =
                        [
                            AIFunctionFactory.Create(mealSearchTool.Search, "search_meal_plan",
                                "Search the approved meal plan for meals, days, ingredients, or nutrition."),
                            AIFunctionFactory.Create(memorySearchTool.Search, "search_user_memory",
                                "Search past conversation turns and preference notes. Use when the user references past sessions or dietary preferences.")
                        ]
                    },
                    AIContextProviders = [advisorContextProvider, turnContextProvider]
                });

            AgentSession advisorSession = await advisorAgent.CreateSessionAsync();
            List<string> sessionTranscript = [];

            Output.Gray("Ready. Ask anything (type 'exit' to finish):");
            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                string input = Console.ReadLine() ?? string.Empty;

                if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
                if (string.IsNullOrWhiteSpace(input)) continue;

                sessionTranscript.Add(input);

                // KEY CONCEPT (V8): no manual UpsertAsync here — ConversationTurnContextProvider
                // handles storage automatically via StoreAIContextAsync after each RunAsync.
                AgentResponse response = await advisorAgent.RunAsync(input, advisorSession);
                Console.WriteLine(response.Text);
                Output.Separator(false);
            }

            // ── Phase 11: Memory consolidation pipeline ───────────────────────────

            if (sessionTranscript.Count > 0)
                await RunMemoryConsolidationAsync(chatClient, noteCollection, username, sessionTranscript);

            Output.Separator();
            Output.Green("Session ended. Meal planning complete.");
            Output.Green($"View traces at: http://localhost:16686 (search for '{TracingServiceName}')");
            Output.Gray("Press Enter to stop Jaeger and exit...");
            Console.ReadLine();
        }
        finally
        {
            await jaeger.StopAsync();
        }
    }

    // ── Context Providers ─────────────────────────────────────────────────────

    // KEY CONCEPT (V8): UserProfileContextProvider
    // Replaces all four Build*Memory() static string helpers from V7.
    //
    // Mode enum selects which subset of memory each agent needs:
    //   Planner  → restrictions + dislikes + preferences + plan history + weight goal
    //   Critic   → restrictions only (as validation rule #7)
    //   Narrative → restrictions + top-3 preferences/dislikes
    //   Advisor  → username + diet + calories + budget + restrictions + dislikes
    //
    // The same provider class is reused for all four agents — the Mode controls what
    // gets injected. This avoids four separate classes while keeping each agent's
    // context focused.
    //
    // ProvideAIContextAsync returns an AIContext with a single ChatMessage(Role=User).
    // The framework appends this message before the agent's own message list.
    // StoreAIContextAsync is a no-op here — this provider only reads, not writes.
    private sealed class UserProfileContextProvider : AIContextProvider
    {
        public enum Mode { Planner, Critic, Narrative, Advisor }

        private readonly UserProfileRecord _profile;
        private readonly List<SemanticNoteRecord> _notes;
        private readonly Mode _mode;
        private readonly string _username;
        private readonly int _targetCalories;
        private readonly double _budget;

        public UserProfileContextProvider(
            UserProfileRecord profile,
            List<SemanticNoteRecord> notes,
            Mode mode,
            string username = "",
            int targetCalories = 0,
            double budget = 0.0)
            : base(null, null)
        {
            _profile = profile;
            _notes = notes;
            _mode = mode;
            _username = username;
            _targetCalories = targetCalories;
            _budget = budget;
        }

        // KEY CONCEPT (V8): StateKeys must be unique per provider instance attached to
        // the same agent. If two providers on the same agent share a StateKey value,
        // the framework throws a validation error at agent creation time.
        public override IReadOnlyList<string> StateKeys => [$"UserProfileContextProvider_{_mode}"];

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            string contextText = _mode switch
            {
                Mode.Planner   => BuildPlannerContext(),
                Mode.Critic    => BuildCriticContext(),
                Mode.Narrative => BuildNarrativeContext(),
                Mode.Advisor   => BuildAdvisorContext(),
                _              => string.Empty
            };

            if (string.IsNullOrWhiteSpace(contextText))
                return new ValueTask<AIContext>(new AIContext());

            // KEY CONCEPT (V8): context is injected as a ChatMessage(Role=User) so the
            // model sees it as part of the conversation, not just as a system instruction.
            // This lets the model reference memory facts naturally in its responses.
            Output.Gray($"[CONTEXT PROVIDER] {_mode} context injected.");
            return new ValueTask<AIContext>(new AIContext
            {
                Messages = [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, contextText)]
            });
        }

        // ProvideAIContextAsync handles everything — no storage needed here.
        protected override ValueTask StoreAIContextAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        private string BuildPlannerContext()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[User memory context for meal planning]");

            if (!string.IsNullOrWhiteSpace(_profile.FoodRestrictions))
                sb.AppendLine($"HARD RESTRICTIONS (allergies — enforce in every meal): {_profile.FoodRestrictions}.");

            string dislikes = string.Join(", ", _notes
                .Where(n => n.NoteType == NoteType.Dislike)
                .Select(n => n.Content));
            if (!string.IsNullOrWhiteSpace(dislikes))
                sb.AppendLine($"User dislikes — avoid where possible: {dislikes}.");

            string preferences = string.Join(", ", _notes
                .Where(n => n.NoteType == NoteType.Preference)
                .Select(n => n.Content));
            if (!string.IsNullOrWhiteSpace(preferences))
                sb.AppendLine($"User preferences: {preferences}.");

            string history = string.Join("; ", _notes
                .Where(n => n.NoteType == NoteType.PlanHistory)
                .Take(5)
                .Select(n => n.Content));
            if (!string.IsNullOrWhiteSpace(history))
                sb.AppendLine($"Past plans: {history}. Aim for variety — do not repeat meals.");

            if (!string.IsNullOrWhiteSpace(_profile.WeightGoal))
                sb.AppendLine($"User goal: {_profile.WeightGoal}.");

            return sb.ToString();
        }

        private string BuildCriticContext()
        {
            if (string.IsNullOrWhiteSpace(_profile.FoodRestrictions)) return string.Empty;
            return $"[User memory context for nutrition review]\n" +
                   $"7. USER RESTRICTION CHECK: restrictions are {_profile.FoodRestrictions}. " +
                   $"Flag any meal containing a restricted ingredient as a diet violation.";
        }

        private string BuildNarrativeContext()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[User memory context for narrative preview]");

            if (!string.IsNullOrWhiteSpace(_profile.FoodRestrictions))
                sb.AppendLine($"The user has restrictions: {_profile.FoodRestrictions}. Do not mention these foods.");

            string advisorNotes = string.Join(", ", _notes
                .Where(n => n.NoteType is NoteType.Preference or NoteType.Dislike)
                .Take(3)
                .Select(n => n.Content));
            if (!string.IsNullOrWhiteSpace(advisorNotes))
                sb.AppendLine($"User preferences: {advisorNotes}. Reflect these in your narrative.");

            return sb.ToString();
        }

        private string BuildAdvisorContext()
        {
            if (string.IsNullOrWhiteSpace(_profile.PreferredDietType)) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[User memory context for meal advisor]");
            sb.AppendLine($"User: {_username}, preferred diet: {_profile.PreferredDietType}, " +
                          $"target: {_targetCalories} kcal/day, budget: ${_budget}.");

            if (!string.IsNullOrWhiteSpace(_profile.FoodRestrictions))
                sb.AppendLine($"Restrictions: {_profile.FoodRestrictions}.");

            string dislikes = string.Join(", ", _notes
                .Where(n => n.NoteType == NoteType.Dislike)
                .Select(n => n.Content));
            if (!string.IsNullOrWhiteSpace(dislikes))
                sb.AppendLine($"Dislikes: {dislikes}.");

            return sb.ToString();
        }
    }

    // KEY CONCEPT (V8): ConversationTurnContextProvider
    // Replaces the manual turnCollection.UpsertAsync inside the advisor while-loop.
    //
    // StoreAIContextAsync fires automatically after every successful advisor invocation.
    // It receives the request messages (user input) and the response messages (agent reply)
    // and persists them as a ConversationTurnRecord — implementing the
    // "one document per turn" pattern from V7 without any boilerplate in the call site.
    //
    // The turn counter is stored in AgentSession.StateBag using a string key derived from
    // the provider type name. This ensures the counter is per-session and not shared
    // across multiple sessions created for the same agent.
    private sealed class ConversationTurnContextProvider : AIContextProvider
    {
        private readonly VectorStoreCollection<Guid, ConversationTurnRecord> _turns;
        private readonly string _username;
        private readonly string _sessionId;
        private readonly string _planContext;
        // KEY CONCEPT (V8): AgentSessionStateBag.TryGetValue<T>/SetValue<T> only accept
        // reference types. We use a TurnCounter wrapper class rather than a plain int.
        private sealed class TurnCounter { public int Value { get; set; } }
        private readonly string _stateKey;

        public ConversationTurnContextProvider(
            VectorStoreCollection<Guid, ConversationTurnRecord> turns,
            string username,
            string sessionId,
            string planContext)
            : base(null, null)
        {
            _turns = turns;
            _username = username;
            _sessionId = sessionId;
            _planContext = planContext;
            _stateKey = GetType().Name + "_TurnNumber";
        }

        public override IReadOnlyList<string> StateKeys => [GetType().Name];

        // No context to inject before the invocation — this provider only stores.
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
            => new(new AIContext());

        // KEY CONCEPT (V8): StoreAIContextAsync is called automatically after every
        // successful RunAsync. We extract the last user message and agent response,
        // then upsert a ConversationTurnRecord — identical to what V7 did manually.
        protected override async ValueTask StoreAIContextAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            // Extract the user's message from the request messages
            string userMessage = context.RequestMessages
                .Where(m => m.Role == ChatRole.User)
                .Select(m => m.Text)
                .LastOrDefault() ?? string.Empty;

            // Extract the agent's response text
            string agentResponse = context.ResponseMessages
                ?.Where(m => m.Role == ChatRole.Assistant)
                .Select(m => m.Text)
                .LastOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(agentResponse))
                return;

            // KEY CONCEPT (V8): turn counter stored in AgentSession.StateBag so each
            // session gets its own counter — the provider instance itself stays stateless.
            // AgentSessionStateBag.TryGetValue<T> / SetValue<T> require reference types,
            // hence TurnCounter wrapper instead of plain int.
            int turnNumber = 1;
            if (context.Session?.StateBag != null &&
                context.Session.StateBag.TryGetValue<TurnCounter>(_stateKey, out TurnCounter? prev) &&
                prev != null)
                turnNumber = prev.Value + 1;
            context.Session?.StateBag?.SetValue(_stateKey, new TurnCounter { Value = turnNumber });

            await _turns.UpsertAsync(new ConversationTurnRecord
            {
                Id            = Guid.NewGuid(),
                SessionId     = _sessionId,
                TurnNumber    = turnNumber,
                Username      = _username,
                UserMessage   = userMessage,
                AgentResponse = agentResponse,
                Timestamp     = DateTime.UtcNow.ToString("o"),
                PlanContext   = _planContext
            }, cancellationToken);

            Output.Gray($"[CONTEXT PROVIDER] Turn {turnNumber} stored automatically.");
        }
    }

    // ── Memory helpers (unchanged from V7) ────────────────────────────────────

    private static async Task<UserProfileRecord?> LookupUserProfileAsync(
        VectorStoreCollection<Guid, UserProfileRecord> collection, string username)
    {
        await foreach (VectorSearchResult<UserProfileRecord> hit in collection.SearchAsync(username, 1))
        {
            if (hit.Score >= UserProfileScoreThreshold)
            {
                Output.Gray($"  → Retrieved: {hit.Record.Username} (score: {hit.Score:F3})");
                return hit.Record;
            }
            Output.Gray($"  → No match above threshold (score: {hit.Score:F3}) — new user");
        }
        return null;
    }

    private static async Task PrintEpisodicRecallAsync(
        VectorStoreCollection<Guid, ConversationTurnRecord> collection, string username)
    {
        VectorSearchOptions<ConversationTurnRecord> options = new()
        {
            Filter = r => r.Username == username
        };

        List<ConversationTurnRecord> turns = [];
        await foreach (VectorSearchResult<ConversationTurnRecord> hit in
            collection.SearchAsync("meal plan conversation", 20, options))
            turns.Add(hit.Record);

        if (turns.Count == 0) return;

        string latestSessionId = turns.OrderByDescending(t => t.Timestamp).First().SessionId;

        List<ConversationTurnRecord> lastSession = turns
            .Where(t => t.SessionId == latestSessionId)
            .OrderBy(t => t.TurnNumber)
            .ToList();

        Output.Gray("[MEMORY] Recalling last session...");
        Output.Blue($"  In your last session ({lastSession[0].Timestamp[..10]}), you discussed:");
        foreach (ConversationTurnRecord t in lastSession.Take(3))
            Output.Gray($"    • \"{Truncate(t.UserMessage, 70)}\"");
    }

    private static async Task<List<SemanticNoteRecord>> LoadSemanticNotesAsync(
        VectorStoreCollection<Guid, SemanticNoteRecord> collection, string username)
    {
        VectorSearchOptions<SemanticNoteRecord> options = new()
        {
            Filter = r => r.Username == username
        };

        List<SemanticNoteRecord> notes = [];
        await foreach (VectorSearchResult<SemanticNoteRecord> hit in
            collection.SearchAsync("user preferences dislikes restrictions plan history", 15, options))
            notes.Add(hit.Record);

        if (notes.Count > 0)
            Output.Gray($"[MEMORY] {notes.Count} semantic note(s) loaded for {username}.");

        return notes;
    }

    private static async Task RunMemoryConsolidationAsync(
        IChatClient chatClient,
        VectorStoreCollection<Guid, SemanticNoteRecord> noteCollection,
        string username,
        List<string> sessionTranscript)
    {
        Output.Yellow("[MEMORY] Running memory consolidation pipeline...");

        ChatClientAgent extractionAgent = chatClient.AsAIAgent(
            instructions:
                "You are a preferences extractor. Read these user messages from a meal advisor session. " +
                "Extract only stated preferences, dislikes, or requests for future plans. " +
                "Return a comma-separated list of concise phrases, or empty string if none found. " +
                "Return ONLY the list, no explanation.");

        AgentResponse extractResult = await extractionAgent.RunAsync(string.Join("\n", sessionTranscript));
        string extracted = extractResult.Text.Trim();

        if (string.IsNullOrWhiteSpace(extracted))
        {
            Output.Gray("[MEMORY] No preferences detected this session.");
            return;
        }

        Output.Gray($"[MEMORY] Extracted: \"{extracted}\"");

        VectorSearchOptions<SemanticNoteRecord> options = new()
        {
            Filter = r => r.Username == username
        };

        List<(Guid Id, string NoteType, string Content)> existing = [];
        await foreach (VectorSearchResult<SemanticNoteRecord> hit in
            noteCollection.SearchAsync("user preferences dislikes plan history notes", 20, options))
            existing.Add((hit.Record.Id, hit.Record.NoteType, hit.Record.Content));

        string existingJson = JsonSerializer.Serialize(
            existing.Select(e => new { Id = e.Id.ToString(), e.NoteType, e.Content }));

        ChatClientAgent consolidationAgent = chatClient.AsAIAgent(
            instructions:
                "You are a memory consolidation agent for a meal planner. " +
                "Merge new preference phrases with existing notes. " +
                "Deduplicate semantically equivalent facts (e.g. 'no avocado' = 'avoid avocado'). " +
                "Resolve conflicts — newer fact wins. " +
                $"Classify each note type: \"{NoteType.Dislike}\", \"{NoteType.Preference}\", or \"{NoteType.PlanHistory}\". " +
                "Return ONLY valid JSON: { \"NotesToAdd\": [{\"NoteType\":\"...\",\"Content\":\"...\"}], \"NoteIdsToDelete\": [\"guid\"] }");

        string consolidationInput =
            $"New phrases this session: {extracted}\n" +
            $"Existing notes: {existingJson}";

        AgentResponse<ConsolidatedMemory> consolidateResult =
            await consolidationAgent.RunAsync<ConsolidatedMemory>(consolidationInput);
        ConsolidatedMemory consolidated = consolidateResult.Result;

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (NoteToAdd note in consolidated.NotesToAdd)
        {
            await noteCollection.UpsertAsync(new SemanticNoteRecord
            {
                Id = Guid.NewGuid(),
                Username = username,
                NoteType = note.NoteType,
                Content = note.Content,
                Source = "consolidation",
                CreatedDate = today
            });
        }

        foreach (string staleId in consolidated.NoteIdsToDelete)
        {
            if (Guid.TryParse(staleId, out Guid id))
            {
                await noteCollection.UpsertAsync(new SemanticNoteRecord
                {
                    Id = id,
                    Username = username,
                    NoteType = "merged",
                    Content = "[merged — superseded by newer note]",
                    Source = "consolidation",
                    CreatedDate = today
                });
            }
        }

        Output.Green($"[MEMORY] {consolidated.NotesToAdd.Count} note(s) added, " +
                     $"{consolidated.NoteIdsToDelete.Count} replaced.");
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    private static void PrintWelcomeBack(UserProfileRecord profile)
    {
        Console.WriteLine();
        Output.Green($"Welcome back, {Capitalise(profile.Username)}!");
        Output.Blue($"Last session: {profile.LastSessionDate}  |  Sessions: {profile.TotalSessionsCount}");
        Console.WriteLine($"  Diet        : {profile.PreferredDietType}");
        Console.WriteLine($"  Calories    : {profile.DefaultCalories} kcal/day");
        Console.WriteLine($"  Budget      : ${profile.DefaultBudget} USD");
        Console.WriteLine($"  Plan days   : {profile.DefaultPlanDays}");
        if (!string.IsNullOrWhiteSpace(profile.FoodRestrictions))
            Console.WriteLine($"  Restrictions: {profile.FoodRestrictions}");
        Output.Gray("Press Enter to keep preferences, or type new values.");
    }

    private static void PrintSessionSummary(
        string username, string diet, int days, int calories, double budget,
        UserProfileRecord profile)
    {
        Output.Separator();
        Console.WriteLine($"User      : {username}");
        Console.WriteLine($"Diet      : {diet}");
        Console.WriteLine($"Days      : {days}");
        Console.WriteLine($"Calories  : {calories} kcal/day");
        Console.WriteLine($"Budget    : ${budget} USD");
        if (!string.IsNullOrWhiteSpace(profile.FoodRestrictions))
            Console.WriteLine($"Restrict  : {profile.FoodRestrictions}");
        Output.Separator();
    }

    private static void CheckBudget(decimal totalCost, double limit)
    {
        if ((double)totalCost <= limit)
            Output.Green($"[BUDGET] ${totalCost:F2} — within ${limit:F2} ✓");
        else
            Output.Yellow($"[BUDGET] ${totalCost:F2} — exceeds ${limit:F2} ⚠ (kept)");
    }

    private static void PrintMealPlan(MealPlan plan)
    {
        Console.WriteLine($"Diet: {plan.DietType}  |  Days: {plan.NumberOfDays}  |  Est. ${plan.EstimatedTotalCost:F2}");
        Output.Separator();
        foreach (DayPlan day in plan.Days)
        {
            Output.Blue($"Day {day.DayNumber}");
            foreach (Meal meal in day.Meals)
            {
                Console.WriteLine(
                    $"  [{meal.MealType,-12}] {meal.Name,-30} " +
                    $"{meal.Calories,4} kcal  |  P:{meal.ProteinGrams}g C:{meal.CarbsGrams}g F:{meal.FatGrams}g  |  ${meal.EstimatedCost:F2}");
                if (meal.Ingredients.Count > 0)
                    Output.Gray("               Ingredients: " + string.Join(", ", meal.Ingredients));
            }
            Console.WriteLine();
        }
    }

    // ── Input helpers ─────────────────────────────────────────────────────────

    private static string ReadString(string prompt, string? defaultValue = null)
    {
        Console.Write(prompt);
        string input = Console.ReadLine() ?? string.Empty;
        return string.IsNullOrWhiteSpace(input) ? (defaultValue ?? string.Empty) : input.Trim();
    }

    private static int ReadInt(string prompt, int defaultValue, int min = 1, int max = int.MaxValue)
    {
        Console.Write(prompt);
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return defaultValue;
        return int.TryParse(input, out int v) && v >= min && v <= max ? v : defaultValue;
    }

    private static double ReadDouble(string prompt, double defaultValue)
    {
        Console.Write(prompt);
        string? input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return defaultValue;
        return double.TryParse(input, out double v) ? v : defaultValue;
    }

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── Diet knowledge ingestion ──────────────────────────────────────────────

    private static async Task RefreshDietKnowledgeAsync(
        VectorStoreCollection<Guid, DietKnowledgeRecord> collection)
    {
        await collection.EnsureCollectionDeletedAsync();
        await collection.EnsureCollectionExistsAsync();

        List<DietKnowledgeRecord> records =
        [
            new()
            {
                Id = Guid.NewGuid(), DietName = "Vegan",
                Rules = "Zero animal products. No meat, fish, dairy, eggs, or honey. Every meal 100% plant-based.",
                MacroGuidelines = "Protein 15–20% (legumes, tofu, tempeh, seitan), carbs 50–60% (whole grains, veg), fat 20–30% (nuts, seeds, avocado, olive oil).",
                TypicalIngredients = "Tofu, tempeh, lentils, chickpeas, black beans, brown rice, quinoa, oats, avocado, almonds, cashews, nutritional yeast, tahini, spinach, kale, sweet potato, broccoli.",
                FoodsToAvoid = "All meat and seafood, dairy, eggs, honey, gelatin.",
                BudgetTips = "Dried lentils and beans are cheapest protein. Buy grains in bulk. Seasonal veg costs far less. Avoid pre-packaged vegan substitutes."
            },
            new()
            {
                Id = Guid.NewGuid(), DietName = "Keto",
                Rules = "Very low carb, high fat. Carbs <50g/day (net <25g preferred). Fat ≥65% of calories. Moderate protein 20–25%. No grains, sugar, starchy veg, most fruit.",
                MacroGuidelines = "Fat ≥65%, protein 20–25%, carbs <10%. For 2000 kcal: ~145g fat, 100g protein, 50g carbs max.",
                TypicalIngredients = "Beef, pork, chicken, salmon, tuna, eggs, butter, ghee, heavy cream, cheddar, parmesan, avocado, olive oil, coconut oil, almonds, walnuts, broccoli, cauliflower, spinach, zucchini.",
                FoodsToAvoid = "All grains (bread, pasta, rice, oats), sugar, starchy veg (potato, sweet potato, peas), legumes, most fruits, beer, juice.",
                BudgetTips = "Cheaper fatty cuts (thighs not breasts, ground beef, pork shoulder). Eggs are most cost-effective. Frozen broccoli and cauliflower are cheap and keto-friendly."
            },
            new()
            {
                Id = Guid.NewGuid(), DietName = "Mediterranean",
                Rules = "Emphasise fish, olive oil, vegetables, legumes, whole grains. Red meat at most once in entire plan. Dairy in moderation (yoghurt, feta). No processed foods.",
                MacroGuidelines = "Fat 35–40% (olive oil, fish), carbs 40–45% (whole grains, legumes, veg), protein 15–20% (fish, legumes, dairy).",
                TypicalIngredients = "Salmon, sardines, tuna, shrimp, chicken, lentils, chickpeas, white beans, olive oil, whole wheat bread, brown rice, couscous, tomatoes, cucumber, spinach, feta, Greek yoghurt, olives, walnuts.",
                FoodsToAvoid = "Processed meats, ultra-processed snacks, fried foods, sugary drinks, refined grains, red meat more than once.",
                BudgetTips = "Canned fish (sardines, tuna) is far cheaper and equally nutritious. Dried legumes very affordable. Buy olive oil in large bottles."
            }
        ];

        int n = 0;
        foreach (DietKnowledgeRecord r in records)
        {
            Console.Write($"\rEmbedding {++n}/{records.Count}: {r.DietName}...");
            await collection.UpsertAsync(r);
        }
        Console.WriteLine();
        Output.Green("Diet knowledge embedded.");
    }

    private static UserProfileRecord NewProfile(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PreferredDietType = string.Empty,
        DefaultCalories = DefaultCaloriesPerDay,
        DefaultBudget = DefaultBudget,
        DefaultPlanDays = 3,
        FoodRestrictions = string.Empty,
        WeightGoal = string.Empty,
        LastSessionDate = string.Empty,
        TotalSessionsCount = 0
    };

    // ── Search tools (unchanged from V7) ─────────────────────────────────────

    private sealed class DietKnowledgeSearchTool(VectorStoreCollection<Guid, DietKnowledgeRecord> collection)
    {
        public async Task<string> Search(string query)
        {
            var sb = new StringBuilder();
            Output.Gray($"[RAG] Diet knowledge search: \"{query}\"");
            await foreach (VectorSearchResult<DietKnowledgeRecord> hit in collection.SearchAsync(query, 2))
            {
                Output.Gray($"  → {hit.Record.DietName} (score: {hit.Score:F3})");
                sb.AppendLine($"Diet: {hit.Record.DietName}");
                sb.AppendLine($"Rules: {hit.Record.Rules}");
                sb.AppendLine($"Macros: {hit.Record.MacroGuidelines}");
                sb.AppendLine($"Ingredients: {hit.Record.TypicalIngredients}");
                sb.AppendLine($"Avoid: {hit.Record.FoodsToAvoid}");
                sb.AppendLine($"Budget tips: {hit.Record.BudgetTips}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    private sealed class MealPlanSearchTool(
        VectorStoreCollection<Guid, MealRecord> collection,
        string username)
    {
        public async Task<string> Search(string query)
        {
            VectorSearchOptions<MealRecord> options = new()
            {
                Filter = r => r.Username == username
            };
            var sb = new StringBuilder();
            Output.Gray($"[RAG] Meal plan search: \"{query}\"");
            await foreach (VectorSearchResult<MealRecord> hit in collection.SearchAsync(query, 3, options))
            {
                Output.Gray($"  → Day {hit.Record.DayNumber} {hit.Record.MealType} — {hit.Record.Name} (score: {hit.Score:F3})");
                sb.AppendLine($"Day {hit.Record.DayNumber} {hit.Record.MealType}: {hit.Record.Name}");
                sb.AppendLine($"  {hit.Record.Calories} kcal | {hit.Record.Macros} | {hit.Record.EstimatedCost}");
                sb.AppendLine($"  Ingredients: {hit.Record.Ingredients}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    private sealed class UserMemorySearchTool(
        VectorStoreCollection<Guid, ConversationTurnRecord> turns,
        VectorStoreCollection<Guid, SemanticNoteRecord> notes,
        string username)
    {
        public async Task<string> Search(string query)
        {
            var sb = new StringBuilder();
            Output.Gray($"[MEMORY] User memory search: \"{query}\"");

            VectorSearchOptions<ConversationTurnRecord> turnOptions = new()
            {
                Filter = r => r.Username == username
            };
            int found = 0;
            await foreach (VectorSearchResult<ConversationTurnRecord> hit in
                turns.SearchAsync(query, 2, turnOptions))
            {
                found++;
                Output.Gray($"  → Past turn [{hit.Record.PlanContext}] (score: {hit.Score:F3})");
                sb.AppendLine($"Past session ({hit.Record.Timestamp[..10]}, {hit.Record.PlanContext}):");
                sb.AppendLine($"  You asked: {hit.Record.UserMessage}");
                sb.AppendLine($"  Advisor: {hit.Record.AgentResponse}");
                sb.AppendLine();
            }

            VectorSearchOptions<SemanticNoteRecord> noteOptions = new()
            {
                Filter = r => r.Username == username
            };
            await foreach (VectorSearchResult<SemanticNoteRecord> hit in
                notes.SearchAsync(query, 3, noteOptions))
            {
                if (hit.Record.NoteType == "merged") continue;
                found++;
                Output.Gray($"  → Note [{hit.Record.NoteType}]: {hit.Record.Content} (score: {hit.Score:F3})");
                sb.AppendLine($"Stored {hit.Record.NoteType}: {hit.Record.Content}");
            }

            return found == 0 ? "No relevant memory found." : sb.ToString();
        }
    }

    // ── Record types (unchanged from V7) ─────────────────────────────────────

    private sealed class UserProfileRecord
    {
        [VectorStoreKey] public required Guid Id { get; set; }
        [VectorStoreData] public required string Username { get; set; }
        [VectorStoreData] public required string PreferredDietType { get; set; }
        [VectorStoreData] public required int DefaultCalories { get; set; }
        [VectorStoreData] public required double DefaultBudget { get; set; }
        [VectorStoreData] public required int DefaultPlanDays { get; set; }
        [VectorStoreData] public required string FoodRestrictions { get; set; }
        [VectorStoreData] public required string WeightGoal { get; set; }
        [VectorStoreData] public required string LastSessionDate { get; set; }
        [VectorStoreData] public required int TotalSessionsCount { get; set; }

        [VectorStoreVector(1536)]
        public string Vector =>
            $"User {Username}: {PreferredDietType} diet, {DefaultCalories} kcal, " +
            $"${DefaultBudget} budget. Restrictions: {FoodRestrictions}. Goal: {WeightGoal}.";
    }

    private sealed class ConversationTurnRecord
    {
        [VectorStoreKey] public required Guid Id { get; set; }
        [VectorStoreData] public required string SessionId { get; set; }
        [VectorStoreData] public required int TurnNumber { get; set; }
        [VectorStoreData] public required string Username { get; set; }
        [VectorStoreData] public required string UserMessage { get; set; }
        [VectorStoreData] public required string AgentResponse { get; set; }
        [VectorStoreData] public required string Timestamp { get; set; }
        [VectorStoreData] public required string PlanContext { get; set; }

        [VectorStoreVector(1536)]
        public string Vector =>
            $"User asked: {UserMessage} | Advisor: {AgentResponse} | Context: {PlanContext}";
    }

    private sealed class SemanticNoteRecord
    {
        [VectorStoreKey] public required Guid Id { get; set; }
        [VectorStoreData] public required string Username { get; set; }
        [VectorStoreData] public required string NoteType { get; set; }
        [VectorStoreData] public required string Content { get; set; }
        [VectorStoreData] public required string Source { get; set; }
        [VectorStoreData] public required string CreatedDate { get; set; }

        [VectorStoreVector(1536)]
        public string Vector => $"User {Username} {NoteType}: {Content}";
    }

    // ── Structured output types ───────────────────────────────────────────────

    private sealed class MealPlan
    {
        public required string DietType { get; set; }
        public required int NumberOfDays { get; set; }
        public required decimal EstimatedTotalCost { get; set; }
        public required List<DayPlan> Days { get; set; }
    }

    private sealed class DayPlan
    {
        public required int DayNumber { get; set; }
        public required List<Meal> Meals { get; set; }
    }

    private sealed class Meal
    {
        public required string MealType { get; set; }
        public required string Name { get; set; }
        public required int Calories { get; set; }
        public required int ProteinGrams { get; set; }
        public required int CarbsGrams { get; set; }
        public required int FatGrams { get; set; }
        public required decimal EstimatedCost { get; set; }
        public required List<string> Ingredients { get; set; }
    }

    private sealed class NutritionCritique
    {
        public required bool Approved { get; set; }
        public required List<string> DietViolations { get; set; }
        public required List<string> MacroIssues { get; set; }
        public required List<string> Suggestions { get; set; }
    }

    private sealed class DietKnowledgeRecord
    {
        [VectorStoreKey] public required Guid Id { get; set; }
        [VectorStoreData] public required string DietName { get; set; }
        [VectorStoreData] public required string Rules { get; set; }
        [VectorStoreData] public required string MacroGuidelines { get; set; }
        [VectorStoreData] public required string TypicalIngredients { get; set; }
        [VectorStoreData] public required string FoodsToAvoid { get; set; }
        [VectorStoreData] public required string BudgetTips { get; set; }

        [VectorStoreVector(1536)]
        public string Vector =>
            $"{DietName}: {Rules}. Macros: {MacroGuidelines}. Ingredients: {TypicalIngredients}";
    }

    private sealed class MealRecord
    {
        [VectorStoreKey]  public required Guid   Id            { get; set; }
        [VectorStoreData] public required string Username      { get; set; }
        [VectorStoreData] public required int    DayNumber     { get; set; }
        [VectorStoreData] public required string MealType      { get; set; }
        [VectorStoreData] public required string Name          { get; set; }
        [VectorStoreData] public required int    Calories      { get; set; }
        [VectorStoreData] public required string Macros        { get; set; }
        [VectorStoreData] public required string EstimatedCost { get; set; }
        [VectorStoreData] public required string Ingredients   { get; set; }

        [VectorStoreVector(1536)]
        public string Vector =>
            $"Day {DayNumber} {MealType}: {Name}. Ingredients: {Ingredients}. " +
            $"Calories: {Calories} kcal. {Macros}. Cost: {EstimatedCost}.";
    }

    private sealed class ConsolidatedMemory
    {
        public required List<NoteToAdd> NotesToAdd { get; set; }
        public required List<string> NoteIdsToDelete { get; set; }
    }

    private sealed class NoteToAdd
    {
        public required string NoteType { get; set; }
        public required string Content { get; set; }
    }

    // ── NoteType constants ────────────────────────────────────────────────────

    private static class NoteType
    {
        public const string Dislike     = "dislike";
        public const string Preference  = "preference";
        public const string PlanHistory = "plan_history";
    }
}
