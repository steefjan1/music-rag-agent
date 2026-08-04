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
/// RAG chat agent: HTTP trigger → Azure AI Search retrieval → Azure OpenAI.
///
/// Flow:
///   1. Accept a user message via HTTP POST /api/chat.
///   2. Generate a query embedding and perform hybrid search
///      (keyword + vector) against the music-index.
///   3. Collect the top passages and format them as context.
///   4. Call Azure OpenAI with the augmented prompt via the
///      chat completion binding (handled in ChatCompletion function).
///   5. Return the grounded answer and source references.
///
/// The retrieval and LLM call are split across two functions so the
/// chat completion binding can be used declaratively. The retrieval
/// step runs first, writes context to a blob, and then the completion
/// function picks it up — or for simplicity in this sample, the
/// context is passed inline via a custom prompt.
/// </summary>
public class MusicChatAgent(
    SearchClient searchClient,
    ILogger<MusicChatAgent> logger)
{
    private const int MaxResults = 5;
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

        // Step 1: Retrieve relevant passages from Azure AI Search.
        // Uses keyword search — for vector/hybrid search, generate an embedding
        // for the query first and use SearchOptions with VectorQueries.
        var searchOptions = new SearchOptions
        {
            Size = MaxResults,
            Select = { "artist_name", "album_title", "album_year", "album_rating", "album_votes", "content" },
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "music-semantic-config",
                QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
            }
        };

        var searchResults = await searchClient.SearchAsync<MusicDocument>(
            request.Message, searchOptions, cancellationToken);

        var passages = new List<(MusicDocument Doc, double? Score)>();
        await foreach (var result in searchResults.Value.GetResultsAsync())
        {
            passages.Add((result.Document, result.Score));
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

        // Step 2: Build the augmented prompt with retrieved context.
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

        // Step 3: Call Azure OpenAI with the augmented prompt.
        // In production, use the chat completion binding for managed identity
        // and session state. This sample uses the Azure OpenAI SDK directly
        // for clarity — see host.json and BlobTriggerIngest for the binding approach.
        var answer = await CallOpenAiAsync(
            SystemPrompt,
            contextBuilder.ToString(),
            request.Message,
            cancellationToken);

        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteAsJsonAsync(new ChatResponse(answer, sources, sessionId));

        return response;
    }

    private async Task<string> CallOpenAiAsync(
        string systemPrompt,
        string context,
        string userMessage,
        CancellationToken cancellationToken)
    {
        // Direct Azure OpenAI SDK call — replace with the chat completion
        // binding for production use (handles session state, managed identity,
        // and retry automatically).
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required.");

        var deployment = Environment.GetEnvironmentVariable("OPENAI_CHAT_DEPLOYMENT")
            ?? "gpt-4o";

        var client = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(endpoint),
            new Azure.Identity.DefaultAzureCredential());

        var chat = client.GetChatClient(deployment);

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new OpenAI.Chat.SystemChatMessage($"{systemPrompt}\n\n{context}"),
            new OpenAI.Chat.UserChatMessage(userMessage)
        };

        var result = await chat.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return result.Value.Content[0].Text;
    }
}
