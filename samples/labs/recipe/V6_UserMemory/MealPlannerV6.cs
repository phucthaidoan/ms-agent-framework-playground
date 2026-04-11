// ReSharper disable ClassNeverInstantiated.Local

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text;
using System.Text.Json;

namespace Samples.Labs.Recipe.V6_UserMemory;

// V6: Per-User Memory
// New concept: user memory as a vector store collection — the same SearchAsync / UpsertAsync pattern
// applied not to domain knowledge or plan content, but to user-owned, session-spanning state.
//   1. At startup, the user's name is looked up in `meal_user_memory`. A similarity score ≥ 0.85
//      means "returning user" — preferences are loaded and input prompts are pre-filled.
//   2. Personal food restrictions flow into the planner (hard constraint), critic (check #7), and
//      advisor instructions — memory reaches all the way into the validation layer.
//   3. After the advisor loop, a PreferencesExtractionAgent distils the conversation into structured
//      notes that are persisted for next time — LLM used for extraction, not just generation.
// Compare with V5 where every session started completely fresh with no user context.

public static class MealPlannerV6
{
    private const int MaxIterations = 3;
    private const int DefaultCaloriesPerDay = 2000;
    private const double DefaultBudget = 50.0;
    private const double UserMemoryScoreThreshold = 0.85;
    private const int MaxAdvisorNotes = 5;
    private const int MaxPlanHistory = 5;

