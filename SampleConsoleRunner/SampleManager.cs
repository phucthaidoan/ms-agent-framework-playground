using Samples.ConversationMemory.V1_BasicSession;
using Samples.ConversationMemory.V2_SessionSerialization;
using Samples.ConversationMemory.V3_InMemoryHistory;
using Samples.ConversationMemory.V4a_CustomHistoryProvider_File;
using Samples.ConversationMemory.V4b_CustomHistoryProvider_Postgres;
using Samples.ConversationMemory.V5_CustomContextProvider;
using Samples.ConversationMemory.V6_Compaction;
using Samples.ConversationMemory.V7_Integration;
using Samples.ConversationMemory.V8_ToolCallHistory;
using Samples.CV_Screening;
using Samples.Recipe;
using Samples.Recipe.V1_HostLoop;
using Samples.Recipe.V2_ChatLoop;
using Samples.Recipe.V3_Streaming;
using Samples.Recipe.V4_DynamicInstructions;
using Samples.Recipe.V5_RAG;
using Samples.Recipe.V6_UserMemory;
using Samples.Recipe.V7_TypedMemory;
using Samples.Recipe.V8_ContextProviders;
using Samples.CV_Screening.V1_AgentAsTool;
using Samples.CV_Screening.V2_ChatLoop;
using Samples.CV_Screening.V3_Streaming;
using Samples.CV_Screening.V4_MultiRole;
using Samples.CV_Screening.V5_RAG;
using Samples.Observability;
using Samples.SampleUtilities;
using Samples.Section03;
using Samples.Section04;
using Samples.Section05;
using Samples.Section06;
using Samples.Section07;
using Samples.Section08;
using System.ComponentModel;
using System.Reflection;

namespace SampleConsoleRunner;

//This class deal with sample-selection and have nothing to do with the course as such
public static class SampleManager
{
    public static async Task RunSample(Sample sample)
    {
        Console.Clear();
        Console.ResetColor();

        if (sample == Sample.Interactive)
        {
            //Choose sample via interactivity
            Console.WriteLine("Available Samples");
            Output.Separator(false);
            Sample[] samplesToChooseFrom = Enum.GetValues<Sample>().Except([Sample.Interactive]).ToArray();
            IEnumerable<IGrouping<string, SampleDetails>> groups = samplesToChooseFrom.Select(x => x.GetDetails()).GroupBy(x => x.Section);
            foreach (IGrouping<string, SampleDetails> group in groups)
            {
                List<SampleDetails> values = group.ToList();
                Output.Title(group.Key);
                string samplesInSection = string.Join(" ", values);
                Console.WriteLine("- " + samplesInSection);
                Output.Separator(false);
            }

            Console.WriteLine("Enter the number of the sample you wish to run");
            Console.Write("> ");
            string input = Console.ReadLine() ?? string.Empty;
            int number = Convert.ToInt32(input);
            sample = (Sample)number;
        }

        Console.Clear();

        Output.Gray("Running sample: " + sample);
        switch (sample)
        {
            case Sample.TokenUsage:
                await TokenUsage.RunSample();
                break;
            case Sample.NormalVsStreamingResponse:
                await NormalVsStreamingResponse.RunSample();
                break;
            case Sample.Chatloop:
                await Chatloop.RunSample();
                break;
            case Sample.Instructions:
                await Instructions.RunSample();
                break;
            case Sample.CreatingTools:
                await CreatingTools.RunSample();
                break;
            case Sample.ConsumingMcp:
                await ConsumingMcpTools.RunSample();
                break;
            case Sample.ToolCallingMiddleware:
                await ToolCallingMiddleware.RunSample();
                break;
            case Sample.OtherAgentsAsTools:
                await OtherAgentsAsTools.RunSample();
                break;
            case Sample.WebSearch:
                await WebSearch.RunSample();
                break;
            case Sample.CodeInterpreter:
                await CodeInterpreter.RunSample();
                break;
            case Sample.StructuredOutput:
                await StructuredOutput.RunSample();
                break;
            case Sample.StructuredOutputInstructions:
                await StructuredOutputInstructions.RunSample();
                break;
            case Sample.LifeOfAnLlmCall:
                await LifeOfAnLlmCall.RunSample();
                break;
            case Sample.EmbeddingData:
                await EmbeddingData.RunSample();
                break;
            case Sample.IngestDataIntoVectorStore:
                await IngestDataIntoVectorStore.RunSample();
                break;
            case Sample.SearchAndUseVectorStore:
                await SearchAndUseVectorStore.RunSample();
                break;
            case Sample.SearchAsATool:
                await SearchAsATool.RunSample();
                break;
            case Sample.CvScreening:
                await CvScreening.RunSample();
                break;
            case Sample.CvScreeningV1:
                await CvScreeningV1.RunSample();
                break;
            case Sample.CvScreeningV2:
                await CvScreeningV2.RunSample();
                break;
            case Sample.CvScreeningV3:
                await CvScreeningV3.RunSample();
                break;
            case Sample.CvScreeningV4:
                await CvScreeningV4.RunSample();
                break;
            case Sample.CvScreeningV5:
                await CvScreeningV5.RunSample();
                break;
            case Sample.MealPlanner:
                await MealPlanner.RunSample();
                break;
            case Sample.MealPlannerV1:
                await MealPlannerV1.RunSample();
                break;
            case Sample.MealPlannerV2:
                await MealPlannerV2.RunSample();
                break;
            case Sample.MealPlannerV3:
                await MealPlannerV3.RunSample();
                break;
            case Sample.MealPlannerV4:
                await MealPlannerV4.RunSample();
                break;
            case Sample.MealPlannerV5:
                await MealPlannerV5.RunSample();
                break;
            case Sample.MealPlannerV6:
                await MealPlannerV6.RunSample();
                break;
            case Sample.MealPlannerV7:
                await MealPlannerV7.RunSample();
                break;
            case Sample.MealPlannerV8:
                await MealPlannerV8.RunSample();
                break;
            case Sample.SupportBotV1:
                await SupportBotV1.RunSample();
                break;
            case Sample.SupportBotV2:
                await SupportBotV2.RunSample();
                break;
            case Sample.SupportBotV3:
                await SupportBotV3.RunSample();
                break;
            case Sample.SupportBotV4a:
                await SupportBotV4a.RunSample();
                break;
            case Sample.SupportBotV4b:
                await SupportBotV4b.RunSample();
                break;
            case Sample.SupportBotV5:
                await SupportBotV5.RunSample();
                break;
            case Sample.SupportBotV6:
                await SupportBotV6.RunSample();
                break;
            case Sample.SupportBotV7:
                await SupportBotV7.RunSample();
                break;
            case Sample.SupportBotV8:
                await SupportBotV8.RunSample();
                break;
            case Sample.JaegerTracing:
                await JaegerTracing.RunSample();
                break;
            case Sample.Interactive:
            default:
                Console.WriteLine("No sample with that number :-(");
                break;
        }

        Output.Gray("--- Done ---");
        Console.ReadLine();
    }
}

