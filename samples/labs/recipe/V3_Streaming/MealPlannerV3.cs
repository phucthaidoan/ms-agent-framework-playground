using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Labs.Recipe.V3_Streaming;

// V3: Streaming Output
// New concept: RunStreamingAsync + AgentResponseUpdate — instead of waiting for the full
// response, a narrative preview streams token-by-token to the console as it is generated.
// Compare with V2 where every agent call blocks until the full response arrives.
// After the stream completes, RunAsync<MealPlan> extracts structured data as normal.
// Note: there is no RunStreamingAsync<T> — streaming and structured extraction are always
// two separate calls (see Step 1 vs Step 2 below).

public static class MealPlannerV3
{
    private const int MaxIterations = 3;
    private const int TargetCaloriesPerDay = 2000;
    private const decimal BudgetLimit = 50m;

    public static async Task RunSample()
    {
        Output.Title("Meal Planner V3 — Streaming Output");
        Output.Separator();

        // --- Step 1: User input ---
        string[] dietTypes = ["Vegan", "Keto", "Mediterranean"];

        Console.WriteLine("Available diet types:");
        for (int i = 0; i < dietTypes.Length; i++)
            Console.WriteLine($"  {i + 1}. {dietTypes[i]}");

        Console.Write("\nEnter the number of your preferred diet: ");
        string? dietInput = Console.ReadLine();

        if (!int.TryParse(dietInput, out int dietChoice) || dietChoice < 1 || dietChoice > dietTypes.Length)
        {
            Output.Red("Invalid selection. Exiting.");
            return;
        }

        string dietType = dietTypes[dietChoice - 1];

        Console.Write("How many days to plan? (1–7): ");
        string? daysInput = Console.ReadLine();

        if (!int.TryParse(daysInput, out int numberOfDays) || numberOfDays < 1 || numberOfDays > 7)
        {
            Output.Red("Invalid number of days. Exiting.");
            return;
        }

        Output.Separator();
        Console.WriteLine($"Diet     : {dietType}");
        Console.WriteLine($"Days     : {numberOfDays}");
        Console.WriteLine($"Calories : {TargetCaloriesPerDay} kcal/day");
        Console.WriteLine($"Budget   : ${BudgetLimit} USD total");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // --- Step 2: Streaming narrative preview ---
        // KEY CONCEPT: narrativeAgent uses RunStreamingAsync — tokens arrive and are printed
        // immediately, giving the user instant feedback (typing effect).
        Output.Title("Step 1: Streaming plan preview...");
        Console.WriteLine();

        ChatClientAgent narrativeAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a professional meal planner specialising in the '{dietType}' diet. " +
                $"Describe the {numberOfDays}-day meal plan you are about to create. " +
                $"Explain the overall theme, key ingredients you will use, how you will distribute " +
                $"the {TargetCaloriesPerDay} kcal daily target across meals, and how you will stay " +
                $"within the ${BudgetLimit} total budget. Write in flowing prose, 2-3 paragraphs. " +
                $"This is a planning preview — the structured plan will be generated separately.");

        // KEY CONCEPT: await foreach over RunStreamingAsync — each update is a token chunk
        await foreach (AgentResponseUpdate update in narrativeAgent.RunStreamingAsync(
            $"Describe your approach for a {numberOfDays}-day {dietType} meal plan " +
            $"targeting {TargetCaloriesPerDay} kcal/day within a ${BudgetLimit} budget."))
        {
            Console.Write(update);  // tokens printed as they arrive
        }
        Console.WriteLine();

