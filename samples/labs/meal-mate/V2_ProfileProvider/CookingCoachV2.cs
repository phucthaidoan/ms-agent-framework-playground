using System.Text.Json.Serialization;
using Samples.SampleUtilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Samples.Labs.MealMate.V2_ProfileProvider;

record UserProfile(string UserId, string Name, string[] DietaryTags, string SkillLevel);
record PantryItem(string Name, string Quantity);

// ──────────────────────────────────────────────────────────────────────────────
// Fake profile service — hardcoded, no cloud required
// ──────────────────────────────────────────────────────────────────────────────

file static class FakeProfileService
{
    private static readonly Dictionary<string, UserProfile> Store = new()
    {
        ["alice"] = new("alice", "Alice", ["vegetarian", "nut-allergy"], "beginner"),
        ["bob"]   = new("bob",   "Bob",   ["lactose-intolerant"],        "intermediate"),
    };

    public static UserProfile? GetProfile(string userId) =>
        Store.GetValueOrDefault(userId);
}

// ──────────────────────────────────────────────────────────────────────────────
// ProfileContextProvider — injects user profile before every LLM call
// ──────────────────────────────────────────────────────────────────────────────

file sealed class ProfileContextProvider : AIContextProvider
{
    private readonly string _userId;

    private sealed class State
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    }

    private readonly ProviderSessionState<State> _sessionState = new(
        stateInitializer: _ => new State(),
        stateKey: nameof(ProfileContextProvider));

    public ProfileContextProvider(string userId) { _userId = userId; }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (string.IsNullOrEmpty(state.UserId)) state.UserId = _userId;
        var profile = FakeProfileService.GetProfile(state.UserId);

        if (profile is null)
        {
            Output.Yellow($"  [ProfileContextProvider] Không tìm thấy hồ sơ cho '{state.UserId}' — bỏ qua.");
            return new ValueTask<AIContext>(new AIContext());
        }

        string tags = profile.DietaryTags.Length > 0
            ? string.Join(", ", profile.DietaryTags)
            : "không có";

        Output.Blue($"  [ProfileContextProvider] {profile.Name} | nhãn: {tags} | kỹ năng: {profile.SkillLevel}");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions =
                $"Hồ sơ người dùng của {profile.Name}:\n" +
                $"- Hạn chế ăn kiêng: {tags}\n" +
                $"- Kỹ năng nấu ăn: {profile.SkillLevel}\n" +
                $"Luôn tôn trọng hạn chế ăn kiêng. Điều chỉnh giải thích theo kỹ năng của người dùng."
        });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default) => default;
}

// ──────────────────────────────────────────────────────────────────────────────

