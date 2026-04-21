using System.Text.Json.Serialization;
using Samples.SampleUtilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Samples.Labs.MealMate.V4_FactLoggingProvider;

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
// ProfileContextProvider
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
// PantryContextProvider
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
            return new ValueTask<AIContext>(new AIContext());

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
// FactLoggingProvider — trích xuất quyết định phiên và lưu trong session state
// ──────────────────────────────────────────────────────────────────────────────

file sealed class FactLoggingProvider : AIContextProvider
{
    // Tín hiệu xác nhận quyết định — cả tiếng Anh và tiếng Việt
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

    // TRƯỚC lời gọi: tiêm các quyết định phiên đã tích lũy
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var log = _sessionState.GetOrInitializeState(context.Session);

        if (log.Facts.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        string facts = string.Join("\n",
            log.Facts.Select((f, i) => $"  {i + 1}. [Lượt {f.Turn}] {f.Fact}"));

        Output.Blue($"  [FactLoggingProvider] Tiêm {log.Facts.Count} quyết định phiên.");

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions =
                "Các quyết định đã đưa ra trong phiên nấu ăn này (KHÔNG hỏi lại):\n" + facts
        });
    }

    // SAU lời gọi: quét phản hồi agent tìm tín hiệu quyết định và lưu
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var log = _sessionState.GetOrInitializeState(context.Session);
        int turnNumber = log.Facts.Count + 1;

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
                    log.Facts.Add(new CookingSessionFact(turnNumber, fact));
                    Output.Blue($"  [FactLoggingProvider] Đã lưu: \"{fact}\"");
                }
            }
        }

        _sessionState.SaveState(context.Session, log);
        return default;
    }

    internal void PrintLog(AgentSession session, string label)
    {
        var log = _sessionState.GetOrInitializeState(session);
        Output.Magenta($"  [{label}] Nhật ký sự kiện ({log.Facts.Count} mục):");
        if (log.Facts.Count == 0)
            Output.Magenta("    (trống)");
        else
            foreach (var f in log.Facts)
                Output.Magenta($"    Lượt {f.Turn}: {f.Fact}");
    }
}

// ──────────────────────────────────────────────────────────────────────────────

public static class CookingCoachV4
{
    private static IChatClient BuildChatClient()
    {
        string apiKey = SecretManager.GetOpenAIApiKey();
        return new OpenAIClient(apiKey).GetChatClient("gpt-4.1-nano").AsIChatClient();
    }

