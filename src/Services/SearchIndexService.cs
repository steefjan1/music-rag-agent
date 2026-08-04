using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;

namespace MusicRagAgent.Services;

/// <summary>
/// Ensures the Azure AI Search index exists with the correct schema.
/// Called on first ingestion; subsequent calls are no-ops.
/// </summary>
public class SearchIndexService(SearchIndexClient indexClient, ILogger<SearchIndexService> logger)
{
    private const string IndexName = "music-index";

    public async Task EnsureIndexExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await indexClient.GetIndexAsync(IndexName, cancellationToken);
            logger.LogInformation("Search index '{IndexName}' already exists.", IndexName);
            return;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            logger.LogInformation("Creating search index '{IndexName}'.", IndexName);
        }

        // Vector search configuration — using HNSW algorithm.
        // The dimensions must match the embedding model output.
        // text-embedding-ada-002 = 1536 dimensions.
        // text-embedding-3-small = 1536 dimensions.
        // text-embedding-3-large = 3072 dimensions.
        // Update EMBEDDING_DIMENSIONS env var if you change the model.
        var embeddingDimensions = int.Parse(
            Environment.GetEnvironmentVariable("EMBEDDING_DIMENSIONS") ?? "1536");

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("hnsw-config"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("vector-profile", "hnsw-config"));

        var index = new SearchIndex(IndexName)
        {
            VectorSearch = vectorSearch,
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("artist_id", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("artist_name") { IsFilterable = true, IsSortable = true },
                new SearchableField("genres", collection: true) { IsFilterable = true },
                new SearchableField("similar_artists", collection: true),
                new SearchableField("artist_description"),
                new SearchableField("album_title") { IsFilterable = true, IsSortable = true },
                new SimpleField("album_year", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                new SimpleField("album_rating", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                new SimpleField("album_votes", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("content"),
                new VectorSearchField("content_vector", embeddingDimensions, "vector-profile"),
            }
        };

        await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
        logger.LogInformation("Search index '{IndexName}' created.", IndexName);
    }
}
