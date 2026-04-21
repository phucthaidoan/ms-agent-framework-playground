using System.Text.Json.Serialization;
using Samples.SampleUtilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Samples.Labs.MealMate.V3_PantryProvider;

record UserProfile(string UserId, string Name, string[] DietaryTags, string SkillLevel);
record PantryItem(string Name, string Quantity);

// ──────────────────────────────────────────────────────────────────────────────
// Fake services
// ──────────────────────────────────────────────────────────────────────────────

file static class FakeProfileService
{
    private static readonly Dictionary<string, UserProfile> Store = new()
    {
        ["alice"] = new("alice", "Alice", ["vegetarian", "nut-allergy"], "beginner"),
        ["bob"]   = new("bob",   "Bob",   ["lactose-intolerant"],        "intermediate"),
    };

    public static UserProfile? GetProfile(string userId) => Store.GetValueOrDefault(userId);
}

file static class FakePantryService
{
    private static readonly Dictionary<string, List<PantryItem>> Store = new()
    {
        ["alice"] = [
            new("pasta",       "500g"),
            new("canned tuna", "2 cans"),
            new("olive oil",   "half a bottle"),
            new("garlic",      "1 bulb"),
            new("lemon",       "2"),
        ],
        ["bob"] = [
            new("pasta",  "500g"),
            new("milk",   "1L"),
            new("garlic", "1 bulb"),
            new("butter", "200g"),
        ],
        ["empty-user"] = [],
    };

    public static List<PantryItem> GetPantry(string userId) =>
        Store.GetValueOrDefault(userId) ?? [];
}

// ──────────────────────────────────────────────────────────────────────────────
// ProfileContextProvider (giống V2)
// ──────────────────────────────────────────────────────────────────────────────

file sealed class ProfileContextProvider : AIContextProvider
{
    private readonly string _userId;
    private sealed class State { [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty; }
    private readonly ProviderSessionState<State> _sessionState = new(_ => new State(), nameof(ProfileContextProvider));
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
            Output.Yellow($"  [ProfileContextProvider] Không có hồ sơ cho '{state.UserId}' — bỏ qua.");
            return new ValueTask<AIContext>(new AIContext());
        }

        string tags = profile.DietaryTags.Length > 0
            ? string.Join(", ", profile.DietaryTags) : "không có";

        Output.Blue($"  [ProfileContextProvider] {profile.Name} | nhãn: {tags} | kỹ năng: {profile.SkillLevel}");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions =
                $"Hồ sơ người dùng của {profile.Name}:\n" +
                $"- Hạn chế ăn kiêng: {tags}\n" +
                $"- Kỹ năng nấu ăn: {profile.SkillLevel}\n" +
                $"Luôn tôn trọng hạn chế ăn kiêng. Điều chỉnh giải thích theo kỹ năng."
        });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default) => default;
}

// ──────────────────────────────────────────────────────────────────────────────
// PantryContextProvider — tiêm kho đồ hiện tại dưới dạng tin nhắn người dùng
// ──────────────────────────────────────────────────────────────────────────────

file sealed class PantryContextProvider : AIContextProvider
{
    private readonly string _userId;
    private sealed class State { [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty; }
    private readonly ProviderSessionState<State> _sessionState = new(_ => new State(), nameof(PantryContextProvider));
    public PantryContextProvider(string userId) { _userId = userId; }
    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (string.IsNullOrEmpty(state.UserId)) state.UserId = _userId;
        var items = FakePantryService.GetPantry(state.UserId);

        if (items.Count == 0)
        {
            Output.Yellow($"  [PantryContextProvider] Kho đồ trống cho '{state.UserId}' — không tiêm gì.");
            return new ValueTask<AIContext>(new AIContext());
        }

        string list = string.Join("\n", items.Select(i => $"  - {i.Name}: {i.Quantity}"));
        Output.Blue($"  [PantryContextProvider] {items.Count} mục cho '{state.UserId}'.");

        return new ValueTask<AIContext>(new AIContext
        {
            Messages =
            [
                new ChatMessage(ChatRole.User,
                    $"Kho đồ hiện tại của tôi CHỈ có những nguyên liệu này:\n{list}\n" +
                    $"Chỉ gợi ý công thức dùng những gì tôi có. Nếu thiếu nguyên liệu, hãy nói rõ ràng.")
            ]
        });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default) => default;
}

// ──────────────────────────────────────────────────────────────────────────────

