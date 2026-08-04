using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using MusicRagAgent.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MusicRagAgent.Functions;

/// <summary>
/// RAG chat agent: HTTP trigger → hybrid search (keyword + vector) → Azure OpenAI.
///
/// Flow:
///   1. Accept a user message via HTTP POST /api/chat.
///   2. Generate a query embedding using Azure OpenAI.
///   3. Perform hybrid search (keyword + vector) against the music-index.
///      Hybrid search surfaces semantically relevant documents regardless
///      of term frequency — critical for artist-specific queries where
///      keyword search alone returns documents from other artists.
///   4. Collect top passages, build augmented prompt, call Azure OpenAI.
///   5. Return grounded answer and source references.
/// </summary>
public class MusicChatAgent(
    SearchClient searchClient,
    ILogger<MusicChatAgent> logger)
{
    private const int MaxResults = 20;

    private static readonly string OpenAiEndpoint =
        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required.");

    private static readonly string EmbeddingsDeployment =
        Environment.GetEnvironmentVariable("OPENAI_EMBEDDINGS_DEPLOYMENT") ?? "text-embedding-ada-002";

    private static readonly string ChatDeployment =
        Environment.GetEnvironmentVariable("OPENAI_CHAT_DEPLOYMENT") ?? "gpt-4o";

    private const string SystemPrompt = """
        You are a music expert assistant specialising in album reviews and discographies.
        Answer questions about bands, albums, and ratings using ONLY the context provided below.
        If the context does not contain enough information to answer, say so clearly.
        Always cite the album rating and vote count when discussing specific albums.
        Be concise but informative. Format album listings as bullet points.
        """;

    [Function(nameof(MusicChatAgent))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "chat")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        ChatRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ChatRequest>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialise chat request.");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body.");
            return badRequest;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("'message' is required.");
            return badRequest;
        }

        logger.LogInformation("Chat request: {Message}", request.Message);

        var openAiClient = new AzureOpenAIClient(
            new Uri(OpenAiEndpoint),
            new Azure.Identity.DefaultAzureCredential());

        // Step 1: Generate query embedding for vector search component.
        var embeddingsClient = openAiClient.GetEmbeddingClient(EmbeddingsDeployment);
        var queryEmbedding = await embeddingsClient.GenerateEmbeddingAsync(
            request.Message, cancellationToken: cancellationToken);
        var queryVector = queryEmbedding.Value.ToFloats();

        // Step 2: Hybrid search — keyword + vector.
        // VectorQueries searches the content_vector field for semantic similarity.
        // The keyword component (request.Message) handles exact term matching.
        // Together they surface relevant documents even when the query terms
        // don't appear verbatim in the indexed content.
        var vectorQuery = new VectorizedQuery(queryVector)
        {
            KNearestNeighborsCount = MaxResults,
            Fields = { "content_vector" }
        };

        var searchOptions = new SearchOptions
        {
            Size = MaxResults,
            Select = { "artist_name", "album_title", "album_year", "album_rating", "album_votes", "content" },
            VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } }
        };

        var searchResults = await searchClient.SearchAsync<MusicDocument>(
            request.Message, searchOptions, cancellationToken);

        var passages = new List<(MusicDocument Doc, double? Score)>();
        await foreach (var searchResult in searchResults.Value.GetResultsAsync())
        {
            passages.Add((searchResult.Document, searchResult.Score));
        }

        logger.LogInformation("Retrieved {Count} passages for query.", passages.Count);

        if (passages.Count == 0)
        {
            var notFound = req.CreateResponse(HttpStatusCode.OK);
            await notFound.WriteAsJsonAsync(new ChatResponse(
                Answer: "I don't have information about that in my music database. Try ingesting the artist first using scripts/ingest.py.",
                Sources: [],
                SessionId: request.SessionId ?? Guid.NewGuid().ToString()));
            return notFound;
        }

        // Step 3: Build augmented prompt with retrieved context.
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("## Retrieved context");
        contextBuilder.AppendLine();

        var sources = new List<string>();
        foreach (var (doc, score) in passages)
        {
            contextBuilder.AppendLine($"**{doc.ArtistName} — {doc.AlbumTitle}** ({doc.AlbumYear})");
            contextBuilder.AppendLine($"Rating: {doc.AlbumRating}/5.0 ({doc.AlbumVotes} votes)");
            contextBuilder.AppendLine(doc.Content);
            contextBuilder.AppendLine();
            sources.Add($"{doc.ArtistName} — {doc.AlbumTitle} ({doc.AlbumRating}/5.0)");
        }

        // Step 4: Call Azure OpenAI with augmented prompt.
        var chatClient = openAiClient.GetChatClient(ChatDeployment);
        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            OpenAI.Chat.ChatMessage.CreateSystemMessage($"{SystemPrompt}\n\n{contextBuilder}"),
            OpenAI.Chat.ChatMessage.CreateUserMessage(request.Message)
        };

        var result = await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var answer = result.Value.Content[0].Text;

        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new ChatResponse(answer, sources, sessionId));
        return response;
    }
}