    public static async Task RunSample()
    {
        Output.Title("MealMate — V4: FactLoggingProvider");
        Output.Gray("Kịch bản A cho thấy lỗi hỏi lại KHÔNG CÓ FactLoggingProvider, rồi sửa CÓ provider.");
        Output.Gray("StoreAIContextAsync trích xuất quyết định phiên. ProviderSessionState<T> cô lập từng phiên.");
        Output.Separator();

        var chatClient = BuildChatClient();

        // ── KỊCH BẢN A — Mất quyết định vs. lưu giữ ─────────────────────────
        Output.Yellow("KỊCH BẢN A — Mất quyết định giữa các lượt (vấn đề FactLoggingProvider giải quyết)");
        Output.Gray("Cuộc trò chuyện 3 lượt: chọn món Ý → không pasta → hỏi món chính.");
        Output.Separator();

        Output.Gray("  [KHÔNG CÓ FactLoggingProvider — xem agent hỏi lại]");
        var agentWithout = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new PantryContextProvider("alice")
            ]
        });
        var sessionWithout = await agentWithout.CreateSessionAsync();

        Output.Gray("  Lượt 1: Tối nay mình ăn món Ý nhé.");
        var rW1 = await agentWithout.RunAsync("Tối nay mình ăn món Ý nhé.", sessionWithout);
        Output.Green($"  Agent: {rW1.Text}");

        Output.Gray("  Lượt 2: Tôi không muốn ăn mì — nặng bụng lắm.");
        var rW2 = await agentWithout.RunAsync("Tôi không muốn ăn mì — nặng bụng lắm.", sessionWithout);
        Output.Green($"  Agent: {rW2.Text}");

        Output.Gray("  Lượt 3: Vậy thì món chính nào ngon?");
        var rW3 = await agentWithout.RunAsync("Vậy thì món chính nào ngon?", sessionWithout);
        Output.Green($"  Agent: {rW3.Text}");
        Output.Red("  ↳ Vấn đề: agent có thể hỏi lại ẩm thực hoặc pasta — quyết định bốc hơi giữa các lượt.");

        Output.Separator();
        Output.Gray("  [CÓ FactLoggingProvider — quyết định tích lũy trong ProviderSessionState<T>]");
        var factProviderA = new FactLoggingProvider();
        var agentWith = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new PantryContextProvider("alice"),
                factProviderA
            ]
        });
        var sessionWith = await agentWith.CreateSessionAsync();

        Output.Gray("  Lượt 1: Tối nay mình ăn món Ý nhé.");
        var rA1 = await agentWith.RunAsync("Tối nay mình ăn món Ý nhé.", sessionWith);
        Output.Green($"  Agent: {rA1.Text}");
        factProviderA.PrintLog(sessionWith, "Nhật ký sau lượt 1");

        Output.Gray("  Lượt 2: Tôi không muốn ăn mì — nặng bụng lắm.");
        var rA2 = await agentWith.RunAsync("Tôi không muốn ăn mì — nặng bụng lắm.", sessionWith);
        Output.Green($"  Agent: {rA2.Text}");
        factProviderA.PrintLog(sessionWith, "Nhật ký sau lượt 2");

        Output.Gray("  Lượt 3: Vậy thì món chính nào ngon?");
        var rA3 = await agentWith.RunAsync("Vậy thì món chính nào ngon?", sessionWith);
        Output.Green($"  Agent: {rA3.Text}");
        Output.Gray("  ✓ Đã sửa: quyết định được tiêm vào mỗi lượt tiếp theo — agent không hỏi lại.");
        Output.Separator();

        // ── KỊCH BẢN B — Cô lập phiên ────────────────────────────────────────
        Output.Yellow("KỊCH BẢN B — Hai phiên song song: sự kiện phải tách biệt");
        Output.Gray("Alice chọn món Ý; Bob chọn món Thái. FactLog của mỗi phiên phải độc lập.");

        var factProviderB = new FactLoggingProvider();
        var agentB = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new PantryContextProvider("alice"),
                factProviderB
            ]
        });

        var sessionAlice = await agentB.CreateSessionAsync();
        var sessionBob   = await agentB.CreateSessionAsync();

        Output.Gray("[Alice] Lượt 1: Tối nay ăn món Ý đi.");
        var rAlice = await agentB.RunAsync("Tối nay ăn món Ý đi.", sessionAlice);
        Output.Green($"Agent (Alice): {rAlice.Text}");

        Output.Gray("[Bob] Lượt 1: Tối nay ăn món Thái đi.");
        var rBob = await agentB.RunAsync("Tối nay ăn món Thái đi.", sessionBob);
        Output.Green($"Agent (Bob): {rBob.Text}");

        factProviderB.PrintLog(sessionAlice, "Nhật ký của Alice");
        factProviderB.PrintLog(sessionBob,   "Nhật ký của Bob");
        Output.Gray("  ✓ Mỗi phiên có FactLog riêng — ProviderSessionState<T> ngăn rò rỉ giữa các phiên.");
        Output.Separator();

        // ── KỊCH BẢN C — Lượt đầu tiên: sự kiện trống ───────────────────────
        Output.Yellow("KỊCH BẢN C — Lượt đầu tiên với nhật ký sự kiện trống (không crash)");
        Output.Gray("ProvideAIContextAsync phải xử lý FactLog trống một cách uyển chuyển.");

        var factProviderC = new FactLoggingProvider();
        var agentC = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders = [new ProfileContextProvider("alice"), factProviderC]
        });
        var sessionC = await agentC.CreateSessionAsync();

        Output.Gray("Lượt 1 (chưa có sự kiện): Tối nay tôi nên nấu gì?");
        var rC1 = await agentC.RunAsync("Tối nay tôi nên nấu gì?", sessionC);
        Output.Green($"Agent: {rC1.Text}");
        factProviderC.PrintLog(sessionC, "sau lượt 1");
        Output.Gray("  ✓ Không crash với FactLog trống — ProvideAIContextAsync trả về AIContext() rỗng.");
        Output.Separator();

        // ── KỊCH BẢN D — Thay đổi ý giữa phiên ──────────────────────────────
        Output.Yellow("KỊCH BẢN D — Người dùng thay đổi ý giữa phiên");
        Output.Gray("Lượt 1: món Ý. Lượt 2: Thực ra, món Nhật. Lượt 3: vẫn là món Nhật.");

        var factProviderD = new FactLoggingProvider();
        var agentD = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new PantryContextProvider("alice"),
                factProviderD
            ]
        });
        var sessionD = await agentD.CreateSessionAsync();

        Output.Gray("Lượt 1: Mình làm món Ý nhé.");
        var rD1 = await agentD.RunAsync("Mình làm món Ý nhé.", sessionD);
        Output.Green($"Agent: {rD1.Text}");
        factProviderD.PrintLog(sessionD, "sau lượt 1");

        Output.Gray("Lượt 2: Thôi, mình chuyển sang món Nhật đi.");
        var rD2 = await agentD.RunAsync("Thôi, mình chuyển sang món Nhật đi.", sessionD);
        Output.Green($"Agent: {rD2.Text}");
        factProviderD.PrintLog(sessionD, "sau lượt 2");

        Output.Gray("Lượt 3: Tôi nên làm món gì?");
        var rD3 = await agentD.RunAsync("Tôi nên làm món gì?", sessionD);
        Output.Green($"Agent: {rD3.Text}");
        Output.Separator();

        // ── KỊCH BẢN E — Xuyên phiên bản ────────────────────────────────────
        Output.Yellow("KỊCH BẢN E — Xuyên phiên bản [V4 — profile + kho đồ + sự kiện]");
        Output.Gray("Cùng 3 lượt như V1–V3. Agent giờ nhớ quyết định trước mà không hỏi lại.");

        var factProviderE = new FactLoggingProvider();
        var agentE = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new() { Instructions = "Bạn là trợ lý nấu ăn hữu ích." },
            AIContextProviders =
            [
                new ProfileContextProvider("alice"),
                new PantryContextProvider("alice"),
                factProviderE
            ]
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
        factProviderE.PrintLog(sessionE, "cuối kịch bản xuyên phiên bản");

        Output.Separator();
        Output.Yellow("V4 hoàn tất. Chạy V5 để xem InvokingCoreAsync với lọc nguồn tin nhắn tường minh.");
    }
}
