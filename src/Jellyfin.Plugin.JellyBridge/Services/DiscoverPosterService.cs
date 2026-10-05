using System.Net.Http.Headers;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Downloads local Discover artwork at the production browse profile.
/// Poster: TMDB w780. Backdrop: TMDB w1280.
/// Logos and additional fanart are deliberately not downloaded.
/// </summary>
public sealed class DiscoverPosterService
{
    public const string PosterSize = "w780";
    public const string BackdropSize = "w1280";
    public const string AssetProfile = "poster-w780-backdrop-w1280-v1";
    public const string PosterFileName = "poster.jpg";
    public const string BackdropFileName = "backdrop.jpg";
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

        return BuildImageUrl(PosterSize, item.PosterPath);
    }

    public string BuildBackdropUrl(DiscoverCatalogItem item)
    {
        if (string.IsNullOrWhiteSpace(item.BackdropPath))
        {
            throw new InvalidOperationException(
                $"TMDB {item.TmdbId} has no backdropPath.");
        }

        return BuildImageUrl(BackdropSize, item.BackdropPath);
    }

    public Task<string> EnsurePosterAsync(
        DiscoverCatalogItem item,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        return EnsureImageAsync(
            item.TmdbId,
            BuildPosterUrl(item),
            targetDirectory,
            PosterFileName,
            "poster",
            PosterSize,
            cancellationToken);
    }

    public async Task<string?> EnsureBackdropAsync(
        DiscoverCatalogItem item,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.BackdropPath))
        {
            _logger.LogDebug(
                "No Discover backdrop available: TMDB={TmdbId}",
                item.TmdbId);
            return null;
        }

        return await EnsureImageAsync(
            item.TmdbId,
            BuildBackdropUrl(item),
            targetDirectory,
            BackdropFileName,
            "backdrop",
            BackdropSize,
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildImageUrl(string size, string imagePath)
    {
        var normalizedPath = imagePath.StartsWith(
            "/",
            StringComparison.Ordinal)
            ? imagePath
            : "/" + imagePath;

        return $"{TmdbImageBaseUrl}/{size}{normalizedPath}";
    }

    private async Task<string> EnsureImageAsync(
        long tmdbId,
        string url,
        string targetDirectory,
        string fileName,
        string imageKind,
        string imageSize,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        var finalPath = Path.Combine(targetDirectory, fileName);

        if (File.Exists(finalPath)
            && new FileInfo(finalPath).Length > 0)
        {
            return finalPath;
        }

        var tempPath = finalPath + ".tmp";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
                    $"TMDB {imageKind} response was not an image for {tmdbId}: {contentType}");
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
                await input.CopyToAsync(output, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!File.Exists(tempPath)
                || new FileInfo(tempPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"Downloaded {imageKind} is empty for TMDB {tmdbId}.");
            }

            File.Move(tempPath, finalPath, overwrite: true);

            _logger.LogDebug(
                "Stored Discover {ImageKind}: TMDB={TmdbId}, Size={ImageSize}, Path={Path}",
                imageKind,
                tmdbId,
                imageSize,
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