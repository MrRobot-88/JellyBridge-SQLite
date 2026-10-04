using System.Net.Http.Headers;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Downloads one compact local browse poster for a Discover item.
/// Deliberately does not download backdrops, logos or fanart.
/// </summary>
public sealed class DiscoverPosterService
{
    public const string PosterSize = "w342";
    public const string PosterFileName = "poster.jpg";
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p";

    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscoverPosterService> _logger;

    public DiscoverPosterService(
        HttpClient httpClient,
        ILogger<DiscoverPosterService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string BuildPosterUrl(DiscoverCatalogItem item)
    {
        if (string.IsNullOrWhiteSpace(item.PosterPath))
        {
            throw new InvalidOperationException(
                $"TMDB {item.TmdbId} has no posterPath.");
        }

        var posterPath = item.PosterPath.StartsWith(
            "/",
            StringComparison.Ordinal)
            ? item.PosterPath
            : "/" + item.PosterPath;

        return $"{TmdbImageBaseUrl}/{PosterSize}{posterPath}";
    }

    public async Task<string> EnsurePosterAsync(
        DiscoverCatalogItem item,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);

        var finalPath = Path.Combine(
            targetDirectory,
            PosterFileName);

        if (File.Exists(finalPath)
            && new FileInfo(finalPath).Length > 0)
        {
            return finalPath;
        }

        var tempPath = finalPath + ".tmp";

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildPosterUrl(item));

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("image/jpeg"));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentType =
                response.Content.Headers.ContentType?.MediaType
                ?? string.Empty;

            if (!contentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"TMDB poster response was not an image for {item.TmdbId}: {contentType}");
            }

            await using (var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false))
            await using (var output = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await input.CopyToAsync(
                    output,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(tempPath)
                || new FileInfo(tempPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"Downloaded poster is empty for TMDB {item.TmdbId}.");
            }

            File.Move(
                tempPath,
                finalPath,
                overwrite: true);

            _logger.LogDebug(
                "Stored compact Discover poster: TMDB={TmdbId}, Size={PosterSize}, Path={Path}",
                item.TmdbId,
                PosterSize,
                finalPath);

            return finalPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