public static class CookingCoachV3
{
    private static IChatClient BuildChatClient()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        return new OpenAIClient(apiKey).GetChatClient("gpt-4.1-nano").AsIChatClient();
    }

    public static async Task RunSample()
    {
        Output.Title("MealMate — V3: PantryProvider");
        Output.Gray("Mỗi kịch bản chạy TRƯỚC (chỉ có profile) rồi SAU (profile + pantry).");
        Output.Gray("Quan sát nhận thức về kho đồ thay đổi gợi ý của agent như thế nào.");
        Output.Separator();

        var chatClient = BuildChatClient();

        // ── KỊCH BẢN A — Mù kho đồ ───────────────────────────────────────────
        Output.Yellow("KỊCH BẢN A — Mù kho đồ");
        Output.Gray("Kho của alice: pasta, cá ngừ, dầu ô liu, tỏi, chanh. Không có parmesan.");
        Output.Gray("Người dùng: Tối nay tôi có thể làm gì để ăn?");
        Output.Separator();

        Output.Gray("  [TRƯỚC — chỉ có profile, không có PantryContextProvider]");
        var beforeA = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var rBeforeA = await beforeA.RunAsync("Tối nay tôi có thể làm gì để ăn?");
        Output.Green($"  Agent: {rBeforeA.Text}");
        Output.Red("  ↳ Vấn đề: agent gợi ý công thức cần nguyên liệu alice không có.");

        Output.Separator();
        Output.Gray("  [SAU — profile + PantryContextProvider]");
        var afterA = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice"), new PantryContextProvider("alice")]
        });
        var rAfterA = await afterA.RunAsync("Tối nay tôi có thể làm gì để ăn?");
        Output.Green($"  Agent: {rAfterA.Text}");
        Output.Gray("  ✓ Đã sửa: agent chỉ dùng những gì alice thực sự có.");
        Output.Separator();

        // ── KỊCH BẢN B — Thiếu nguyên liệu được báo hiệu ────────────────────
        Output.Yellow("KỊCH BẢN B — Thiếu nguyên liệu được báo hiệu");
        Output.Gray("Cacio e pepe cần parmesan/pecorino. Alice không có cả hai.");
        Output.Gray("Người dùng: Tôi muốn làm cacio e pepe tối nay. Tôi cần những nguyên liệu gì?");
        Output.Separator();

        Output.Gray("  [TRƯỚC — chỉ có profile]");
        var beforeB = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice")]
        });
        var rBeforeB = await beforeB.RunAsync("Tôi muốn làm cacio e pepe tối nay. Tôi cần những nguyên liệu gì?");
        Output.Green($"  Agent: {rBeforeB.Text}");
        Output.Red("  ↳ Vấn đề: agent liệt kê parmesan/pecorino — alice không có chúng.");

        Output.Separator();
        Output.Gray("  [SAU — profile + PantryContextProvider]");
        var afterB = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice"), new PantryContextProvider("alice")]
        });
        var rAfterB = await afterB.RunAsync("Tôi muốn làm cacio e pepe tối nay. Tôi cần những nguyên liệu gì?");
        Output.Green($"  Agent: {rAfterB.Text}");
        Output.Gray("  ✓ Đã sửa: agent báo thiếu nguyên liệu và gợi ý thay thế chanh-tỏi-cá ngừ.");
        Output.Separator();

        // ── KỊCH BẢN C — Cả hai providers hoạt động ──────────────────────────
        Output.Yellow("KỊCH BẢN C — Cả hai providers kích hoạt cùng một lượt");
        Output.Gray("Xác minh ProfileContextProvider + PantryContextProvider kết hợp mượt mà.");
        Output.Gray("Người dùng: Gợi ý một món ăn cho tôi.");
        Output.Separator();

        Output.Gray("  [SAU — cả hai providers hoạt động, quan sát log]");
        var afterC = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice"), new PantryContextProvider("alice")]
        });
        var rAfterC = await afterC.RunAsync("Gợi ý một món ăn cho tôi.");
        Output.Green($"  Agent: {rAfterC.Text}");
        Output.Gray("  ✓ Cả dòng [ProfileContextProvider] và [PantryContextProvider] đều xuất hiện ở trên.");
        Output.Separator();

        // ── KỊCH BẢN D — Xung đột lactose ────────────────────────────────────
        Output.Yellow("KỊCH BẢN D — Xung đột lactose: bob có sữa nhưng không nên ăn");
        Output.Gray("bob không dung nạp lactose nhưng kho đồ có sữa và bơ.");
        Output.Gray("Không có context kho đồ, agent có thể gợi ý món nhiều sữa.");
        Output.Gray("Người dùng (bob): Tối nay tôi có thể nấu món mì pasta nào?");
        Output.Separator();

        Output.Gray("  [TRƯỚC — chỉ có profile, không có kho đồ]");
        var beforeD = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("bob")]
        });
        var rBeforeD = await beforeD.RunAsync("Tối nay tôi có thể nấu món mì pasta nào?");
        Output.Green($"  Agent: {rBeforeD.Text}");
        Output.Red("  ↳ Vấn đề: agent không biết bob có sữa ở nhà — có thể gợi ý tùy chọn không có xung đột cụ thể.");

        Output.Separator();
        Output.Gray("  [SAU — profile + PantryContextProvider cho bob]");
        var afterD = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("bob"), new PantryContextProvider("bob")]
        });
        var rAfterD = await afterD.RunAsync("Tối nay tôi có thể nấu món mì pasta nào?");
        Output.Green($"  Agent: {rAfterD.Text}");
        Output.Gray("  ✓ Đã sửa: agent thấy xung đột (không dung nạp lactose + sữa/bơ trong kho) và tư vấn phù hợp.");
        Output.Separator();

        // ── KỊCH BẢN E — Xuyên phiên bản ────────────────────────────────────
        Output.Yellow("KỊCH BẢN E — Xuyên phiên bản [V3 — profile + kho đồ]");
        Output.Gray("Cùng 3 lượt như V1/V2. Lượt 3: agent giờ biết không có parmesan.");

        var agentE = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice"), new PantryContextProvider("alice")]
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
        Output.Yellow("V3 hoàn tất. Chạy V4 để thêm FactLoggingProvider — quyết định phiên được lưu qua các lượt.");
    }
}
