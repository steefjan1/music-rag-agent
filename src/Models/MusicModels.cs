using System.Text.Json.Serialization;

namespace MusicRagAgent.Models;

/// <summary>
/// Represents a band/artist scraped from SputnikMusic.
/// Matches the JSON output of scripts/ingest.py.
/// </summary>
public record BandData(
    [property: JsonPropertyName("artist_id")] string ArtistId,
    [property: JsonPropertyName("artist_name")] string ArtistName,
    [property: JsonPropertyName("genres")] List<string> Genres,
    [property: JsonPropertyName("similar")] List<string> Similar,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("releases")] List<Release> Releases
);

public record Release(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("rating")] string Rating,
    [property: JsonPropertyName("votes")] string Votes
);

/// <summary>
/// A document indexed into Azure AI Search.
/// One document per album — the artist description and genres are
/// repeated on each document so retrieval works for both
/// artist-level and album-level queries.
/// </summary>
public record MusicDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("artist_id")]
    public string ArtistId { get; init; } = string.Empty;

    [JsonPropertyName("artist_name")]
    public string ArtistName { get; init; } = string.Empty;

    [JsonPropertyName("genres")]
    public List<string> Genres { get; init; } = [];

    [JsonPropertyName("similar_artists")]
    public List<string> SimilarArtists { get; init; } = [];

    [JsonPropertyName("artist_description")]
    public string ArtistDescription { get; init; } = string.Empty;

    [JsonPropertyName("album_title")]
    public string AlbumTitle { get; init; } = string.Empty;

    [JsonPropertyName("album_year")]
    public string AlbumYear { get; init; } = string.Empty;

    [JsonPropertyName("album_rating")]
    public string AlbumRating { get; init; } = string.Empty;

    [JsonPropertyName("album_votes")]
    public string AlbumVotes { get; init; } = string.Empty;

    /// <summary>
    /// The text field that is embedded and used for vector search.
    /// Combines all relevant textual content for a single album.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// The vector embedding of the Content field.
    /// Populated by the embeddings binding in BlobTriggerIngest.
    /// </summary>
    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; init; }
}

/// <summary>
/// HTTP request body for the chat agent endpoint.
/// </summary>
public record ChatRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("session_id")] string? SessionId = null
);

/// <summary>
/// HTTP response from the chat agent endpoint.
/// </summary>
public record ChatResponse(
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("sources")] List<string> Sources,
    [property: JsonPropertyName("session_id")] string SessionId
);
