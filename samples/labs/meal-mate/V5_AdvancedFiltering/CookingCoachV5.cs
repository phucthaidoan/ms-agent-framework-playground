using System.Text.Json.Serialization;
using Samples.SampleUtilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Samples.Labs.MealMate.V5_AdvancedFiltering;

record UserProfile(string UserId, string Name, string[] DietaryTags, string SkillLevel);
record PantryItem(string Name, string Quantity);
record CookingSessionFact(int Turn, string Fact);

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
    };

    public static List<PantryItem> GetPantry(string userId) =>
        Store.GetValueOrDefault(userId) ?? [];
}

// ──────────────────────────────────────────────────────────────────────────────
// ProfileContextProvider (giống V2–V4)
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
            return new ValueTask<AIContext>(new AIContext());

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
// FactLoggingProvider (giống V4)
// ──────────────────────────────────────────────────────────────────────────────

file sealed class FactLoggingProvider : AIContextProvider
{
    private static readonly string[] DecisionSignals =
        ["let's go with", "we'll make", "great choice", "tonight we're making",
         "decided on", "we'll do", "sounds like you want", "perfect, let's",
         "tối nay chúng ta làm", "tuyệt vời", "đã quyết định", "chúng ta sẽ làm",
         "nghe hay đó", "được rồi", "vậy là"];

    private sealed class FactLog
    {
        [JsonPropertyName("facts")] public List<CookingSessionFact> Facts { get; set; } = [];
    }

    private readonly ProviderSessionState<FactLog> _sessionState = new(
        stateInitializer: _ => new FactLog(),
        stateKey: nameof(FactLoggingProvider));

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var log = _sessionState.GetOrInitializeState(context.Session);

        if (log.Facts.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        string facts = string.Join("\n",
            log.Facts.Select((f, i) => $"  {i + 1}. [Lượt {f.Turn}] {f.Fact}"));

        Output.Blue($"  [FactLoggingProvider] Tiêm {log.Facts.Count} quyết định.");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = "Các quyết định đã đưa ra trong phiên này (KHÔNG hỏi lại):\n" + facts
        });
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var log = _sessionState.GetOrInitializeState(context.Session);
        int turn = log.Facts.Count + 1;

        foreach (var msg in context.ResponseMessages ?? [])
        {
            string text = msg.Text ?? string.Empty;
            string lower = text.ToLowerInvariant();

            foreach (string signal in DecisionSignals)
            {
                int idx = lower.IndexOf(signal, StringComparison.Ordinal);
                if (idx < 0) continue;

                int start = text.LastIndexOf('\n', idx) + 1;
                int end = text.IndexOfAny(['.', '!', '\n'], idx + signal.Length);
                string fact = end > 0
                    ? text[start..end].Trim()
                    : text[start..].Trim();

                if (!string.IsNullOrWhiteSpace(fact) &&
                    !log.Facts.Any(f => f.Fact.Equals(fact, StringComparison.OrdinalIgnoreCase)))
                {
                    log.Facts.Add(new CookingSessionFact(turn, fact));
                    Output.Blue($"  [FactLoggingProvider] Đã lưu: \"{fact}\"");
                }
            }
        }

        _sessionState.SaveState(context.Session, log);
        return default;
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// NaivePantryProvider — kiểu V3, KHÔNG có đánh dấu nguồn
// Dùng trong khối TRƯỚC của Kịch bản A để minh hoạ vấn đề vòng lặp phản hồi.
// ──────────────────────────────────────────────────────────────────────────────

