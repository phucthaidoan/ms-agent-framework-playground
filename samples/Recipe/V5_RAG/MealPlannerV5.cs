// ReSharper disable ClassNeverInstantiated.Local

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.Text;

namespace Samples.Recipe.V5_RAG;

// V5: RAG-Powered Meal Planning
// New concept: Retrieval-Augmented Generation (RAG) — used in TWO places:
//   1. MealPlannerAgent retrieves dietary rules + ingredients from a vector store
//      instead of reading them from hardcoded instructions.
//   2. MealAdvisorAgent retrieves relevant meals from the approved plan instead of
//      receiving the full plan JSON as context (expensive on long plans).
// Compare with V4 where a DietProfile array was hardcoded and the full planJson was
// injected into the advisor session on every run.
// Here, knowledge lives in data — adding a new diet or extending a plan requires no
// code change, just a new record in the vector store.

public static class MealPlannerV5
{
    private const int MaxIterations = 3;
    private const int DefaultCaloriesPerDay = 2000;
    private const decimal DefaultBudget = 50m;

    public static async Task RunSample()
    {
        Output.Title("Meal Planner V5 — RAG-Powered Planning");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // KEY CONCEPT: embedding generator converts text into vectors for semantic search
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = client
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator();

        // Same DB as Section08 and CV_Screening V5 — separate collections keep data isolated
        string connectionString = $"Data Source={Path.GetTempPath()}\\af-course-vector-store.db";
        VectorStore vectorStore = new SqliteVectorStore(connectionString, new SqliteVectorStoreOptions
        {
            EmbeddingGenerator = embeddingGenerator
        });

        VectorStoreCollection<Guid, DietKnowledgeRecord> dietCollection =
            vectorStore.GetCollection<Guid, DietKnowledgeRecord>("meal_diet_profiles");

        VectorStoreCollection<Guid, MealRecord> mealCollection =
            vectorStore.GetCollection<Guid, MealRecord>("meal_plan_meals");

        await dietCollection.EnsureCollectionExistsAsync();
        await mealCollection.EnsureCollectionExistsAsync();

        // --- Phase 1: Ingest diet knowledge ---
        // KEY CONCEPT: embed domain knowledge into vector store at startup.
        // In production this runs once (or when the knowledge base changes); here we offer a choice.
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

        // --- Phase 2: User input ---
        Console.Write("Describe your diet preference (e.g. 'Keto', 'plant-based vegan', 'Mediterranean'): ");
        string dietDescription = Console.ReadLine() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(dietDescription))
        {
            Output.Red("No diet preference provided. Exiting.");
            return;
        }

        Console.Write("How many days to plan? (1–7): ");
        string? daysInput = Console.ReadLine();
        if (!int.TryParse(daysInput, out int numberOfDays) || numberOfDays < 1 || numberOfDays > 7)
        {
            Output.Red("Invalid number of days. Exiting.");
            return;
        }

        Console.Write($"Daily calorie target? (press Enter for {DefaultCaloriesPerDay}): ");
        string? caloriesInput = Console.ReadLine();
        int targetCalories = string.IsNullOrWhiteSpace(caloriesInput)
            ? DefaultCaloriesPerDay
            : int.TryParse(caloriesInput, out int c) ? c : DefaultCaloriesPerDay;

        Console.Write($"Total budget in USD? (press Enter for ${DefaultBudget}): ");
        string? budgetInput = Console.ReadLine();
        decimal budget = string.IsNullOrWhiteSpace(budgetInput)
            ? DefaultBudget
            : decimal.TryParse(budgetInput, out decimal b) ? b : DefaultBudget;

        Output.Separator();
        Console.WriteLine($"Diet      : {dietDescription}");
        Console.WriteLine($"Days      : {numberOfDays}");
        Console.WriteLine($"Calories  : {targetCalories} kcal/day");
        Console.WriteLine($"Budget    : ${budget} USD total");
        Output.Separator();

        // --- Phase 3: Streaming narrative preview (V3 concept) ---
        // The narrative agent also benefits from RAG but keeps it simple:
        // the planner agent below will do the deep retrieval for the structured plan.
        Output.Title("Step 1: Streaming plan preview...");
        Console.WriteLine();

        ChatClientAgent narrativeAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a professional meal planner. " +
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

        // --- Phase 4: Build RAG-powered planner + critic ---
        // KEY CONCEPT: RAG = search tool wrapping vector store.
        // The planner agent retrieves diet rules autonomously — no hardcoded knowledge in instructions.
        DietKnowledgeSearchTool dietSearchTool = new DietKnowledgeSearchTool(dietCollection);

        ChatClientAgent plannerAgent = client
            .GetChatClient("gpt-4.1-nano")
            // KEY CONCEPT: agent grounded by retrieval, not by hardcoded instructions.
            // Notice: no diet rules are hardcoded here — the agent retrieves them via the tool.
            .AsAIAgent(
                instructions:
                    $"You are a professional meal planner. " +
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
                $"Approve only if ALL checks pass. List every violation found, even minor ones.");

        // --- Phase 5: Host-orchestrated refinement loop (V1 concept) ---
        AgentSession plannerSession = await plannerAgent.CreateSessionAsync();

        Output.Separator();
        Output.Title("Step 2: Generating structured plan with refinement loop...");
        Output.Yellow("[PLANNER] Calling MealPlannerAgent...");

        // KEY CONCEPT: LLM calls search_diet_knowledge before generating — grounds the plan
        // in retrieved facts from the vector store, not in hardcoded text or training data.
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

        // --- Phase 6: Budget check ---
        Output.Separator();
        Output.Title("Step 3: Checking budget...");
        CheckBudget(plan.EstimatedTotalCost, budget);

        // --- Phase 7: Print final plan ---
        Output.Separator();
        Output.Title("Step 4: Final Meal Plan");
        PrintMealPlan(plan);

        // --- Phase 8: Index approved plan meals into vector store ---
        // KEY CONCEPT: index the approved plan so the advisor can retrieve by similarity.
        // This avoids injecting the full plan JSON (expensive) as session context.
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

        // --- Phase 9: Multi-turn meal advisor with RAG retrieval (V2 concept — adapted) ---
        // KEY CONCEPT: advisor retrieves only relevant meals per question — no full plan JSON in context.
        // Compare with V2 where the full planJson was injected as context before every session.
        Output.Separator();
        Output.Title("Step 5: Meal Plan Advisor (type 'exit' to quit)");

        MealPlanSearchTool mealSearchTool = new MealPlanSearchTool(mealCollection);

        ChatClientAgent mealAdvisorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions:
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

        // KEY CONCEPT: NO planJson pre-seeding — advisor fetches what it needs via search_meal_plan tool
        AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();

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

            AgentResponse response = await mealAdvisorAgent.RunAsync(input, advisorSession);
            Console.WriteLine(response.Text);
            Output.Separator(false);
        }

        Output.Separator();
        Output.Green("Session ended. Meal planning complete.");
    }

    private static void CheckBudget(decimal totalCost, decimal budgetLimit)
    {
        if (totalCost <= budgetLimit)
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

    // KEY CONCEPT: search tool wraps VectorStoreCollection.SearchAsync — used by planner agent
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

    // KEY CONCEPT: second search tool wraps the meal plan collection — used by advisor agent
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

    // KEY CONCEPT: vector record schema — [VectorStoreKey], [VectorStoreData], [VectorStoreVector]
    // The Vector property is a computed string that represents the document in embedding space.
    // Richer, semantically meaningful text in the vector = better semantic search results.
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
}