    public static async Task RunSample()
    {
        Output.Title("Meal Planner V6 — Per-User Memory");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = client
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator();

        string connectionString = $"Data Source={Path.GetTempPath()}\\af-course-vector-store.db";
        VectorStore vectorStore = new SqliteVectorStore(connectionString, new SqliteVectorStoreOptions
        {
            EmbeddingGenerator = embeddingGenerator
        });

        VectorStoreCollection<Guid, DietKnowledgeRecord> dietCollection =
            vectorStore.GetCollection<Guid, DietKnowledgeRecord>("meal_diet_profiles");

        VectorStoreCollection<Guid, MealRecord> mealCollection =
            vectorStore.GetCollection<Guid, MealRecord>("meal_plan_meals");

        // KEY CONCEPT: user memory collection — never deleted, accumulates across sessions
        VectorStoreCollection<Guid, UserMemoryRecord> userCollection =
            vectorStore.GetCollection<Guid, UserMemoryRecord>("meal_user_memory");

        await dietCollection.EnsureCollectionExistsAsync();
        await mealCollection.EnsureCollectionExistsAsync();
        await userCollection.EnsureCollectionExistsAsync();

        // --- Phase 0: User identity + memory lookup ---
        // KEY CONCEPT: username is looked up via SearchAsync — similarity score decides new vs. returning user.
        // No auth system needed; the vector field encodes the user's dietary identity for fuzzy matching.
        Console.Write("Enter your name (new or returning user): ");
        string rawName = Console.ReadLine() ?? string.Empty;
        string username = rawName.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
        {
            Output.Red("No name provided. Exiting.");
            return;
        }

        Output.Gray($"[MEMORY] Searching user memory for: \"{username}\"");
        UserMemoryRecord? userMemory = null;

        // KEY CONCEPT: score threshold drives a business decision — is this a new or returning user?
        await foreach (VectorSearchResult<UserMemoryRecord> hit in userCollection.SearchAsync(username, 1))
        {
            if (hit.Score >= UserMemoryScoreThreshold)
            {
                userMemory = hit.Record;
                Output.Gray($"  → Retrieved: {hit.Record.Username} (score: {hit.Score:F3})");
            }
            else
            {
                Output.Gray($"  → No match above threshold (score: {hit.Score:F3}) — new user");
            }
        }

        if (userMemory is not null)
        {
            // Returning user: show what was remembered
            Console.WriteLine();
            Output.Green($"Welcome back, {char.ToUpperInvariant(userMemory.Username[0])}{userMemory.Username[1..]}!");
            Output.Blue($"Remembered from your last session ({userMemory.LastSessionDate}, {userMemory.TotalSessionsCount} session(s) total):");
            Console.WriteLine($"  Diet        : {userMemory.PreferredDietType}");
            Console.WriteLine($"  Calories    : {userMemory.DefaultCalories} kcal/day");
            Console.WriteLine($"  Budget      : ${userMemory.DefaultBudget} USD");
            Console.WriteLine($"  Plan days   : {userMemory.DefaultPlanDays}");
            if (!string.IsNullOrWhiteSpace(userMemory.FoodRestrictions))
                Console.WriteLine($"  Restrictions: {userMemory.FoodRestrictions}");
            if (!string.IsNullOrWhiteSpace(userMemory.DislikedIngredients))
                Console.WriteLine($"  Dislikes    : {userMemory.DislikedIngredients}");
            if (!string.IsNullOrWhiteSpace(userMemory.AdvisorNotes))
                Console.WriteLine($"  Your notes  : \"{userMemory.AdvisorNotes.Split('|')[0]}\"");
            Output.Gray("Press Enter to keep these preferences, or enter new values below.");
            Console.WriteLine();
        }
        else
        {
            // First-time user: create a fresh record
            userMemory = new UserMemoryRecord
            {
                Id = Guid.NewGuid(),
                Username = username,
                PreferredDietType = string.Empty,
                DefaultCalories = DefaultCaloriesPerDay,
                DefaultBudget = DefaultBudget,
                DefaultPlanDays = 3,
                FoodRestrictions = string.Empty,
                DislikedIngredients = string.Empty,
                AdvisorNotes = string.Empty,
                PlanHistorySummary = string.Empty,
                WeightGoal = string.Empty,
                LastSessionDate = string.Empty,
                TotalSessionsCount = 0
            };
            Console.WriteLine();
            Output.Green($"Welcome! This is your first session, {char.ToUpperInvariant(username[0])}{username[1..]}.");
            Output.Gray("Let's set up your meal preferences.");
            Console.WriteLine();
        }

        Output.Separator();

        // --- Phase 1: Diet knowledge ingestion (unchanged from V5) ---
        Console.Write("Import/refresh diet knowledge base? (Y/N): ");
        ConsoleKeyInfo key = Console.ReadKey();
        Console.WriteLine();

        if (key.Key == ConsoleKey.Y)
        {
            await dietCollection.EnsureCollectionDeletedAsync();
            await dietCollection.EnsureCollectionExistsAsync();

            List<DietKnowledgeRecord> dietRecords =
            [
                new DietKnowledgeRecord
                {
                    Id = Guid.NewGuid(),
                    DietName = "Vegan",
                    Rules = "Zero animal products. No meat, fish, dairy, eggs, or honey. Every meal must be 100% plant-based.",
                    MacroGuidelines = "Balanced macros: protein 15-20% (from legumes, tofu, tempeh, seitan), carbs 50-60% (whole grains, vegetables), fat 20-30% (nuts, seeds, avocado, olive oil).",
                    TypicalIngredients = "Tofu, tempeh, lentils, chickpeas, black beans, brown rice, quinoa, oats, avocado, almonds, cashews, nutritional yeast, tahini, flaxseeds, chia seeds, spinach, kale, sweet potato, broccoli, mushrooms.",
                    FoodsToAvoid = "All meat (beef, pork, poultry, lamb), all seafood, dairy (milk, cheese, butter, yoghurt), eggs, honey, gelatin, any ingredient derived from animals.",
                    BudgetTips = "Dried beans and lentils are the cheapest protein sources. Buy grains in bulk. Seasonal vegetables are significantly cheaper. Tofu and tempeh are cost-effective proteins. Avoid pre-packaged vegan substitutes — they are expensive."
                },
                new DietKnowledgeRecord
                {
                    Id = Guid.NewGuid(),
                    DietName = "Keto",
                    Rules = "Very low carbohydrate, high fat diet. Total carbs must stay under 50g per day (net carbs under 25g preferred). Fat must provide at least 65% of daily calories. Moderate protein (20-25% of calories). No grains, sugar, starchy vegetables, or most fruit.",
                    MacroGuidelines = "Fat ≥65% of daily calories, protein 20-25%, carbs <10% (<50g/day). Example for 2000 kcal: 145g fat, 100g protein, 50g carbs max.",
                    TypicalIngredients = "Beef, pork, chicken, salmon, tuna, eggs, butter, ghee, heavy cream, hard cheese (cheddar, parmesan), avocado, olive oil, coconut oil, almonds, walnuts, macadamia nuts, broccoli, cauliflower, spinach, zucchini, asparagus, mushrooms, berries (small amounts).",
                    FoodsToAvoid = "All grains (bread, pasta, rice, oats, corn), sugar and sweeteners (except stevia/erythritol), starchy vegetables (potato, sweet potato, parsnip, peas), legumes (beans, lentils, chickpeas), most fruits (except berries in small amounts), beer, juice, sodas.",
                    BudgetTips = "Buy cheaper cuts of fatty meat (chicken thighs not breasts, ground beef, pork shoulder). Eggs are the most cost-effective keto protein. Buy nuts and seeds in bulk. Frozen broccoli and cauliflower are cheap and keto-friendly. Avocados when on sale."
                },
                new DietKnowledgeRecord
                {
                    Id = Guid.NewGuid(),
                    DietName = "Mediterranean",
                    Rules = "Emphasise fish (especially oily fish), olive oil, vegetables, legumes, and whole grains. Red meat limited to at most once in the entire plan. Dairy allowed in moderation (yoghurt, feta). No processed or ultra-processed foods.",
                    MacroGuidelines = "Balanced macros: fat 35-40% (primarily olive oil and fish), carbs 40-45% (whole grains, legumes, vegetables), protein 15-20% (fish, legumes, dairy). Favour unsaturated fats over saturated.",
                    TypicalIngredients = "Salmon, sardines, tuna, shrimp, chicken, lentils, chickpeas, white beans, olive oil, whole wheat bread, brown rice, couscous, farro, tomatoes, cucumber, spinach, eggplant, zucchini, red pepper, onion, garlic, feta cheese, Greek yoghurt, olives, walnuts, almonds.",
                    FoodsToAvoid = "Processed meats (sausage, deli meats, bacon), ultra-processed snacks, fried foods (except occasional), sugary drinks, refined grains (white bread, white pasta), red meat more than once per plan.",
                    BudgetTips = "Canned fish (sardines, tuna, salmon) is far cheaper than fresh and equally nutritious. Dried legumes (chickpeas, lentils) are very affordable. Seasonal vegetables cost much less. Olive oil in larger bottles reduces cost per use. Whole grains in bulk."
                }
            ];

            int count = 0;
            foreach (DietKnowledgeRecord record in dietRecords)
            {
                count++;
                Console.Write($"\rEmbedding diet knowledge {count}/{dietRecords.Count}: {record.DietName}...");
                await dietCollection.UpsertAsync(record);
            }

            Console.WriteLine();
            Output.Green("Diet knowledge embedded successfully.");
        }

        Output.Separator();

        // --- Phase 2: User input — pre-filled for returning users ---
        bool isReturning = !string.IsNullOrWhiteSpace(userMemory.PreferredDietType);

        string dietPrompt = isReturning
            ? $"Describe your diet preference (press Enter to keep '{userMemory.PreferredDietType}'): "
            : "Describe your diet preference (e.g. 'Keto', 'plant-based vegan', 'Mediterranean'): ";
        Console.Write(dietPrompt);
        string dietInput = Console.ReadLine() ?? string.Empty;
        string dietDescription = string.IsNullOrWhiteSpace(dietInput) && isReturning
            ? userMemory.PreferredDietType
            : dietInput;

        if (string.IsNullOrWhiteSpace(dietDescription))
        {
            Output.Red("No diet preference provided. Exiting.");
            return;
        }

        string daysPrompt = isReturning
            ? $"How many days to plan? (press Enter to keep {userMemory.DefaultPlanDays}): "
            : "How many days to plan? (1–7): ";
        Console.Write(daysPrompt);
        string? daysInput = Console.ReadLine();
        int numberOfDays;
        if (string.IsNullOrWhiteSpace(daysInput) && isReturning)
            numberOfDays = userMemory.DefaultPlanDays;
        else if (!int.TryParse(daysInput, out numberOfDays) || numberOfDays < 1 || numberOfDays > 7)
        {
            Output.Red("Invalid number of days. Exiting.");
            return;
        }

        string caloriesPrompt = isReturning
            ? $"Daily calorie target? (press Enter to keep {userMemory.DefaultCalories}): "
            : $"Daily calorie target? (press Enter for {DefaultCaloriesPerDay}): ";
        Console.Write(caloriesPrompt);
        string? caloriesInput = Console.ReadLine();
        int targetCalories = string.IsNullOrWhiteSpace(caloriesInput)
            ? (isReturning ? userMemory.DefaultCalories : DefaultCaloriesPerDay)
            : int.TryParse(caloriesInput, out int c) ? c : (isReturning ? userMemory.DefaultCalories : DefaultCaloriesPerDay);

        string budgetPrompt = isReturning
            ? $"Total budget in USD? (press Enter to keep ${userMemory.DefaultBudget}): "
            : $"Total budget in USD? (press Enter for ${DefaultBudget}): ";
        Console.Write(budgetPrompt);
        string? budgetInput = Console.ReadLine();
        double budget = string.IsNullOrWhiteSpace(budgetInput)
            ? (isReturning ? userMemory.DefaultBudget : DefaultBudget)
            : double.TryParse(budgetInput, out double b) ? b : (isReturning ? userMemory.DefaultBudget : DefaultBudget);

        // Optional: food restrictions (only ask if not already set, or allow appending)
        if (string.IsNullOrWhiteSpace(userMemory.FoodRestrictions))
        {
            Console.Write("Any food restrictions or allergies? (e.g. 'no nuts, gluten-free' or press Enter to skip): ");
            string? restrictionsInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(restrictionsInput))
                userMemory.FoodRestrictions = restrictionsInput.Trim();
        }

