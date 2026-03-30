using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Recipe.V4_DynamicInstructions;

// V4: Dynamic Instructions
// New concept: agent instructions composed at runtime from a DietProfile data record.
// The same AsAIAgent(instructions: ...) API is used in all variants, but here the
// instructions string is assembled from a user-selected profile object — demonstrating
// that agent behaviour is data-driven, not code-driven.
// Compare with V3 where instructions used hardcoded constants. Here, selecting a
// different profile produces entirely different planner and critic behaviour without
// touching agent code.

public static class MealPlannerV4
{
    private const int MaxIterations = 3;

    // KEY CONCEPT: behaviour is driven by this record — not by constants.
    // Changing the selected profile changes ALL agent instructions automatically.
    private record DietProfile(
        string Name,
        string DietType,
        int DaysToplan,
        int TargetCaloriesPerDay,
        decimal BudgetLimit,
        string PersonaNote
    );

    public static async Task RunSample()
    {
        Output.Title("Meal Planner V4 — Dynamic Instructions");
        Output.Separator();

        // --- Step 1: User selects a profile (replaces manual diet + days input) ---
        // KEY CONCEPT: profiles are data — agent instructions are derived from the selection
        DietProfile[] profiles =
        [
            new("Office Worker",        "Mediterranean", 5, 1800, 60m,  "Sedentary adult with a mild weight-loss goal"),
            new("Athlete",              "Keto",          7, 2800, 80m,  "Endurance athlete with high protein priority"),
            new("Plant-Based Beginner", "Vegan",         3, 2000, 40m,  "Transitioning from omnivore, prefers familiar ingredients")
        ];

        Console.WriteLine("Select a dietary profile:");
        for (int i = 0; i < profiles.Length; i++)
        {
            Console.WriteLine(
                $"  {i + 1}. {profiles[i].Name} — {profiles[i].DietType}, " +
                $"{profiles[i].DaysToplan} days, {profiles[i].TargetCaloriesPerDay} kcal/day, " +
                $"${profiles[i].BudgetLimit} budget");
            Output.Gray($"       {profiles[i].PersonaNote}");
        }

        Console.Write("\nEnter profile number: ");
        string? profileInput = Console.ReadLine();

        if (!int.TryParse(profileInput, out int profileChoice) || profileChoice < 1 || profileChoice > profiles.Length)
        {
            Output.Red("Invalid selection. Exiting.");
            return;
        }

        DietProfile selectedProfile = profiles[profileChoice - 1];

        Output.Separator();
        Console.WriteLine($"Profile  : {selectedProfile.Name}");
        Console.WriteLine($"Diet     : {selectedProfile.DietType}");
        Console.WriteLine($"Days     : {selectedProfile.DaysToplan}");
        Console.WriteLine($"Calories : {selectedProfile.TargetCaloriesPerDay} kcal/day");
        Console.WriteLine($"Budget   : ${selectedProfile.BudgetLimit} USD total");
        Console.WriteLine($"Persona  : {selectedProfile.PersonaNote}");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // --- Step 2: Streaming narrative preview (V3 concept) ---
        // Note: narrativeAgent instructions reference selectedProfile fields
        Output.Title("Step 1: Streaming plan preview...");
        Console.WriteLine();

        // KEY CONCEPT: instructions built from selectedProfile at runtime
        ChatClientAgent narrativeAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a professional meal planner specialising in the '{selectedProfile.DietType}' diet. " +
                $"This plan is for: {selectedProfile.PersonaNote}. " +
                $"Describe the {selectedProfile.DaysToplan}-day meal plan you are about to create. " +
                $"Explain the overall theme, key ingredients, how you will distribute the " +
                $"{selectedProfile.TargetCaloriesPerDay} kcal daily target, and how you will stay " +
                $"within the ${selectedProfile.BudgetLimit} total budget. " +
                $"Write in flowing prose, 2-3 paragraphs. This is a planning preview.");

        await foreach (AgentResponseUpdate update in narrativeAgent.RunStreamingAsync(
            $"Describe your approach for a {selectedProfile.DaysToplan}-day {selectedProfile.DietType} " +
            $"meal plan for: {selectedProfile.PersonaNote}. " +
            $"Target {selectedProfile.TargetCaloriesPerDay} kcal/day within a ${selectedProfile.BudgetLimit} budget."))
        {
            Console.Write(update);
        }
        Console.WriteLine();

