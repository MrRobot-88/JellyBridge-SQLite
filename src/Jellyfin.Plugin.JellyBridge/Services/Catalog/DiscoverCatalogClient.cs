using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyBridge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services.Catalog;

/// <summary>
/// Read-only client for the UI ARR Discover Catalog.
/// The catalog service owns discover.db; JellyBridge-SQLite never opens the
/// master catalog database directly.
/// </summary>
public sealed class DiscoverCatalogClient
{
    public const int MaximumPageSize = 200;

    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscoverCatalogClient> _logger;

    public DiscoverCatalogClient(
        HttpClient httpClient,
        ILogger<DiscoverCatalogClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<DiscoverCatalogPage> GetPageAsync(
        string mediaType,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaximumPageSize);

        var baseUrl = Plugin.GetConfigOrDefault<string>(
            nameof(PluginConfiguration.DiscoverCatalogUrl));

        var path = string.Equals(
            mediaType,
            "tv",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                mediaType,
                "series",
                StringComparison.OrdinalIgnoreCase)
            ? "/catalog/series"
            : "/catalog/movies";

        var url =
            $"{baseUrl.TrimEnd('/')}{path}?sort=rank&offset={offset}&limit={limit}";

        using var response = await _httpClient.GetAsync(
            url,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = await JsonSerializer.DeserializeAsync<DiscoverCatalogPage>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (page is null)
        {
            throw new InvalidOperationException(
                $"Discover Catalog returned an empty response for {mediaType}.");
        }

        _logger.LogDebug(
            "Discover Catalog page: media={MediaType}, offset={Offset}, returned={Returned}, total={Total}",
            mediaType,
            page.Offset,
            page.Returned,
            page.Total);

        return page;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed class DiscoverCatalogPage
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("returned")]
    public int Returned { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<DiscoverCatalogItem> Items { get; set; } = new();
}

public sealed class DiscoverCatalogItem
{
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("tmdbId")]
    public long TmdbId { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")]
    public string OriginalTitle { get; set; } = string.Empty;

    [JsonPropertyName("releaseDate")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = string.Empty;

    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdropPath")]
    public string? BackdropPath { get; set; }

    [JsonPropertyName("voteAverage")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("voteCount")]
    public int VoteCount { get; set; }

    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    // UI ARR Discover Catalog v0.4.3 metadata contract.
    [JsonPropertyName("genreIds")]
    public List<int> GenreIds { get; set; } = new();

    [JsonPropertyName("genreNames")]
    public List<string> GenreNames { get; set; } = new();

    [JsonPropertyName("originalLanguage")]
    public string? OriginalLanguage { get; set; }

    [JsonPropertyName("originCountries")]
    public List<string> OriginCountries { get; set; } = new();

    [JsonPropertyName("adult")]
    public bool Adult { get; set; }
}