        Output.Separator();
        Console.WriteLine($"User      : {username}");
        Console.WriteLine($"Diet      : {dietDescription}");
        Console.WriteLine($"Days      : {numberOfDays}");
        Console.WriteLine($"Calories  : {targetCalories} kcal/day");
        Console.WriteLine($"Budget    : ${budget} USD total");
        if (!string.IsNullOrWhiteSpace(userMemory.FoodRestrictions))
            Console.WriteLine($"Restrict  : {userMemory.FoodRestrictions}");
        if (!string.IsNullOrWhiteSpace(userMemory.DislikedIngredients))
            Console.WriteLine($"Dislikes  : {userMemory.DislikedIngredients}");
        Output.Separator();

        // --- Phase 3: Build memory context strings for agent instructions ---
        // KEY CONCEPT: user memory flows into agent instructions — not hardcoded, retrieved per user
        StringBuilder memoryForPlanner = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(userMemory.FoodRestrictions))
            memoryForPlanner.Append($"HARD RESTRICTIONS for this user (allergies/intolerances — must be enforced in every meal): {userMemory.FoodRestrictions}. ");
        if (!string.IsNullOrWhiteSpace(userMemory.DislikedIngredients))
            memoryForPlanner.Append($"The user dislikes these ingredients — avoid where possible: {userMemory.DislikedIngredients}. ");
        if (!string.IsNullOrWhiteSpace(userMemory.PlanHistorySummary))
            memoryForPlanner.Append($"Previous plans for this user: {userMemory.PlanHistorySummary}. Aim for variety — do not repeat the same meals. ");
        if (!string.IsNullOrWhiteSpace(userMemory.AdvisorNotes))
            memoryForPlanner.Append($"Past feedback from this user: {userMemory.AdvisorNotes.Replace('|', ',')}. ");
        if (!string.IsNullOrWhiteSpace(userMemory.WeightGoal))
            memoryForPlanner.Append($"User goal: {userMemory.WeightGoal}. ");

        string criticRestrictionCheck = string.IsNullOrWhiteSpace(userMemory.FoodRestrictions)
            ? string.Empty
            : $"7. USER RESTRICTION CHECK: This user has the following restrictions: {userMemory.FoodRestrictions}. " +
              $"Any meal containing a restricted ingredient must be flagged as a diet violation, even if it is otherwise diet-compliant. ";

        string narrativeMemoryContext = string.Empty;
        if (!string.IsNullOrWhiteSpace(userMemory.FoodRestrictions))
            narrativeMemoryContext += $"The user has the following restrictions: {userMemory.FoodRestrictions}. Do not mention these foods. ";
        if (!string.IsNullOrWhiteSpace(userMemory.AdvisorNotes))
            narrativeMemoryContext += $"The user has previously requested: {userMemory.AdvisorNotes.Replace('|', ',')}. Reflect these preferences in your narrative. ";

        string advisorMemoryContext = string.Empty;
        if (!string.IsNullOrWhiteSpace(userMemory.PreferredDietType))
            advisorMemoryContext =
                $"User context: {username} prefers {userMemory.PreferredDietType}, " +
                $"targets {userMemory.DefaultCalories} kcal/day, budget ${userMemory.DefaultBudget}. " +
                (string.IsNullOrWhiteSpace(userMemory.FoodRestrictions) ? string.Empty : $"Restrictions: {userMemory.FoodRestrictions}. ") +
                (string.IsNullOrWhiteSpace(userMemory.DislikedIngredients) ? string.Empty : $"Dislikes: {userMemory.DislikedIngredients}. ") +
                (string.IsNullOrWhiteSpace(userMemory.AdvisorNotes) ? string.Empty : $"Past requests: {userMemory.AdvisorNotes.Replace('|', ',')}. ");

        // --- Phase 4: Streaming narrative preview ---
        Output.Title("Step 1: Streaming plan preview...");
        Console.WriteLine();

        ChatClientAgent narrativeAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a professional meal planner. " +
                $"{narrativeMemoryContext}" +
                $"Describe the {numberOfDays}-day meal plan you are about to create for someone requesting a '{dietDescription}' diet. " +
                $"Explain the overall theme, key ingredients you will use, how you will distribute the {targetCalories} kcal " +
                $"daily target across meals, and how you will stay within the ${budget} total budget. " +
                $"Write in flowing prose, 2-3 paragraphs. This is a planning preview.");

        await foreach (AgentResponseUpdate update in narrativeAgent.RunStreamingAsync(
            $"Describe your approach for a {numberOfDays}-day '{dietDescription}' meal plan " +
            $"targeting {targetCalories} kcal/day within a ${budget} total budget."))
        {
            Console.Write(update);
        }

        Console.WriteLine();

        // --- Phase 5: Build RAG-powered planner + critic (enriched with user memory) ---
        DietKnowledgeSearchTool dietSearchTool = new DietKnowledgeSearchTool(dietCollection);

        ChatClientAgent plannerAgent = client
            .GetChatClient("gpt-4.1-nano")
            // KEY CONCEPT: planner grounded by retrieval (V5) AND personalised by user memory (V6)
            .AsAIAgent(
                instructions:
                    $"You are a professional meal planner. " +
                    $"{memoryForPlanner}" +
                    $"Use the search_diet_knowledge tool to retrieve the specific dietary rules, macro guidelines, " +
                    $"typical ingredients, and foods to avoid for the requested diet — then generate the meal plan. " +
                    $"Generate a {numberOfDays}-day meal plan. Each day MUST contain exactly 3 meals: Breakfast, Lunch, and Dinner. " +
                    $"Calorie target: {targetCalories} kcal per day (Breakfast ~25%, Lunch ~35%, Dinner ~40%). " +
                    $"Total budget: ${budget} USD across all {numberOfDays} days (approx ${budget / numberOfDays:F2}/day). " +
                    $"Each meal must include: a descriptive name, realistic calorie count, protein/carbs/fat in grams, estimated cost in USD, and 4-6 specific ingredients.",
                tools:
                [
                    AIFunctionFactory.Create(
                        dietSearchTool.Search,
                        "search_diet_knowledge",
                        "Search the diet knowledge base for dietary rules, macro guidelines, typical ingredients, and foods to avoid for a given diet type.")
                ]);

        ChatClientAgent criticAgent = client
            .GetChatClient("gpt-4.1-nano")
            // KEY CONCEPT: critic enforces user-specific restrictions (check #7) — memory reaches the validation layer
            .AsAIAgent(instructions:
                $"You are a strict nutrition and diet compliance reviewer. " +
                $"You receive a meal plan and must verify ALL of the following: " +
                $"1. Each day has exactly 3 meals (Breakfast, Lunch, Dinner). " +
                $"2. Each day totals approximately {targetCalories} kcal (±200 kcal tolerance). " +
                $"3. No meal exceeds 50% of the daily calorie target. " +
                $"4. Total estimated cost does not exceed ${budget} USD. " +
                $"5. Standard diet compliance: " +
                $"     Vegan         — zero animal products in any meal. " +
                $"     Keto          — carbs <50g/day total; fat calories ≥65% of daily total. " +
                $"     Mediterranean — no processed food; red meat at most once in the entire plan. " +
                $"6. Every meal has a name, calories, protein, carbs, fat, cost, and at least one ingredient. " +
                $"{criticRestrictionCheck}" +
                $"Approve only if ALL checks pass. List every violation found, even minor ones.");

        // --- Phase 6: Host-orchestrated refinement loop ---
        AgentSession plannerSession = await plannerAgent.CreateSessionAsync();

        Output.Separator();
        Output.Title("Step 2: Generating structured plan with refinement loop...");
        Output.Yellow("[PLANNER] Calling MealPlannerAgent...");

        AgentResponse<MealPlan> initialResponse = await plannerAgent.RunAsync<MealPlan>(
            $"Generate a {numberOfDays}-day '{dietDescription}' meal plan targeting {targetCalories} kcal/day " +
            $"with a total budget of ${budget} USD. " +
            $"Use the search_diet_knowledge tool to retrieve the appropriate dietary rules first.",
            plannerSession);

        string planJson = initialResponse.Text;
        MealPlan plan = initialResponse.Result;
        Output.Green($"[PLANNER] Initial plan generated ({plan.Days.Count} days, est. ${plan.EstimatedTotalCost:F2})");

        for (int iteration = 1; iteration <= MaxIterations; iteration++)
        {
            Output.Separator();
            Output.Yellow($"[ITERATION {iteration}/{MaxIterations}] Calling NutritionCriticAgent...");

            AgentResponse<NutritionCritique> critiqueResponse =
                await criticAgent.RunAsync<NutritionCritique>(planJson);

            NutritionCritique critique = critiqueResponse.Result;

            if (critique.Approved)
            {
                Output.Green("[CRITIC] Approved ✓");
                break;
            }

            Output.Red("[CRITIC] Not approved. Issues found:");
            foreach (string v in critique.DietViolations)
                Output.Red($"  • {v}");
            foreach (string m in critique.MacroIssues)
                Output.Red($"  • {m}");

            if (critique.Suggestions.Count > 0)
            {
                Output.Yellow("[CRITIC] Suggestions for planner:");
                foreach (string s in critique.Suggestions)
                    Output.Yellow($"  → {s}");
            }

            if (iteration == MaxIterations)
            {
                Output.Yellow("[PLANNER] Max iterations reached — keeping last plan.");
                break;
            }

            Output.Yellow("[PLANNER] Refining based on critic feedback...");

            string refineFeedback =
                $"Your previous plan was rejected. Fix ALL of the following issues and return a corrected JSON plan.\n" +
                $"Diet violations: {string.Join("; ", critique.DietViolations)}\n" +
                $"Macro issues: {string.Join("; ", critique.MacroIssues)}\n" +
                $"Suggestions: {string.Join("; ", critique.Suggestions)}\n\n" +
                $"Previous plan:\n{planJson}";

            AgentResponse<MealPlan> refinedResponse = await plannerAgent.RunAsync<MealPlan>(refineFeedback, plannerSession);
            planJson = refinedResponse.Text;
            plan = refinedResponse.Result;
            Output.Green($"[PLANNER] Plan refined (est. ${plan.EstimatedTotalCost:F2})");
        }

        // --- Phase 7: Budget check + print ---
        Output.Separator();
        Output.Title("Step 3: Checking budget...");
        CheckBudget(plan.EstimatedTotalCost, budget);

        Output.Separator();
        Output.Title("Step 4: Final Meal Plan");
        PrintMealPlan(plan);

        // --- Phase 8: First memory write — save preferences + plan history ---
        // KEY CONCEPT: UpsertAsync is idempotent — same Id creates on first run, overwrites on return runs
        Output.Separator();
        Output.Yellow($"[MEMORY] Saving plan preferences for {username}...");

        userMemory.PreferredDietType = dietDescription;
        userMemory.DefaultCalories = targetCalories;
        userMemory.DefaultBudget = budget;
        userMemory.DefaultPlanDays = numberOfDays;
        userMemory.TotalSessionsCount++;
        userMemory.LastSessionDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Append plan history (cap at MaxPlanHistory, drop oldest)
        List<PlanHistoryEntry> history = string.IsNullOrWhiteSpace(userMemory.PlanHistorySummary)
            ? []
            : JsonSerializer.Deserialize<List<PlanHistoryEntry>>(userMemory.PlanHistorySummary) ?? [];
        history.Insert(0, new PlanHistoryEntry
        {
            Date = userMemory.LastSessionDate,
            Diet = dietDescription,
            Days = numberOfDays,
            Cost = plan.EstimatedTotalCost
        });
        if (history.Count > MaxPlanHistory)
            history = history.Take(MaxPlanHistory).ToList();
        userMemory.PlanHistorySummary = JsonSerializer.Serialize(history);

        await userCollection.UpsertAsync(userMemory);
        Output.Green($"[MEMORY] Plan preferences saved for {username}.");

        // --- Phase 9: Index approved plan meals into vector store (unchanged from V5) ---
        Output.Separator();
        Output.Yellow("[RAG] Indexing approved meal plan for advisor queries...");

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
                    Id = Guid.NewGuid(),
                    DayNumber = day.DayNumber,
                    MealType = meal.MealType,
                    Name = meal.Name,
                    Calories = meal.Calories,
                    Macros = $"P:{meal.ProteinGrams}g C:{meal.CarbsGrams}g F:{meal.FatGrams}g",
                    EstimatedCost = $"${meal.EstimatedCost:F2}",
                    Ingredients = string.Join(", ", meal.Ingredients)
                });
            }
        }

        Output.Green($"[RAG] {mealCount} meals indexed for advisor queries.");

        // --- Phase 10: Multi-turn meal advisor (enriched with user context) ---
        Output.Separator();
        Output.Title("Step 5: Meal Plan Advisor (type 'exit' to quit)");

        MealPlanSearchTool mealSearchTool = new MealPlanSearchTool(mealCollection);

        ChatClientAgent mealAdvisorAgent = client
            .GetChatClient("gpt-4.1-nano")
            // KEY CONCEPT: advisor instructions prepended with user context from memory
            .AsAIAgent(
                instructions:
                    $"{advisorMemoryContext}" +
                    $"You are a friendly and knowledgeable meal plan advisor specialising in '{dietDescription}' eating. " +
                    $"Use the search_meal_plan tool to look up specific meals from the approved {numberOfDays}-day plan " +
                    $"before answering questions. Do not guess meal details — always retrieve first. " +
                    $"Respect the calorie target of {targetCalories} kcal/day and budget of ${budget} USD total. " +
                    $"Be practical and concise.",
                tools:
                [
                    AIFunctionFactory.Create(
                        mealSearchTool.Search,
                        "search_meal_plan",
                        "Search the approved meal plan for specific meals, days, ingredients, or nutritional information.")
                ]);

        AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();
        List<string> advisorTranscript = [];

        Output.Gray("Meal plan indexed. Ask your questions (type 'exit' to finish):");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine() ?? string.Empty;

            if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Accumulate user turns for extraction later
            advisorTranscript.Add(input);

            AgentResponse response = await mealAdvisorAgent.RunAsync(input, advisorSession);
            Console.WriteLine(response.Text);
            Output.Separator(false);
        }

        // --- Phase 11: Second memory write — extract preferences from advisor conversation ---
        // KEY CONCEPT: LLM used for extraction, not just generation — distils conversation into structured notes
        if (advisorTranscript.Count > 0)
        {
            Output.Yellow("[MEMORY] Extracting preferences from advisor conversation...");

            ChatClientAgent extractionAgent = client
                .GetChatClient("gpt-4.1-nano")
                .AsAIAgent(instructions:
                    "You are a preferences extractor. Read the following meal advisor conversation (user turns only) " +
                    "and extract only the user's stated preferences, dislikes, or requests for future plans. " +
                    "Return a comma-separated list of concise phrases (e.g. 'avoid mushrooms, more fish lunches'), " +
                    "or an empty string if none found. Return ONLY the comma-separated list, no explanation.");

            AgentResponse extractResult = await extractionAgent.RunAsync(string.Join("\n", advisorTranscript));
            string extracted = extractResult.Text.Trim();

            if (!string.IsNullOrWhiteSpace(extracted))
            {
                Output.Gray($"  → Extracted: \"{extracted}\"");

                // Append to AdvisorNotes (pipe-separated, newest first, cap at MaxAdvisorNotes)
                List<string> notes = string.IsNullOrWhiteSpace(userMemory.AdvisorNotes)
                    ? []
                    : [.. userMemory.AdvisorNotes.Split('|', StringSplitOptions.RemoveEmptyEntries)];
                notes.Insert(0, extracted);
                if (notes.Count > MaxAdvisorNotes)
                    notes = notes.Take(MaxAdvisorNotes).ToList();
                userMemory.AdvisorNotes = string.Join("|", notes);

                // Append to DislikedIngredients (deduplicated)
                HashSet<string> dislikes = string.IsNullOrWhiteSpace(userMemory.DislikedIngredients)
                    ? []
                    : [.. userMemory.DislikedIngredients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                foreach (string phrase in extracted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (phrase.StartsWith("avoid ", StringComparison.OrdinalIgnoreCase) ||
                        phrase.StartsWith("no ", StringComparison.OrdinalIgnoreCase) ||
                        phrase.StartsWith("don't like ", StringComparison.OrdinalIgnoreCase))
                    {
                        dislikes.Add(phrase);
                    }
                }
                userMemory.DislikedIngredients = string.Join(", ", dislikes);

                await userCollection.UpsertAsync(userMemory);
                Output.Green("[MEMORY] Preferences updated from advisor conversation.");
            }
            else
            {
                Output.Gray("[MEMORY] No new preferences detected in conversation.");
            }
        }

        Output.Separator();
        Output.Green("Session ended. Meal planning complete.");
    }

    private static void CheckBudget(decimal totalCost, double budgetLimit)
    {
        if ((double)totalCost <= budgetLimit)
            Output.Green($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Within limit of ${budgetLimit:F2} ✓");
        else
            Output.Yellow($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Exceeds limit of ${budgetLimit:F2} ⚠ (plan kept)");
    }

    private static void PrintMealPlan(MealPlan plan)
    {
        Console.WriteLine($"Diet: {plan.DietType}  |  Days: {plan.NumberOfDays}  |  Est. Total: ${plan.EstimatedTotalCost:F2}");
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

    private class DietKnowledgeSearchTool(VectorStoreCollection<Guid, DietKnowledgeRecord> collection)
    {
        public async Task<string> Search(string query)
        {
            StringBuilder result = new StringBuilder();
            Output.Gray($"[RAG] Searching diet knowledge for: \"{query}\"");

            await foreach (VectorSearchResult<DietKnowledgeRecord> hit in collection.SearchAsync(query, 2))
            {
                Output.Gray($"  → Retrieved: {hit.Record.DietName} (score: {hit.Score:F3})");
                result.AppendLine($"Diet: {hit.Record.DietName}");
                result.AppendLine($"Rules: {hit.Record.Rules}");
                result.AppendLine($"Macro guidelines: {hit.Record.MacroGuidelines}");
                result.AppendLine($"Typical ingredients: {hit.Record.TypicalIngredients}");
                result.AppendLine($"Foods to avoid: {hit.Record.FoodsToAvoid}");
                result.AppendLine($"Budget tips: {hit.Record.BudgetTips}");
                result.AppendLine();
            }

            return result.ToString();
        }
    }

    private class MealPlanSearchTool(VectorStoreCollection<Guid, MealRecord> collection)
    {
        public async Task<string> Search(string query)
        {
            StringBuilder result = new StringBuilder();
            Output.Gray($"[RAG] Searching meal plan for: \"{query}\"");

            await foreach (VectorSearchResult<MealRecord> hit in collection.SearchAsync(query, 3))
            {
                Output.Gray($"  → Retrieved: Day {hit.Record.DayNumber} {hit.Record.MealType} — {hit.Record.Name} (score: {hit.Score:F3})");
                result.AppendLine($"Day {hit.Record.DayNumber} {hit.Record.MealType}: {hit.Record.Name}");
                result.AppendLine($"  Calories: {hit.Record.Calories} kcal | {hit.Record.Macros} | Cost: {hit.Record.EstimatedCost}");
                result.AppendLine($"  Ingredients: {hit.Record.Ingredients}");
                result.AppendLine();
            }

            return result.ToString();
        }
    }

    // --- Structured output types ---

    private class MealPlan
    {
        public required string DietType { get; set; }
        public required int NumberOfDays { get; set; }
        public required decimal EstimatedTotalCost { get; set; }
        public required List<DayPlan> Days { get; set; }
    }

    private class DayPlan
    {
        public required int DayNumber { get; set; }
        public required List<Meal> Meals { get; set; }
    }

    private class Meal
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

    private class NutritionCritique
    {
        public required bool Approved { get; set; }
        public required List<string> DietViolations { get; set; }
        public required List<string> MacroIssues { get; set; }
        public required List<string> Suggestions { get; set; }
    }

    private class DietKnowledgeRecord
    {
        [VectorStoreKey]
        public required Guid Id { get; set; }

        [VectorStoreData]
        public required string DietName { get; set; }

        [VectorStoreData]
        public required string Rules { get; set; }

        [VectorStoreData]
        public required string MacroGuidelines { get; set; }

        [VectorStoreData]
        public required string TypicalIngredients { get; set; }

        [VectorStoreData]
        public required string FoodsToAvoid { get; set; }

        [VectorStoreData]
        public required string BudgetTips { get; set; }

        [VectorStoreVector(1536)]
        public string Vector => $"{DietName}: {Rules}. Macros: {MacroGuidelines}. Ingredients: {TypicalIngredients}";
    }

    private class MealRecord
    {
        [VectorStoreKey]
        public required Guid Id { get; set; }

        [VectorStoreData]
        public required int DayNumber { get; set; }

        [VectorStoreData]
        public required string MealType { get; set; }

        [VectorStoreData]
        public required string Name { get; set; }

        [VectorStoreData]
        public required int Calories { get; set; }

        [VectorStoreData]
        public required string Macros { get; set; }

        [VectorStoreData]
        public required string EstimatedCost { get; set; }

        [VectorStoreData]
        public required string Ingredients { get; set; }

        [VectorStoreVector(1536)]
        public string Vector =>
            $"Day {DayNumber} {MealType}: {Name}. Ingredients: {Ingredients}. " +
            $"Calories: {Calories} kcal. {Macros}. Cost: {EstimatedCost}.";
    }

    // KEY CONCEPT: UserMemoryRecord — same vector store pattern, new purpose: user-owned persistent state
    // Unlike DietKnowledgeRecord and MealRecord, this collection is never cleared between sessions.
    private class UserMemoryRecord
    {
        [VectorStoreKey]
        public required Guid Id { get; set; }

        [VectorStoreData]
        public required string Username { get; set; }

        [VectorStoreData]
        public required string PreferredDietType { get; set; }

        [VectorStoreData]
        public required int DefaultCalories { get; set; }

        [VectorStoreData]
        public required double DefaultBudget { get; set; }

        [VectorStoreData]
        public required int DefaultPlanDays { get; set; }

        // Append-only — allergies/intolerances are never auto-removed
        [VectorStoreData]
        public required string FoodRestrictions { get; set; }

        // Comma-separated, deduplicated — extracted from advisor conversations
        [VectorStoreData]
        public required string DislikedIngredients { get; set; }

        // Pipe-separated, newest first, capped at MaxAdvisorNotes
        [VectorStoreData]
        public required string AdvisorNotes { get; set; }

        // JSON-encoded list of PlanHistoryEntry, capped at MaxPlanHistory
        [VectorStoreData]
        public required string PlanHistorySummary { get; set; }

        [VectorStoreData]
        public required string WeightGoal { get; set; }

        [VectorStoreData]
        public required string LastSessionDate { get; set; }

        [VectorStoreData]
        public required int TotalSessionsCount { get; set; }

        // Vector encodes dietary identity for fuzzy username lookup via SearchAsync
        [VectorStoreVector(1536)]
        public string Vector =>
            $"User {Username}: {PreferredDietType} diet, {DefaultCalories} kcal, " +
            $"${DefaultBudget} budget. Restrictions: {FoodRestrictions}. " +
            $"Dislikes: {DislikedIngredients}. Goal: {WeightGoal}.";
    }

    private class PlanHistoryEntry
    {
        public string Date { get; set; } = string.Empty;
        public string Diet { get; set; } = string.Empty;
        public int Days { get; set; }
        public decimal Cost { get; set; }
    }
}
