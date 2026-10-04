using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Jellyfin.Plugin.JellyBridge.Utils;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services.Catalog;

/// <summary>
/// Builds the desired Jellyfin working set from the UI ARR catalog.
/// This is selection logic only; Jellyfin remains responsible for user-facing
/// filtering, sorting, genre/year/rating UI and search.
/// </summary>
public sealed class CatalogSelectionService
{
    private static readonly HashSet<string> IndianLanguages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "hi", "ta", "te", "ml", "kn", "bn", "mr", "gu", "pa"
        };

    private readonly DiscoverCatalogClient _catalog;
    private readonly ILogger<CatalogSelectionService> _logger;

    public CatalogSelectionService(
        DiscoverCatalogClient catalog,
        ILogger<CatalogSelectionService> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DesiredCatalogItem>> GetDesiredAsync(
        string mediaType,
        int targetCount,
        CancellationToken cancellationToken = default)
    {
        if (targetCount <= 0)
        {
            return Array.Empty<DesiredCatalogItem>();
        }

        var minimumYear = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverMinimumYear));

        var excludeIndia = Plugin.GetConfigOrDefault<bool>(
            nameof(PluginConfiguration.DiscoverExcludeIndia));

        var result = new List<DesiredCatalogItem>(targetCount);
        var seen = new HashSet<long>();

        var offset = 0;
        var total = int.MaxValue;

        while (result.Count < targetCount && offset < total)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _catalog.GetPageAsync(
                mediaType,
                offset,
                DiscoverCatalogClient.MaximumPageSize,
                cancellationToken).ConfigureAwait(false);

            total = page.Total;

            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (var item in page.Items)
            {
                if (result.Count >= targetCount)
                {
                    break;
                }

                if (item.TmdbId <= 0 || !seen.Add(item.TmdbId))
                {
                    continue;
                }

                if (!item.Year.HasValue || item.Year.Value < minimumYear)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.PosterPath))
                {
                    continue;
                }

                if (item.Adult || IsFutureRelease(item.ReleaseDate))
                {
                    continue;
                }

                if (excludeIndia && IsIndian(item))
                {
                    continue;
                }

                var normalizedMediaType =
                    string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
                        ? "tv"
                        : "movie";

                var targetPath = BuildTargetPath(
                    normalizedMediaType,
                    item);

                var fingerprint = BuildFingerprint(
                    normalizedMediaType,
                    item);

                result.Add(
                    new DesiredCatalogItem(
                        normalizedMediaType,
                        item.TmdbId,
                        targetPath,
                        fingerprint,
                        item));
            }

            offset += page.Items.Count;
        }

        _logger.LogInformation(
            "Catalog desired set built: media={MediaType}, target={Target}, selected={Selected}, scanned={Scanned}, catalogTotal={Total}",
            mediaType,
            targetCount,
            result.Count,
            offset,
            total);

        if (result.Count < targetCount)
        {
            throw new InvalidOperationException(
                $"Discover Catalog produced only {result.Count} eligible {mediaType} items for target {targetCount}.");
        }

        return result;
    }


    /// <summary>
    /// Finds one exact TMDB item in the ranked Discover Catalog and applies
    /// the same eligibility policy used by the normal desired-set builder.
    /// Intended for controlled diagnostics/pilots where rank must not choose
    /// a different title.
    /// </summary>
    public async Task<DesiredCatalogItem> GetDesiredByTmdbIdAsync(
        string mediaType,
        long tmdbId,
        CancellationToken cancellationToken = default)
    {
        if (tmdbId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tmdbId),
                "TMDB id must be greater than zero.");
        }

        var minimumYear = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverMinimumYear));

        var excludeIndia = Plugin.GetConfigOrDefault<bool>(
            nameof(PluginConfiguration.DiscoverExcludeIndia));

        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _catalog.GetPageAsync(
                mediaType,
                offset,
                DiscoverCatalogClient.MaximumPageSize,
                cancellationToken).ConfigureAwait(false);

            total = page.Total;

            if (page.Items.Count == 0)
            {
                break;
            }

            var item = page.Items.FirstOrDefault(
                candidate => candidate.TmdbId == tmdbId);

            if (item is not null)
            {
                if (!item.Year.HasValue || item.Year.Value < minimumYear)
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} was found but is excluded by minimum year {minimumYear}.");
                }

                if (string.IsNullOrWhiteSpace(item.PosterPath))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} was found but has no posterPath.");
                }

                if (item.Adult)
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} was found but is marked adult.");
                }

                if (IsFutureRelease(item.ReleaseDate))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} was found but releaseDate {item.ReleaseDate} is still in the future.");
                }

                if (excludeIndia && IsIndian(item))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} was found but is excluded by the India filter.");
                }

                var normalizedMediaType =
                    string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
                        ? "tv"
                        : "movie";

                var desired = new DesiredCatalogItem(
                    normalizedMediaType,
                    item.TmdbId,
                    BuildTargetPath(normalizedMediaType, item),
                    BuildFingerprint(normalizedMediaType, item),
                    item);

                _logger.LogInformation(
                    "Catalog exact item selected: media={MediaType}, tmdb={TmdbId}, rank={Rank}, title={Title}, scannedThrough={ScannedThrough}, catalogTotal={Total}",
                    mediaType,
                    tmdbId,
                    item.Rank,
                    item.Title,
                    offset + page.Items.Count,
                    total);

                return desired;
            }

            offset += page.Items.Count;
        }

        throw new InvalidOperationException(
            $"TMDB {tmdbId} was not found in the Discover Catalog {mediaType} feed.");
    }

    /// <summary>
    /// Resolves one exact catalog item by TMDB id while applying the same
    /// eligibility rules as the ranked working-set selector. Intended for
    /// controlled/manual pilots, not bulk synchronization.
    /// </summary>
    public async Task<DesiredCatalogItem> GetDesiredByTmdbIdAsync(
        string mediaType,
        long tmdbId,
        CancellationToken cancellationToken = default)
    {
        if (tmdbId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tmdbId));
        }

        var minimumYear = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverMinimumYear));

        var excludeIndia = Plugin.GetConfigOrDefault<bool>(
            nameof(PluginConfiguration.DiscoverExcludeIndia));

        var offset = 0;
        var total = int.MaxValue;

        while (offset < total)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _catalog.GetPageAsync(
                mediaType,
                offset,
                DiscoverCatalogClient.MaximumPageSize,
                cancellationToken).ConfigureAwait(false);

            total = page.Total;

            if (page.Items.Count == 0)
            {
                break;
            }

            var item = page.Items.FirstOrDefault(
                candidate => candidate.TmdbId == tmdbId);

            if (item is not null)
            {
                if (!item.Year.HasValue || item.Year.Value < minimumYear)
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} is outside the configured minimum year.");
                }

                if (string.IsNullOrWhiteSpace(item.PosterPath))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} has no posterPath.");
                }

                if (item.Adult)
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} is marked adult and is not eligible.");
                }

                if (IsFutureRelease(item.ReleaseDate))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} has a future release date {item.ReleaseDate}.");
                }

                if (excludeIndia && IsIndian(item))
                {
                    throw new InvalidOperationException(
                        $"TMDB {tmdbId} is excluded by the India filter.");
                }

                var normalizedMediaType =
                    string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
                        ? "tv"
                        : "movie";

                var desired = new DesiredCatalogItem(
                    normalizedMediaType,
                    item.TmdbId,
                    BuildTargetPath(normalizedMediaType, item),
                    BuildFingerprint(normalizedMediaType, item),
                    item);

                _logger.LogInformation(
                    "Exact catalog item selected: media={MediaType}, tmdb={TmdbId}, rank={Rank}, title={Title}, scannedThroughOffset={Offset}",
                    normalizedMediaType,
                    item.TmdbId,
                    item.Rank,
                    item.Title,
                    offset);

                return desired;
            }

            offset += page.Items.Count;
        }

        throw new InvalidOperationException(
            $"TMDB {tmdbId} was not found in the Discover Catalog.");
    }

    private static bool IsFutureRelease(string releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
        {
            return false;
        }

        if (!DateOnly.TryParseExact(
                releaseDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        return parsed > DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static bool IsIndian(DiscoverCatalogItem item)
    {
        if (item.OriginCountries.Any(
                country => string.Equals(
                    country,
                    "IN",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.OriginalLanguage)
            && IndianLanguages.Contains(item.OriginalLanguage);
    }

    private static string BuildTargetPath(
        string mediaType,
        DiscoverCatalogItem item)
    {
        var baseDirectory = FolderUtils.GetBaseDirectory();

        var mediaDirectory = mediaType == "tv"
            ? "Shows"
            : "Movies";

        var safeTitle = FolderUtils.SanitizeFileName(
            string.IsNullOrWhiteSpace(item.Title)
                ? $"TMDB {item.TmdbId}"
                : item.Title);

        var year = item.Year?.ToString(CultureInfo.InvariantCulture)
            ?? "Unknown";

        var folderName =
            $"{safeTitle} ({year}) [tmdbid-{item.TmdbId}]";

        return Path.Combine(
            baseDirectory,
            mediaDirectory,
            folderName);
    }

    private static string BuildFingerprint(
        string mediaType,
        DiscoverCatalogItem item)
    {
        var canonical = new
        {
            mediaType,
            item.TmdbId,
            item.Title,
            item.OriginalTitle,
            item.ReleaseDate,
            item.Year,
            item.Overview,
            item.PosterPath,
            item.BackdropPath,
            item.VoteAverage,
            item.VoteCount,
            item.GenreIds,
            item.GenreNames,
            item.OriginalLanguage,
            item.OriginCountries,
            item.Adult
        };

        var json = JsonSerializer.Serialize(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));

        return Convert.ToHexString(bytes);
    }
}

public sealed record DesiredCatalogItem(
    string MediaType,
    long TmdbId,
    string TargetPath,
    string Fingerprint,
    DiscoverCatalogItem CatalogItem)
{
    public DesiredBridgeItem ToPlannerItem() =>
        new(MediaType, TmdbId, TargetPath, Fingerprint);
}