file sealed class NaivePantryProvider : AIContextProvider
{
    private readonly string _userId;
    private sealed class State { [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty; }
    private readonly ProviderSessionState<State> _sessionState = new(_ => new State(), nameof(NaivePantryProvider));
    public NaivePantryProvider(string userId) { _userId = userId; }
    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    // Override InvokingCoreAsync để xem danh sách tin nhắn đầy đủ và log số lượng theo nguồn —
    // nhưng KHÔNG đánh dấu tin nhắn tiêm vào, nên lần sau nó sẽ được coi là External.
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (string.IsNullOrEmpty(state.UserId)) state.UserId = _userId;
        var items = FakePantryService.GetPantry(state.UserId);

        var allMessages = context.AIContext.Messages ?? [];
        var externalCount = allMessages.Count(m =>
            m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        // Đây là vấn đề: tin nhắn kho đồ từ lượt trước giờ trông giống External
        Output.Yellow($"  [NaivePantryProvider] tổng={allMessages.Count()}, external={externalCount}");
        var grouped = allMessages
            .GroupBy(m => m.GetAgentRequestMessageSourceType().ToString())
            .Select(g => $"{g.Key}={g.Count()}");
        Output.Yellow($"  [NaivePantryProvider] Nguồn: {string.Join(", ", grouped)}");

        if (items.Count == 0)
            return new ValueTask<AIContext>(new AIContext
            {
                Instructions = context.AIContext.Instructions,
                Messages     = allMessages,
                Tools        = context.AIContext.Tools
            });

        string list = string.Join("\n", items.Select(i => $"  - {i.Name}: {i.Quantity}"));

        // KHÔNG có WithAgentRequestMessageSource — lượt sau tin nhắn này sẽ được coi là External
        ChatMessage pantryMsg = new ChatMessage(ChatRole.User,
            $"Kho đồ hiện tại của tôi CHỈ có những nguyên liệu này:\n{list}\n" +
            $"Chỉ gợi ý công thức dùng những gì tôi có. Nếu thiếu nguyên liệu, hãy nói rõ ràng.");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages     = allMessages.Append(pantryMsg),
            Tools        = context.AIContext.Tools
        });
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default) => default;
}

// ──────────────────────────────────────────────────────────────────────────────
// AdvancedPantryProvider — override InvokingCoreAsync + InvokedCoreAsync
// CÓ đánh dấu nguồn: tin nhắn tiêm vào bị loại khỏi bộ lọc External lượt sau.
// ──────────────────────────────────────────────────────────────────────────────

file sealed class AdvancedPantryProvider : AIContextProvider
{
    private readonly string _userId;
    private sealed class State { [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty; }
    private readonly ProviderSessionState<State> _sessionState = new(_ => new State(), nameof(AdvancedPantryProvider));
    public AdvancedPantryProvider(string userId) { _userId = userId; }
    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        if (string.IsNullOrEmpty(state.UserId)) state.UserId = _userId;
        var items = FakePantryService.GetPantry(state.UserId);

        var allMessages = context.AIContext.Messages ?? [];

        // CHỐT: chỉ đếm External — tin nhắn tiêm vào từ lượt trước bị loại trừ
        var externalCount = allMessages.Count(m =>
            m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

        Output.Blue($"  [AdvancedPantryProvider] tổng={allMessages.Count()}, external={externalCount}");
        var grouped = allMessages
            .GroupBy(m => m.GetAgentRequestMessageSourceType().ToString())
            .Select(g => $"{g.Key}={g.Count()}");
        Output.Blue($"  [AdvancedPantryProvider] Nguồn: {string.Join(", ", grouped)}");

        if (items.Count == 0)
        {
            Output.Yellow($"  [AdvancedPantryProvider] Kho đồ trống — không tiêm gì.");
            return new ValueTask<AIContext>(new AIContext
            {
                Instructions = context.AIContext.Instructions,
                Messages     = allMessages,
                Tools        = context.AIContext.Tools
            });
        }

        string list = string.Join("\n", items.Select(i => $"  - {i.Name}: {i.Quantity}"));

        // CHỐT: đánh dấu tin nhắn tiêm vào để bộ lọc lượt sau loại trừ nó khỏi External
        ChatMessage pantryMsg = new ChatMessage(ChatRole.User,
                $"Kho đồ hiện tại của tôi CHỈ có những nguyên liệu này:\n{list}\n" +
                $"Chỉ gợi ý công thức dùng những gì tôi có. Nếu thiếu nguyên liệu, hãy nói rõ ràng.")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!);

        Output.Blue($"  [AdvancedPantryProvider] Tiêm {items.Count} mục (đã đánh dấu AIContextProvider).");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = context.AIContext.Instructions,
            Messages     = allMessages.Append(pantryMsg),
            Tools        = context.AIContext.Tools
        });
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
        {
            Output.Yellow($"  [AdvancedPantryProvider] Phát hiện InvokeException — bỏ qua xử lý hậu kỳ.");
            return default;
        }
        return default;
    }
}

// ──────────────────────────────────────────────────────────────────────────────