public enum Sample
{
    Interactive = 0,

    [SampleDetails("Token Usage", SampleSection.Section3)]
    TokenUsage = 300,

    [SampleDetails("Streaming Response", SampleSection.Section4)]
    NormalVsStreamingResponse = 400,

    [SampleDetails("Chat-loop (AgentSession)", SampleSection.Section4)]
    Chatloop = 401,

    [SampleDetails("Instructions (Prompt Engineering)", SampleSection.Section4)]
    Instructions = 402,

    [SampleDetails("Creating Tools", SampleSection.Section5)]
    CreatingTools = 500,

    [SampleDetails("Consuming MCP Servers", SampleSection.Section5)]
    ConsumingMcp = 501,

    [SampleDetails("CodeInterpreter Tool", SampleSection.Section5)]
    CodeInterpreter = 502,

    [SampleDetails("Web Search Tool", SampleSection.Section5)]
    WebSearch = 503,

    [SampleDetails("Other Agents as Tools", SampleSection.Section5)]
    OtherAgentsAsTools = 504,

    [SampleDetails("Tool Calling Middleware", SampleSection.Section5)]
    ToolCallingMiddleware = 505,

    [SampleDetails("Structured Output", SampleSection.Section6)]
    StructuredOutput = 600,
    
    [SampleDetails("Structured Output (Instructions)", SampleSection.Section6)]
    StructuredOutputInstructions = 601,

    [SampleDetails("The Life of an LLM Call", SampleSection.Section7)]
    LifeOfAnLlmCall = 700,

    [SampleDetails("Embedding Data", SampleSection.Section8)]
    EmbeddingData = 800,

    [SampleDetails("Ingest Data into Vector Store", SampleSection.Section8)]
    IngestDataIntoVectorStore = 801,

    [SampleDetails("Search and Use Vector Store", SampleSection.Section8)]
    SearchAndUseVectorStore = 802,

    [SampleDetails("Search as a Tool", SampleSection.Section8)]
    SearchAsATool = 803,

    [SampleDetails("CV Screening & Interview Coordinator", SampleSection.CV_Screening)]
    CvScreening = 900,

    [SampleDetails("CV Screening V1 - Agent as Tool", SampleSection.CV_Screening)]
    CvScreeningV1 = 901,

    [SampleDetails("CV Screening V2 - Chat Loop", SampleSection.CV_Screening)]
    CvScreeningV2 = 902,

    [SampleDetails("CV Screening V3 - Streaming", SampleSection.CV_Screening)]
    CvScreeningV3 = 903,

    [SampleDetails("CV Screening V4 - Multi Role", SampleSection.CV_Screening)]
    CvScreeningV4 = 904,

    [SampleDetails("CV Screening V5 - RAG Powered", SampleSection.CV_Screening)]
    CvScreeningV5 = 905,

