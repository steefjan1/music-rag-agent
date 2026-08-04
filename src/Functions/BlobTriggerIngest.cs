using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.AI.OpenAI;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using MusicRagAgent.Models;
using MusicRagAgent.Services;
using System.Text;
using System.Text.Json;

namespace MusicRagAgent.Functions;

public class BlobTriggerIngest(
    SearchClient searchClient,
    SearchIndexService indexService,
    ILogger<BlobTriggerIngest> logger)
{
    private static readonly string OpenAiEndpoint =
        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is required.");

    private static readonly string EmbeddingsDeployment =
        Environment.GetEnvironmentVariable("OPENAI_EMBEDDINGS_DEPLOYMENT") ?? "text-embedding-ada-002";

    private static readonly string StorageAccount =
        Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT_NAME")
        ?? throw new InvalidOperationException("AZURE_STORAGE_ACCOUNT_NAME is required.");

    [Function(nameof(BlobTriggerIngest))]
    public async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("EventGrid event: {EventType} {Subject}",
            eventGridEvent.EventType, eventGridEvent.Subject);

        if (eventGridEvent.EventType != "Microsoft.Storage.BlobCreated")
            return;

        var subject = eventGridEvent.Subject;
        var blobName = subject.Split("/blobs/").LastOrDefault();
        if (string.IsNullOrEmpty(blobName)) return;

        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{StorageAccount}.blob.core.windows.net"),
            new Azure.Identity.DefaultAzureCredential());

        var blobClient = blobServiceClient
            .GetBlobContainerClient("band-data")
            .GetBlobClient(blobName);

        var download = await blobClient.DownloadContentAsync(cancellationToken);
        var blobContent = download.Value.Content.ToString();

        BandData? bandData;
        try
        {
            bandData = JsonSerializer.Deserialize<BandData>(blobContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialise blob {Name}.", blobName);
            return;
        }

        if (bandData is null || bandData.Releases.Count == 0)
        {
            logger.LogWarning("Blob {Name} has no releases — skipping.", blobName);
            return;
        }

        await indexService.EnsureIndexExistsAsync(cancellationToken);

        var openAiClient = new AzureOpenAIClient(
            new Uri(OpenAiEndpoint),
            new Azure.Identity.DefaultAzureCredential());
        var embeddingsClient = openAiClient.GetEmbeddingClient(EmbeddingsDeployment);

        var documents = new List<MusicDocument>();

        foreach (var release in bandData.Releases)
        {
            var content = new StringBuilder();
            content.AppendLine($"Artist: {bandData.ArtistName}");
            content.AppendLine($"Genres: {string.Join(", ", bandData.Genres)}");
            content.AppendLine($"Similar artists: {string.Join(", ", bandData.Similar)}");
            content.AppendLine($"Album: {release.Title} ({release.Date})");
            content.AppendLine($"Rating: {release.Rating}/5.0 ({release.Votes} votes)");
            content.AppendLine($"Description: {bandData.Description}");

            var embeddingResult = await embeddingsClient.GenerateEmbeddingAsync(
                content.ToString(), cancellationToken: cancellationToken);

            documents.Add(new MusicDocument
            {
                Id = SafeKey(bandData.ArtistId, release.Title),
                ArtistId = bandData.ArtistId,
                ArtistName = bandData.ArtistName,
                Genres = bandData.Genres,
                SimilarArtists = bandData.Similar,
                ArtistDescription = bandData.Description,
                AlbumTitle = release.Title,
                AlbumYear = release.Date,
                AlbumRating = release.Rating,
                AlbumVotes = release.Votes,
                Content = content.ToString(),
                ContentVector = embeddingResult.Value.ToFloats().ToArray()
            });
        }

        var batch = IndexDocumentsBatch.Upload(documents);
        var result = await searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);
        var succeeded = result.Value.Results.Count(r => r.Succeeded);
        logger.LogInformation("Indexed {ArtistName}: {Succeeded}/{Total} documents.",
            bandData.ArtistName, succeeded, documents.Count);
    }

    /// <summary>
    /// Produces a URL-safe Base64 key: artistId + "-" + base64(albumTitle).
    /// Azure AI Search keys allow letters, digits, underscore, dash, and equals.
    /// Base64 uses +, /, = — replace + with -, / with _, keep =.
    /// </summary>
    private static string SafeKey(string artistId, string albumTitle)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(albumTitle))
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{artistId}-{encoded}";
    }
}