        // --- Step 3: Build planner + critic agents (same as V2) ---
        ChatClientAgent plannerAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a professional meal planner specialising in the '{dietType}' diet. " +
                $"Generate a {numberOfDays}-day meal plan. Each day MUST contain exactly 3 meals: Breakfast, Lunch, and Dinner. " +
                $"Calorie target: {TargetCaloriesPerDay} kcal per day, distributed as: Breakfast ~25%, Lunch ~35%, Dinner ~40%. " +
                $"Total budget: ${BudgetLimit} USD across all {numberOfDays} days (approx ${BudgetLimit / numberOfDays:F2}/day). " +
                $"Each meal must include: a descriptive name, realistic calorie count, protein/carbs/fat in grams, estimated cost in USD, and 4–6 specific ingredients. " +
                $"Diet rules to follow strictly: " +
                $"  Vegan         — NO meat, fish, dairy, eggs, or honey. Use tofu, legumes, whole grains, nuts, seeds. " +
                $"  Keto          — NO grains, sugar, starchy vegetables, or fruit (except berries). Fat ≥65% of calories, carbs <10% (<50g/day). " +
                $"  Mediterranean — Emphasize fish, olive oil, vegetables, legumes, whole grains. Limit red meat to once per plan.");

        ChatClientAgent criticAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a strict nutrition and diet compliance reviewer. " +
                $"You receive a meal plan and must verify ALL of the following: " +
                $"1. Each day has exactly 3 meals (Breakfast, Lunch, Dinner). " +
                $"2. Each day totals approximately {TargetCaloriesPerDay} kcal (±200 kcal tolerance). " +
                $"3. No meal exceeds 50% of the daily calorie target. " +
                $"4. Total estimated cost does not exceed ${BudgetLimit} USD. " +
                $"5. Diet compliance for '{dietType}': " +
                $"     Vegan         — zero animal products in any meal. " +
                $"     Keto          — carbs <50g/day total; fat calories ≥65% of daily total. " +
                $"     Mediterranean — no processed food; red meat at most once in the entire plan. " +
                $"6. Every meal has a name, calories, protein, carbs, fat, cost, and at least one ingredient. " +
                $"Approve only if ALL checks pass. List every violation found, even minor ones.");

        // --- Step 4: Host-orchestrated refinement loop (same as V2) ---
        AgentSession plannerSession = await plannerAgent.CreateSessionAsync();

        Output.Separator();
        Output.Title("Step 2: Generating structured plan with refinement loop...");
        Output.Yellow("[PLANNER] Calling MealPlannerAgent...");

        AgentResponse<MealPlan> initialResponse = await plannerAgent.RunAsync<MealPlan>(
            $"Generate a {numberOfDays}-day {dietType} meal plan targeting {TargetCaloriesPerDay} kcal/day " +
            $"with a total budget of ${BudgetLimit} USD.",
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

        // --- Step 5: Budget check ---
        Output.Separator();
        Output.Title("Step 3: Checking budget...");
        CheckBudget(plan.EstimatedTotalCost);

        // --- Step 6: Print final plan ---
        Output.Separator();
        Output.Title("Step 4: Final Meal Plan");
        PrintMealPlan(plan);

        // --- Step 7: Multi-turn chat with meal advisor (same as V2) ---
        Output.Separator();
        Output.Title("Step 5: Meal Plan Advisor (type 'exit' to quit)");

        ChatClientAgent mealAdvisorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a friendly and knowledgeable meal plan advisor specialising in the '{dietType}' diet. " +
                $"You have been given an approved {numberOfDays}-day meal plan to discuss with the user. " +
                $"Help them understand meals, suggest swaps, explain nutritional choices, or answer any questions. " +
                $"Respect the original calorie target of {TargetCaloriesPerDay} kcal/day and budget of ${BudgetLimit} USD total. " +
                $"Be practical and concise.");

        AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();

        string planContext =
            $"Here is the approved {dietType} meal plan for {numberOfDays} days " +
            $"(target: {TargetCaloriesPerDay} kcal/day, budget: ${BudgetLimit} USD): " +
            $"{planJson}. Use this as the basis for answering follow-up questions.";

        await mealAdvisorAgent.RunAsync(planContext, advisorSession);

        Output.Gray("Plan loaded. Ask your questions (type 'exit' to finish):");
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

    private static void CheckBudget(decimal totalCost)
    {
        if (totalCost <= BudgetLimit)
            Output.Green($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Within limit of ${BudgetLimit:F2} ✓");
        else
            Output.Yellow($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Exceeds limit of ${BudgetLimit:F2} ⚠ (plan kept)");
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
}
