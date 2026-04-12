// ReSharper disable ClassNeverInstantiated.Local

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using OpenAI;
using OpenAI.Chat;
using Samples.SampleUtilities;
using System.ClientModel;
using System.Text;
using static Samples.Rag.IngestDataIntoVectorStore;

namespace Samples.Rag;

public static class SearchAsATool
{
    public static async Task RunSample()
    {
        //Create Raw Connection
        string apiKey = SecretManager.GetOpenAIApiKey();
        OpenAIClient client = new OpenAIClient(apiKey);

        //Define Embedding Generator
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = client
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator();

        //Define Vector Store
        string connectionString = $"Data Source={Path.GetTempPath()}\\af-course-vector-store.db";
        VectorStore vectorStore = new Microsoft.SemanticKernel.Connectors.SqliteVec.SqliteVectorStore(connectionString, new SqliteVectorStoreOptions
        {
            EmbeddingGenerator = embeddingGenerator
        });

        //Get Vector Store Collection (so we can search against it)
        VectorStoreCollection<Guid, KnowledgeBaseVectorRecord> vectorStoreCollection = vectorStore.GetCollection<Guid, KnowledgeBaseVectorRecord>("knowledge_base");

        //Create out Search Tool
        SearchTool searchTool = new SearchTool(vectorStoreCollection);

        //Create Agent
        ChatClientAgent agent = client
            .GetChatClient("gpt-4.1-nano")
            .AsAIAgent(
                instructions: "You are an expert in the companies Internal Knowledge Base (use the 'search_knowledge' tool)",
                tools: [AIFunctionFactory.Create(searchTool.Search, "search_knowledge")]);

        AgentSession session = await agent.CreateSessionAsync();

        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine() ?? "";
            AgentResponse response = await agent.RunAsync(input, session);
            {
                Console.WriteLine(response);
            }

            Output.Separator();
        }
    }

    private class SearchTool(VectorStoreCollection<Guid, KnowledgeBaseVectorRecord> vectorStoreCollection)
    {
        public async Task<string> Search(string input)
        {
            StringBuilder mostSimilarKnowledge = new StringBuilder();
            await foreach (VectorSearchResult<KnowledgeBaseVectorRecord> searchResult in vectorStoreCollection.SearchAsync(input, 3))
            {
                string searchResultAsQAndA = $"Q: {searchResult.Record.Question} - A: {searchResult.Record.Answer}";
                Output.Gray($"- Search result [Score: {searchResult.Score}] {searchResultAsQAndA}");
                mostSimilarKnowledge.AppendLine(searchResultAsQAndA);
            }

            Console.WriteLine();

            return mostSimilarKnowledge.ToString();
        }
    }
}