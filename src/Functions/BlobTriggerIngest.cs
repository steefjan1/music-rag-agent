using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.OpenAI.Embeddings;
using Microsoft.Extensions.Logging;
using MusicRagAgent.Models;
using MusicRagAgent.Services;
using System.Text.Json;

namespace MusicRagAgent.Functions;

/// <summary>
/// Ingestion pipeline: blob trigger → embeddings → Azure AI Search.
///
/// Flow:
///   1. scripts/ingest.py scrapes SputnikMusic and uploads a JSON file
///      to the band-data/{artist_id}.json blob container.
///   2. This function fires, deserialises the JSON into a BandData record,
///      and creates one MusicDocument per album release.
///   3. The Azure OpenAI embeddings binding generates a vector for each
///      document's Content field.
///   4. The documents are uploaded to the Azure AI Search music-index.
///
/// The embeddings binding handles authentication to Azure OpenAI via
/// managed identity — no API key is needed in the function code.
/// </summary>
public class BlobTriggerIngest(
    SearchClient searchClient,
    SearchIndexService indexService,
    ILogger<BlobTriggerIngest> logger)
{
    [Function(nameof(BlobTriggerIngest))]
    public async Task Run(
        [BlobTrigger("band-data/{name}", Connection = "STORAGE_CONNECTION")]
        string blobContent,
        string name,
        [EmbeddingsStore(
            "{blobContent}",
            InputType.RawText,
            "AZURE_OPENAI_ENDPOINT",
            "%OPENAI_EMBEDDINGS_DEPLOYMENT%",
            MaxChunkLength = 2000,
            MaxOverlap = 200
        )] EmbeddingsStoreOutput embeddingsOutput,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Ingesting blob: {Name}", name);

        BandData? bandData;
        try
        {
            bandData = JsonSerializer.Deserialize<BandData>(blobContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialise blob {Name}.", name);
            return;
        }

        if (bandData is null)
        {
            logger.LogWarning("Blob {Name} deserialised to null — skipping.", name);
            return;
        }

        // Ensure the search index exists before writing to it.
        await indexService.EnsureIndexExistsAsync(cancellationToken);

        var documents = new List<MusicDocument>();

        foreach (var release in bandData.Releases)
        {
            // Build the content field — this is what gets embedded and searched.
            // Combine artist context with album-specific data for richer retrieval.
            var content = $"""
                Artist: {bandData.ArtistName}
                Genres: {string.Join(", ", bandData.Genres)}
                Similar artists: {string.Join(", ", bandData.Similar)}
                
                Album: {release.Title} ({release.Date})
                Rating: {release.Rating} ({release.Votes} votes)
                
                Artist description: {bandData.Description}
                """;

            var docId = $"{bandData.ArtistId}-{SanitiseId(release.Title)}";

            // Match the embedding from embeddingsOutput to this document.
            // EmbeddingsStoreOutput chunks the full blob content — find the chunk
            // whose text most closely corresponds to this album's content.
            var embedding = GetEmbeddingForContent(embeddingsOutput, release.Title);

            documents.Add(new MusicDocument
            {
                Id = docId,
                ArtistId = bandData.ArtistId,
                ArtistName = bandData.ArtistName,
                Genres = bandData.Genres,
                SimilarArtists = bandData.Similar,
                ArtistDescription = bandData.Description,
                AlbumTitle = release.Title,
                AlbumYear = release.Date,
                AlbumRating = release.Rating,
                AlbumVotes = release.Votes,
                Content = content,
                ContentVector = embedding
            });
        }

        if (documents.Count == 0)
        {
            logger.LogWarning("No releases found for {ArtistName} — nothing indexed.", bandData.ArtistName);
            return;
        }

        var batch = IndexDocumentsBatch.Upload(documents);
        var result = await searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken);

        var succeeded = result.Value.Results.Count(r => r.Succeeded);
        var failed = result.Value.Results.Count(r => !r.Succeeded);

        logger.LogInformation(
            "Indexed {ArtistName}: {Succeeded} documents succeeded, {Failed} failed.",
            bandData.ArtistName, succeeded, failed);
    }

    private static float[]? GetEmbeddingForContent(
        EmbeddingsStoreOutput embeddingsOutput,
        string albumTitle)
    {
        // EmbeddingsStoreOutput may contain multiple chunks if the blob is large.
        // Use the first embedding as a reasonable approximation — for production,
        // generate a separate embedding per album document using the Embeddings
        // input binding with per-invocation text rather than the store output.
        return embeddingsOutput?.Response?.Data?.FirstOrDefault()?.Embedding?.ToArray();
    }

    private static string SanitiseId(string input) =>
        new string(input
            .ToLowerInvariant()
            .Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .ToArray());
}
