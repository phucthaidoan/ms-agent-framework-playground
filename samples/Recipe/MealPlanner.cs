using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;

namespace Samples.Recipe;

// Base sample: AI-Powered Meal Plan Generator
//
// Concepts demonstrated:
// - Structured Output       — MealPlannerAgent returns a typed MealPlan object
// - Agent-as-Tool           — NutritionCriticAgent wrapped as a tool for the planner
// - Iterative Refinement    — planner loops up to MaxIterations until critic approves
// - Tool Calling            — CheckBudget and PrintMealPlan are simulated tools
// - Instructions            — each agent has domain-specific, constraint-driven prompts

public static class MealPlanner
{
    private const int MaxIterations = 3;
    private const int TargetCaloriesPerDay = 2000;
    private const decimal BudgetLimit = 50m;

    public static async Task RunSample()
    {
        Output.Title("AI-Powered Meal Plan Generator");
        Output.Separator();

        // --- Step 1: Let user pick a diet type ---
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

        // --- Step 2: Build agents ---
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        // NutritionCriticAgent — validates diet compliance and macro balance.
        // Wrapped as a tool so the planner can call it directly.
        ChatClientAgent criticAgent = client
            .GetChatClient("gpt-4.1-mini")
            .AsAIAgent(
                name: "validate_nutrition",
                instructions:
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

        AIFunction criticTool = criticAgent.AsAIFunction();

        // MealPlannerAgent — generates and refines meal plans. Critic is a tool it can call.
        ChatClientAgent plannerAgent = client
            .GetChatClient("gpt-4.1-mini")
            .AsAIAgent(
                name: "meal_planner",
                instructions:
                    $"You are a professional meal planner specialising in the '{dietType}' diet. " +
                    $"Generate a {numberOfDays}-day meal plan. Each day MUST contain exactly 3 meals: Breakfast, Lunch, and Dinner. " +
                    $"Calorie target: {TargetCaloriesPerDay} kcal per day, distributed as: Breakfast ~25%, Lunch ~35%, Dinner ~40%. " +
                    $"Total budget: ${BudgetLimit} USD across all {numberOfDays} days (approx ${BudgetLimit / numberOfDays:F2}/day). " +
                    $"Each meal must include: a descriptive name, realistic calorie count, protein/carbs/fat in grams, estimated cost in USD, and 4–6 specific ingredients. " +
                    $"Diet rules to follow strictly: " +
                    $"  Vegan         — NO meat, fish, dairy, eggs, or honey. Use tofu, legumes, whole grains, nuts, seeds. " +
                    $"  Keto          — NO grains, sugar, starchy vegetables, or fruit (except berries). Fat ≥65% of calories, carbs <10% (<50g/day). " +
                    $"  Mediterranean — Emphasize fish, olive oil, vegetables, legumes, whole grains. Limit red meat to once per plan. " +
                    $"After generating the plan, call the validate_nutrition tool with the plan JSON to get a critique. " +
                    $"If the critique says approved=false, fix all violations and issues listed in the critique, then call validate_nutrition again. " +
                    $"Repeat until approved=true or you have refined {MaxIterations} times. " +
                    $"Once approved, respond with the final approved meal plan.",
                tools: [criticTool]);

        // --- Step 3: Generate plan (planner calls critic internally) ---
        Output.Title("Generating meal plan...");
        Output.Yellow("(The planner will call the nutrition critic automatically and refine if needed)");
        Output.Separator();

        string plannerPrompt =
            $"Generate a {numberOfDays}-day {dietType} meal plan targeting {TargetCaloriesPerDay} kcal/day " +
            $"with a total budget of ${BudgetLimit} USD. " +
            $"Validate it using the validate_nutrition tool and refine until it is approved. " +
            $"Then return the final approved plan as JSON.";

        AgentSession session = await plannerAgent.CreateSessionAsync();
        AgentResponse<MealPlan> planResponse = await plannerAgent.RunAsync<MealPlan>(plannerPrompt, session);
        MealPlan plan = planResponse.Result;

        // --- Step 4: Budget check tool ---
        Output.Separator();
        Output.Title("Step 2: Checking budget...");
        CheckBudget(plan.EstimatedTotalCost);

        // --- Step 5: Print the final plan ---
        Output.Separator();
        Output.Title("Step 3: Final Meal Plan");
        PrintMealPlan(plan);

        Output.Separator();
        Output.Green("Meal planning complete.");
    }

    // Tool: simulated budget check
    private static void CheckBudget(decimal totalCost)
    {
        if (totalCost <= BudgetLimit)
            Output.Green($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Within limit of ${BudgetLimit:F2} ✓");
        else
            Output.Yellow($"[BUDGET CHECK] Estimated total: ${totalCost:F2} — Exceeds limit of ${BudgetLimit:F2} ⚠ (plan kept)");
    }

    // Tool: simulated plan printer
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
}