public static class CookingCoachV2
{
    private static IChatClient BuildChatClient()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        return new OpenAIClient(apiKey).GetChatClient("gpt-4.1-nano").AsIChatClient();
    }

    public static async Task RunSample()
    {
        Output.Title("MealMate — V2: ProfileProvider");
        Output.Gray("Mỗi kịch bản chạy TRƯỚC (không có providers) rồi SAU (ProfileContextProvider hoạt động).");
        Output.Gray("Quan sát hành vi agent thay đổi khi nó biết đang nói chuyện với ai.");
        Output.Separator();

        var chatClient = BuildChatClient();

        // ── KỊCH BẢN A — Nguy cơ dị ứng ─────────────────────────────────────
        Output.Yellow("KỊCH BẢN A — Nguy cơ dị ứng");
        Output.Gray("alice là người ăn chay và dị ứng đậu phộng. Agent nhận prompt có đậu phộng.");
        Output.Gray("Người dùng: Gợi ý bữa tối nhanh với sốt chấm đậu phộng.");
        Output.Separator();

        Output.Gray("  [TRƯỚC — không có ProfileContextProvider]");
        // System prompt không đề cập dị ứng để buộc vi phạm hiện ra rõ
        var beforeA = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new()
            {
                Instructions = "Bạn là trợ lý nấu ăn hữu ích. Đừng tự ý đề cập đến dị ứng thực phẩm hoặc hạn chế ăn kiêng trừ khi người dùng đề cập trước. Chỉ trả lời đúng yêu cầu."
            }
        });
        var rBeforeA = await beforeA.RunAsync("Gợi ý cho tôi một bữa tối nhanh với sốt chấm đậu phộng.");
        Output.Green($"  Agent: {rBeforeA.Text}");

        bool mentionsAllergy = rBeforeA.Text.Contains("dị ứng", StringComparison.OrdinalIgnoreCase)
            || rBeforeA.Text.Contains("allergy", StringComparison.OrdinalIgnoreCase)
            || rBeforeA.Text.Contains("cảnh báo", StringComparison.OrdinalIgnoreCase);

        if (!mentionsAllergy)
            Output.Red("  ↳ Vấn đề: agent đề xuất đậu phộng mà KHÔNG có cảnh báo dị ứng — nguy hiểm cho alice!");
        else
            Output.Red("  ↳ Vấn đề: agent thêm cảnh báo nhưng không biết alice cụ thể dị ứng đậu phộng — chỉ đoán mò.");

        Output.Separator();
        Output.Gray("  [SAU — ProfileContextProvider hoạt động]");
        var afterA = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var rAfterA = await afterA.RunAsync("Gợi ý cho tôi một bữa tối nhanh với sốt chấm đậu phộng.");
        Output.Green($"  Agent: {rAfterA.Text}");
        Output.Gray("  ✓ Đã sửa: hồ sơ ăn kiêng được tiêm trước mỗi lần gọi LLM — agent biết alice dị ứng đậu phộng.");
        Output.Separator();

        // ── KỊCH BẢN B — Không phù hợp kỹ năng ─────────────────────────────
        Output.Yellow("KỊCH BẢN B — Không phù hợp kỹ năng");
        Output.Gray("alice là người nấu ăn mới. 'Beurre blanc' là kỹ thuật Pháp chuyên nghiệp.");
        Output.Gray("Người dùng: Làm thế nào để làm beurre blanc hoàn hảo từ đầu?");
        Output.Separator();

        Output.Gray("  [TRƯỚC — không có ProfileContextProvider]");
        var beforeB = chatClient.AsAIAgent("Bạn là trợ lý nấu ăn hữu ích.");
        var rBeforeB = await beforeB.RunAsync("Làm thế nào để làm beurre blanc hoàn hảo từ đầu?");
        Output.Green($"  Agent: {rBeforeB.Text}");
        Output.Red("  ↳ Vấn đề: giải thích cấp độ chuyên nghiệp, không điều chỉnh cho người mới.");

        Output.Separator();
        Output.Gray("  [SAU — ProfileContextProvider hoạt động]");
        var afterB = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var rAfterB = await afterB.RunAsync("Làm thế nào để làm beurre blanc hoàn hảo từ đầu?");
        Output.Green($"  Agent: {rAfterB.Text}");
        Output.Gray("  ✓ Đã sửa: kỹ năng được tiêm — giải thích điều chỉnh cho người mới bắt đầu.");
        Output.Separator();

        // ── KỊCH BẢN C — Hạn chế ăn kiêng qua các lượt ─────────────────────
        Output.Yellow("KỊCH BẢN C — Hạn chế ăn kiêng qua các lượt");
        Output.Gray("alice là người ăn chay. Lượt 1: hỏi món pasta. Lượt 2: hỏi phiên bản có thịt.");
        Output.Separator();

        Output.Gray("  [TRƯỚC — không có ProfileContextProvider]");
        var beforeC = chatClient.AsAIAgent("Bạn là trợ lý nấu ăn hữu ích.");
        var sessionBeforeC = await beforeC.CreateSessionAsync();
        Output.Gray("  Lượt 1: Món mì pasta nào tôi có thể nấu được?");
        var rBeforeC1 = await beforeC.RunAsync("Món mì pasta nào tôi có thể nấu được?", sessionBeforeC);
        Output.Green($"  Agent: {rBeforeC1.Text}");
        Output.Gray("  Lượt 2: Bạn có thể gợi ý phiên bản có thịt không?");
        var rBeforeC2 = await beforeC.RunAsync("Bạn có thể gợi ý phiên bản có thịt không?", sessionBeforeC);
        Output.Green($"  Agent: {rBeforeC2.Text}");
        Output.Red("  ↳ Vấn đề: agent vui vẻ gợi ý thịt — nó không biết alice là người ăn chay.");

        Output.Separator();
        Output.Gray("  [SAU — ProfileContextProvider hoạt động]");
        var afterC = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var sessionAfterC = await afterC.CreateSessionAsync();
        Output.Gray("  Lượt 1: Món mì pasta nào tôi có thể nấu được?");
        var rAfterC1 = await afterC.RunAsync("Món mì pasta nào tôi có thể nấu được?", sessionAfterC);
        Output.Green($"  Agent: {rAfterC1.Text}");
        Output.Gray("  Lượt 2: Bạn có thể gợi ý phiên bản có thịt không?");
        var rAfterC2 = await afterC.RunAsync("Bạn có thể gợi ý phiên bản có thịt không?", sessionAfterC);
        Output.Green($"  Agent: {rAfterC2.Text}");
        Output.Gray("  ✓ Đã sửa: hạn chế được tiêm lại mỗi lượt — agent từ chối hoặc cảnh báo.");
        Output.Separator();

        // ── KỊCH BẢN D — Người dùng không xác định: xử lý uyển chuyển ────────
        Output.Yellow("KỊCH BẢN D — Người dùng không xác định (xử lý uyển chuyển)");
        Output.Gray("userId='unknown' không có trong store. Provider không được crash.");
        Output.Gray("Người dùng: Tối nay tôi nên nấu gì?");
        Output.Separator();

        Output.Gray("  [TRƯỚC — không có ProfileContextProvider]");
        var beforeD = chatClient.AsAIAgent("Bạn là trợ lý nấu ăn hữu ích.");
        var rBeforeD = await beforeD.RunAsync("Tối nay tôi nên nấu gì?");
        Output.Green($"  Agent: {rBeforeD.Text}");
        Output.Red("  ↳ Lưu ý: câu trả lời chung — không có context hồ sơ.");

        Output.Separator();
        Output.Gray("  [SAU — ProfileContextProvider với userId không xác định]");
        var afterD = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("unknown")]
        });
        var rAfterD = await afterD.RunAsync("Tối nay tôi nên nấu gì?");
        Output.Green($"  Agent: {rAfterD.Text}");
        Output.Gray("  ✓ Không crash: provider trả về AIContext() rỗng khi không tìm thấy hồ sơ.");
        Output.Separator();

        // ── KỊCH BẢN E — Xuyên phiên bản ─────────────────────────────────────
        Output.Yellow("KỊCH BẢN E — Xuyên phiên bản [V2 — chỉ có profile]");
        Output.Gray("Cùng 3 lượt chạy ở mọi phiên bản. Có profile; kho đồ vẫn chưa biết.");
        Output.Gray("Lượt 3 'Nếu không có parmesan?' — agent vẫn chưa biết kho đồ của alice.");

        var agentE = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var sessionE = await agentE.CreateSessionAsync();

        Output.Gray("Lượt 1: Tôi muốn ăn gì đó nhanh mà no cho bữa tối tối nay.");
        var rE1 = await agentE.RunAsync("Tôi muốn ăn gì đó nhanh mà no cho bữa tối tối nay.", sessionE);
        Output.Green($"Agent: {rE1.Text}");

        Output.Gray("Lượt 2: Nghe hay đó — bạn có thể cho tôi công thức đầy đủ không?");
        var rE2 = await agentE.RunAsync("Nghe hay đó — bạn có thể cho tôi công thức đầy đủ không?", sessionE);
        Output.Green($"Agent: {rE2.Text}");

        Output.Gray("Lượt 3: Nếu tôi không có phô mai parmesan thì sao?");
        var rE3 = await agentE.RunAsync("Nếu tôi không có phô mai parmesan thì sao?", sessionE);
        Output.Green($"Agent: {rE3.Text}");

        Output.Separator();
        Output.Yellow("V2 hoàn tất. Chạy V3 để thêm PantryProvider — sửa lỗi mù parmesan ở lượt 3.");
    }
}