    [SampleDetails("Meal Planner — Multi-Agent Refinement", SampleSection.Recipe)]
    MealPlanner = 1000,

    [SampleDetails("Meal Planner V1 - Host-Orchestrated Loop", SampleSection.Recipe)]
    MealPlannerV1 = 1001,

    [SampleDetails("Meal Planner V2 - Chat Loop", SampleSection.Recipe)]
    MealPlannerV2 = 1002,

    [SampleDetails("Meal Planner V3 - Streaming", SampleSection.Recipe)]
    MealPlannerV3 = 1003,

    [SampleDetails("Meal Planner V4 - Dynamic Instructions", SampleSection.Recipe)]
    MealPlannerV4 = 1004,

    [SampleDetails("Meal Planner V5 - RAG Powered", SampleSection.Recipe)]
    MealPlannerV5 = 1005,

    [SampleDetails("Meal Planner V6 - Per-User Memory", SampleSection.Recipe)]
    MealPlannerV6 = 1006,

    [SampleDetails("Meal Planner V7 - Typed Memory Architecture", SampleSection.Recipe)]
    MealPlannerV7 = 1007,

    [SampleDetails("Meal Planner V8 - Context Providers", SampleSection.Recipe)]
    MealPlannerV8 = 1008,

    [SampleDetails("Jaeger Tracing (OpenTelemetry)", SampleSection.Observability)]
    JaegerTracing = 1100,

    [SampleDetails("Support Bot V1 - Basic Session", SampleSection.ConversationMemory)]
    SupportBotV1 = 1200,

    [SampleDetails("Support Bot V2 - Session Serialization", SampleSection.ConversationMemory)]
    SupportBotV2 = 1201,

    [SampleDetails("Support Bot V3 - InMemory History", SampleSection.ConversationMemory)]
    SupportBotV3 = 1202,

    [SampleDetails("Support Bot V4a - Custom History Provider (File)", SampleSection.ConversationMemory)]
    SupportBotV4a = 1203,

    [SampleDetails("Support Bot V4b - Custom History Provider (PostgreSQL)", SampleSection.ConversationMemory)]
    SupportBotV4b = 1204,

    [SampleDetails("Support Bot V5 - Custom Context Provider", SampleSection.ConversationMemory)]
    SupportBotV5 = 1205,

    [SampleDetails("Support Bot V6 - Compaction Strategies", SampleSection.ConversationMemory)]
    SupportBotV6 = 1206,

    [SampleDetails("Support Bot V7 - Integration", SampleSection.ConversationMemory)]
    SupportBotV7 = 1207,

    [SampleDetails("Support Bot V8 - Tool Calls in History", SampleSection.ConversationMemory)]
    SupportBotV8 = 1208,
}

public enum SampleSection
{
    [Description("Introduction to the course")]
    Section1,

    [Description("Hello World (Zero to first Prompt)")]
    Section2,

    [Description("Before we dive deeper...")]
    Section3,

    [Description("Chat")]
    Section4,

    [Description("Tool Calling")]
    Section5,

    [Description("Structured Output")]
    Section6,

    [Description("Intermission: The Life of an LLM Call")]
    Section7,

    [Description("RAG (Retrieval Augmented Generation)")]
    Section8,

    [Description("CV Screening & Interview Coordinator")]
    CV_Screening,

    [Description("AI-Powered Meal Plan Generator")]
    Recipe,

    [Description("Observability (OpenTelemetry / Jaeger)")]
    Observability,

    [Description("Conversations & Memory")]
    ConversationMemory,
}

public class SampleDetailsAttribute(string name, SampleSection section) : Attribute
{
    public string Name { get; } = name;
    public SampleSection Section { get; set; } = section;
}

public class SampleDetails
{
    public required int Number { get; set; }
    public required string Name { get; set; }
    public required string Section { get; set; }

    public override string ToString()
    {
        return $"[{Number}: {Name}]";
    }
}

public static class EnumExtensions
{
    public static SampleDetails GetDetails(this Sample sample)
    {
        Type enumType = sample.GetType();
        string name = Enum.GetName(enumType, sample) ?? throw new InvalidOperationException($"Name is null for {sample} ({enumType})");
        FieldInfo field = enumType.GetField(name) ?? throw new InvalidOperationException($"Field is null for {sample} ({enumType})");
        SampleDetailsAttribute attribute = field.GetCustomAttribute<SampleDetailsAttribute>()!;
        return new SampleDetails
        {
            Number = Convert.ToInt32(sample),
            Name = attribute.Name,
            Section = attribute.Section.ToString() + ": " + attribute.Section.Description()!
        };
    }

    public static string? Description(this Enum enumValue)
    {
        Type enumType = enumValue.GetType();
        string name = Enum.GetName(enumType, enumValue) ?? throw new InvalidOperationException($"Name is null for {enumValue} ({enumType})");
        FieldInfo field = enumType.GetField(name) ?? throw new InvalidOperationException($"Field is null for {enumValue} ({enumType})");
        return field.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }
}