        // --- Step 3: Build planner + critic with profile-driven instructions ---
        // KEY CONCEPT: all instruction strings interpolate selectedProfile.* fields
        string plannerInstructions =
            $"You are a professional meal planner specialising in the '{selectedProfile.DietType}' diet. " +
            $"This plan is for: {selectedProfile.PersonaNote}. " +
            $"Generate a {selectedProfile.DaysToplan}-day meal plan. Each day MUST contain exactly 3 meals: Breakfast, Lunch, and Dinner. " +
            $"Calorie target: {selectedProfile.TargetCaloriesPerDay} kcal per day, distributed as: Breakfast ~25%, Lunch ~35%, Dinner ~40%. " +
            $"Total budget: ${selectedProfile.BudgetLimit} USD across all {selectedProfile.DaysToplan} days " +
            $"(approx ${selectedProfile.BudgetLimit / selectedProfile.DaysToplan:F2}/day). " +
            $"Each meal must include: a descriptive name, realistic calorie count, protein/carbs/fat in grams, estimated cost in USD, and 4–6 specific ingredients. " +
            $"Diet rules to follow strictly: " +
            $"  Vegan         — NO meat, fish, dairy, eggs, or honey. Use tofu, legumes, whole grains, nuts, seeds. " +
            $"  Keto          — NO grains, sugar, starchy vegetables, or fruit (except berries). Fat ≥65% of calories, carbs <10% (<50g/day). " +
            $"  Mediterranean — Emphasize fish, olive oil, vegetables, legumes, whole grains. Limit red meat to once per plan.";

        string criticInstructions =
            $"You are a strict nutrition and diet compliance reviewer. " +
            $"You receive a meal plan for: {selectedProfile.PersonaNote}. " +
            $"Verify ALL of the following: " +
            $"1. Each day has exactly 3 meals (Breakfast, Lunch, Dinner). " +
            $"2. Each day totals approximately {selectedProfile.TargetCaloriesPerDay} kcal (±200 kcal tolerance). " +
            $"3. No meal exceeds 50% of the daily calorie target. " +
            $"4. Total estimated cost does not exceed ${selectedProfile.BudgetLimit} USD. " +
            $"5. Diet compliance for '{selectedProfile.DietType}': " +
            $"     Vegan         — zero animal products in any meal. " +
            $"     Keto          — carbs <50g/day total; fat calories ≥65% of daily total. " +
            $"     Mediterranean — no processed food; red meat at most once in the entire plan. " +
            $"6. Every meal has a name, calories, protein, carbs, fat, cost, and at least one ingredient. " +
            $"Approve only if ALL checks pass. List every violation found, even minor ones.";

        ChatClientAgent plannerAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions: plannerInstructions);

        ChatClientAgent criticAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions: criticInstructions);

        // --- Step 4: Host-orchestrated refinement loop (same as V3) ---
        AgentSession plannerSession = await plannerAgent.CreateSessionAsync();

        Output.Separator();
        Output.Title("Step 2: Generating structured plan with refinement loop...");
        Output.Yellow("[PLANNER] Calling MealPlannerAgent...");

        AgentResponse<MealPlan> initialResponse = await plannerAgent.RunAsync<MealPlan>(
            $"Generate a {selectedProfile.DaysToplan}-day {selectedProfile.DietType} meal plan " +
            $"for {selectedProfile.PersonaNote}. " +
            $"Target {selectedProfile.TargetCaloriesPerDay} kcal/day with a total budget of ${selectedProfile.BudgetLimit} USD.",
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

        // --- Step 5: Budget check (uses profile budget, not a constant) ---
        Output.Separator();
        Output.Title("Step 3: Checking budget...");
        CheckBudget(plan.EstimatedTotalCost, selectedProfile.BudgetLimit);

        // --- Step 6: Print final plan ---
        Output.Separator();
        Output.Title("Step 4: Final Meal Plan");
        PrintMealPlan(plan);

        // --- Step 7: Multi-turn advisor session (V2 concept) ---
        // KEY CONCEPT: advisor instructions also reference selectedProfile fields
        Output.Separator();
        Output.Title("Step 5: Meal Plan Advisor (type 'exit' to quit)");

        ChatClientAgent mealAdvisorAgent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(instructions:
                $"You are a friendly and knowledgeable meal plan advisor specialising in the '{selectedProfile.DietType}' diet. " +
                $"You are advising: {selectedProfile.PersonaNote}. " +
                $"You have been given an approved {selectedProfile.DaysToplan}-day meal plan to discuss. " +
                $"Help the user understand meals, suggest swaps, explain nutritional choices, or answer questions. " +
                $"Respect the calorie target of {selectedProfile.TargetCaloriesPerDay} kcal/day and budget of ${selectedProfile.BudgetLimit} USD. " +
                $"Be practical and concise.");

        AgentSession advisorSession = await mealAdvisorAgent.CreateSessionAsync();

        string planContext =
            $"Here is the approved {selectedProfile.DietType} meal plan for {selectedProfile.DaysToplan} days " +
            $"(target: {selectedProfile.TargetCaloriesPerDay} kcal/day, budget: ${selectedProfile.BudgetLimit} USD, " +
            $"profile: {selectedProfile.PersonaNote}): {planJson}. " +
            $"Use this as the basis for answering follow-up questions.";

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