public static class CookingCoachV5
{
    private static IChatClient BuildChatClient()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        return new OpenAIClient(apiKey).GetChatClient("gpt-4.1-nano").AsIChatClient();
    }

    public static async Task RunSample()
    {
        Output.Title("MealMate — V5: AdvancedFiltering (InvokingCoreAsync)");
        Output.Gray("Kịch bản A cho thấy vấn đề vòng lặp KHÔNG CÓ đánh dấu, rồi sửa CÓ đánh dấu.");
        Output.Gray("AdvancedPantryProvider override InvokingCoreAsync để kiểm soát danh sách tin nhắn đầy đủ.");
        Output.Separator();

        var chatClient = BuildChatClient();

        // ── KỊCH BẢN A — Vòng lặp phản hồi vs. đánh dấu nguồn ───────────────
        Output.Yellow("KỊCH BẢN A — Vòng lặp tin nhắn kho đồ (vấn đề đánh dấu giải quyết)");
        Output.Gray("Vấn đề: không có WithAgentRequestMessageSource, tin nhắn kho đồ tiêm lượt 1");
        Output.Gray("xuất hiện lại trong lịch sử được gắn thẻ External lượt 2 — trông như người dùng gõ.");
        Output.Gray("Quan sát số 'external=' trong log: phải là 1 (chỉ input thực của người dùng).");
        Output.Separator();

        Output.Gray("  [KHÔNG CÓ đánh dấu nguồn — NaivePantryProvider (kiểu V3)]");
        Output.Gray("  Chạy 2 lượt. Quan sát external count ở lượt 2 — sẽ bị thổi phồng.");
        var agentNaive = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new NaivePantryProvider("alice")
            ]
        });
        var sessionNaive = await agentNaive.CreateSessionAsync();

        Output.Gray("  Lượt 1: Tối nay tôi có thể nấu gì?");
        var rN1 = await agentNaive.RunAsync("Tối nay tôi có thể nấu gì?", sessionNaive);
        Output.Green($"  Agent: {rN1.Text}");

        Output.Gray("  Lượt 2: Cho tôi công thức đầy đủ đi.  ← quan sát dòng [Nguồn] ở trên");
        var rN2 = await agentNaive.RunAsync("Cho tôi công thức đầy đủ đi.", sessionNaive);
        Output.Green($"  Agent: {rN2.Text}");
        Output.Red("  ↳ Vấn đề: 'external' count bao gồm tin nhắn kho đồ tiêm vào — vòng lặp phản hồi.");

        Output.Separator();
        Output.Gray("  [CÓ đánh dấu nguồn — AdvancedPantryProvider]");
        Output.Gray("  Cùng 2 lượt. external count phải ở mức 1 ở lượt 2.");
        var agentStamped = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new AdvancedPantryProvider("alice")
            ]
        });
        var sessionStamped = await agentStamped.CreateSessionAsync();

        Output.Gray("  Lượt 1: Tối nay tôi có thể nấu gì?");
        var rS1 = await agentStamped.RunAsync("Tối nay tôi có thể nấu gì?", sessionStamped);
        Output.Green($"  Agent: {rS1.Text}");

        Output.Gray("  Lượt 2: Cho tôi công thức đầy đủ đi.  ← external=1 bây giờ");
        var rS2 = await agentStamped.RunAsync("Cho tôi công thức đầy đủ đi.", sessionStamped);
        Output.Green($"  Agent: {rS2.Text}");
        Output.Gray("  ✓ Đã sửa: tin nhắn kho đồ được đánh dấu AIContextProvider — loại khỏi External count.");
        Output.Separator();

        // ── KỊCH BẢN B — Sự kiện fact-log không bị nhầm là kho đồ ───────────
        Output.Yellow("KỊCH BẢN B — Mục nhật ký sự kiện không bị nhầm là nguyên liệu kho đồ");
        Output.Gray("FactLoggingProvider tiêm 'Đã quyết định: món Ý'. Không có lọc, có thể bị");
        Output.Gray("đếm là input người dùng cùng với context kho đồ. Quan sát external count vẫn sạch.");

        var factProviderB = new FactLoggingProvider();
        var agentB = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new AdvancedPantryProvider("alice"),
                factProviderB
            ]
        });
        var sessionB = await agentB.CreateSessionAsync();

        Output.Gray("Lượt 1: Tối nay ăn Ý — tối của mì pasta.");
        var rB1 = await agentB.RunAsync("Tối nay ăn Ý — tối của mì pasta.", sessionB);
        Output.Green($"Agent: {rB1.Text}");

        Output.Gray("Lượt 2: Tôi có thể làm gì?  ← 'Ý' trong fact-log không được thổi phồng external count");
        var rB2 = await agentB.RunAsync("Tôi có thể làm gì?", sessionB);
        Output.Green($"Agent: {rB2.Text}");
        Output.Gray("  ✓ Log AdvancedPantryProvider cho thấy external=1 dù có fact-log injection hoạt động.");
        Output.Separator();

        // ── KỊCH BẢN C — Bảo vệ exception trong InvokedCoreAsync ─────────────
        Output.Yellow("KỊCH BẢN C — Bảo vệ exception trong InvokedCoreAsync");
        Output.Gray("InvokedCoreAsync kiểm tra context.InvokeException trước khi xử lý hậu kỳ.");
        Output.Gray("Lỗi LLM thực (mạng, hạn ngạch) đặt InvokeException. Chúng ta log và bỏ qua sạch sẽ.");
        Output.Gray("Chạy bình thường ở đây — đường dẫn bảo vệ kích hoạt khi LLM thực sự lỗi.");

        var agentC = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new AdvancedPantryProvider("alice")
            ]
        });

        Output.Gray("Lượt 1 (bình thường): Tối nay tôi có thể làm gì?");
        var rC = await agentC.RunAsync("Tối nay tôi có thể làm gì?");
        Output.Green($"Agent: {rC.Text}");
        Output.Gray("  ✓ Không có exception — InvokedCoreAsync hoàn thành bình thường.");
        Output.Gray("  Lưu ý: trong production, timeout LLM sẽ đặt InvokeException và bảo vệ kích hoạt.");
        Output.Separator();

        // ── KỊCH BẢN D — Xuyên phiên bản ────────────────────────────────────
        Output.Yellow("KỊCH BẢN D — Xuyên phiên bản [V5 — lọc nâng cao]");
        Output.Gray("Cùng 3 lượt như V1–V4. Chất lượng output giống V4, nay có lọc tường minh được log.");

        var factProviderD = new FactLoggingProvider();
        var agentD = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new AdvancedPantryProvider("alice"),
                factProviderD
            ]
        });
        var sessionD = await agentD.CreateSessionAsync();

        Output.Gray("Lượt 1: Tôi muốn ăn gì đó nhanh mà no cho bữa tối tối nay.");
        var rD1 = await agentD.RunAsync("Tôi muốn ăn gì đó nhanh mà no cho bữa tối tối nay.", sessionD);
        Output.Green($"Agent: {rD1.Text}");

        Output.Gray("Lượt 2: Nghe hay đó — bạn có thể cho tôi công thức đầy đủ không?");
        var rD2 = await agentD.RunAsync("Nghe hay đó — bạn có thể cho tôi công thức đầy đủ không?", sessionD);
        Output.Green($"Agent: {rD2.Text}");

        Output.Gray("Lượt 3: Nếu tôi không có phô mai parmesan thì sao?");
        var rD3 = await agentD.RunAsync("Nếu tôi không có phô mai parmesan thì sao?", sessionD);
        Output.Green($"Agent: {rD3.Text}");

        Output.Separator();
        Output.Title("V5 hoàn tất — toàn bộ lab xong.");
        Output.Gray("Những gì bạn học được từ V1 → V5:");
        Output.Gray("  V1: Agent mù quáng — không có profile, không có kho đồ, không có trí nhớ.");
        Output.Gray("  V2: ProfileContextProvider tiêm nhãn ăn kiêng + kỹ năng vô điều kiện.");
        Output.Gray("  V3: PantryContextProvider thêm kho đồ trực tiếp; hai providers kết hợp mượt mà.");
        Output.Gray("  V4: FactLoggingProvider lưu quyết định phiên; ProviderSessionState<T> ngăn rò rỉ giữa phiên.");
        Output.Gray("  V5: InvokingCoreAsync cho kiểm soát đầy đủ — đánh dấu nguồn ngăn vòng lặp phản hồi, InvokedCoreAsync bảo vệ exception.");
    }
}
