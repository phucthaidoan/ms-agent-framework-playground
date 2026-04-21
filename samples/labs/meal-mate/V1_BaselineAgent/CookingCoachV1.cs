using Samples.SampleUtilities;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Samples.Labs.MealMate.V1_BaselineAgent;

// Domain entities — shared across all versions
record UserProfile(string UserId, string Name, string[] DietaryTags, string SkillLevel);
record PantryItem(string Name, string Quantity);

public static class CookingCoachV1
{
    // ──────────────────────────────────────────────────────────────────────────
    // Fake services — no cloud required
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, UserProfile> Profiles = new()
    {
        ["alice"] = new("alice", "Alice", ["vegetarian", "nut-allergy"], "beginner"),
        ["bob"]   = new("bob",   "Bob",   ["lactose-intolerant"],        "intermediate"),
    };

    private static readonly Dictionary<string, List<PantryItem>> Pantries = new()
    {
        ["alice"] = [
            new("pasta",      "500g"),
            new("canned tuna","2 cans"),
            new("olive oil",  "half a bottle"),
            new("garlic",     "1 bulb"),
            new("lemon",      "2"),
        ],
        ["bob"] = [
            new("pasta",   "500g"),
            new("milk",    "1L"),
            new("garlic",  "1 bulb"),
            new("butter",  "200g"),
        ],
    };

    // ──────────────────────────────────────────────────────────────────────────
    // Kịch bản
    // ──────────────────────────────────────────────────────────────────────────

    // Kịch bản A — Nguy cơ dị ứng: agent không biết user dị ứng đậu phộng
    private const string ScenarioA =
        "Gợi ý cho tôi một bữa tối nhanh với sốt chấm đậu phộng.";

    // Kịch bản B — Mù kho đồ: agent gợi ý nguyên liệu user không có
    private const string ScenarioB =
        "Tôi muốn làm cacio e pepe tối nay. Tôi cần những nguyên liệu gì?";

    // Kịch bản C — Mất trí nhớ phiên: agent quên lựa chọn ẩm thực sau lượt 1
    private const string ScenarioCTurn1 = "Tối nay mình ăn món Ý nhé.";
    private const string ScenarioCTurn2 = "Tôi nên bắt đầu với món gì?";

    // Kịch bản D — Không phù hợp kỹ năng: kỹ thuật chuyên nghiệp cho người mới
    private const string ScenarioD =
        "Làm thế nào để làm beurre blanc hoàn hảo từ đầu?";

    // Kịch bản E — Xuyên phiên bản: cùng 3 lượt chạy ở tất cả phiên bản
    private const string ScenarioETurn1 = "Tôi muốn ăn gì đó nhanh mà no cho bữa tối tối nay.";
    private const string ScenarioETurn2 = "Nghe hay đó — bạn có thể cho tôi công thức đầy đủ không?";
    private const string ScenarioETurn3 = "Nếu tôi không có phô mai parmesan thì sao?";

    // ──────────────────────────────────────────────────────────────────────────

    public static async Task RunSample()
    {
        Output.Title("MealMate — V1: Agent cơ bản (không có context providers)");
        Output.Gray("Agent chỉ có system prompt chung. Nó không biết gì về người dùng.");
        Output.Separator();

        string apiKey = SecretManager.GetOpenAIApiKey();
        IChatClient chatClient = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4.1-nano")
            .AsIChatClient();

        // Không có context providers — agent thô với một lệnh hệ thống duy nhất
        var agent = chatClient.AsAIAgent("Bạn là trợ lý nấu ăn hữu ích.");

        // ── Kịch bản A ────────────────────────────────────────────────────────
        Output.Yellow("KỊCH BẢN A — Nguy cơ dị ứng");
        Output.Gray($"Người dùng (alice, dị ứng đậu phộng): {ScenarioA}");
        Output.Red("Lỗi dự kiến: agent gợi ý đậu phộng mà không có cảnh báo dị ứng.");

        var respA = await agent.RunAsync(ScenarioA);
        Output.Green($"Agent: {respA.Text}");
        Output.Separator();

        // ── Kịch bản B ────────────────────────────────────────────────────────
        Output.Yellow("KỊCH BẢN B — Mù kho đồ");
        Output.Gray($"Người dùng (alice — không có parmesan/pecorino): {ScenarioB}");
        Output.Red("Lỗi dự kiến: agent liệt kê parmesan/pecorino; người dùng không có.");

        var respB = await agent.RunAsync(ScenarioB);
        Output.Green($"Agent: {respB.Text}");
        Output.Separator();

        // ── Kịch bản C ────────────────────────────────────────────────────────
        Output.Yellow("KỊCH BẢN C — Mất trí nhớ phiên");
        Output.Red("Lỗi dự kiến: agent hỏi lại ẩm thực ở lượt 2.");

        var sessionC = await agent.CreateSessionAsync();
        Output.Gray($"Lượt 1: {ScenarioCTurn1}");
        var respC1 = await agent.RunAsync(ScenarioCTurn1, sessionC);
        Output.Green($"Agent: {respC1.Text}");

        Output.Gray($"Lượt 2: {ScenarioCTurn2}");
        var respC2 = await agent.RunAsync(ScenarioCTurn2, sessionC);
        Output.Green($"Agent: {respC2.Text}");
        Output.Separator();

        // ── Kịch bản D ────────────────────────────────────────────────────────
        Output.Yellow("KỊCH BẢN D — Không phù hợp kỹ năng");
        Output.Gray($"Người dùng (alice, người mới): {ScenarioD}");
        Output.Red("Lỗi dự kiến: giải thích cấp độ chuyên nghiệp, không điều chỉnh cho người mới.");

        var respD = await agent.RunAsync(ScenarioD);
        Output.Green($"Agent: {respD.Text}");
        Output.Separator();

        // ── Kịch bản E — Xuyên phiên bản ────────────────────────────────────
        Output.Yellow("KỊCH BẢN E — Xuyên phiên bản [V1 — không có providers]");
        Output.Gray("Cùng 3 lượt sẽ chạy ở mọi phiên bản. Quan sát kết quả cải thiện dần.");

        var sessionE = await agent.CreateSessionAsync();

        Output.Gray($"Lượt 1: {ScenarioETurn1}");
        var respE1 = await agent.RunAsync(ScenarioETurn1, sessionE);
        Output.Green($"Agent: {respE1.Text}");

        Output.Gray($"Lượt 2: {ScenarioETurn2}");
        var respE2 = await agent.RunAsync(ScenarioETurn2, sessionE);
        Output.Green($"Agent: {respE2.Text}");

        Output.Gray($"Lượt 3: {ScenarioETurn3}");
        var respE3 = await agent.RunAsync(ScenarioETurn3, sessionE);
        Output.Green($"Agent: {respE3.Text}");

        Output.Separator();
        Output.Yellow("V1 hoàn tất. Chạy V2 để xem ProfileProvider sửa kịch bản A và D.");
    }
}
