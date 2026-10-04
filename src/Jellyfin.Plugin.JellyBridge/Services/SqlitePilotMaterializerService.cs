using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Manual, one-item materialization pilot for the SQLite-first architecture.
/// It deliberately creates exactly one movie and never invokes the legacy
/// file-driven sync path.
/// </summary>
public sealed class SqlitePilotMaterializerService
{
    public const long PilotMovieTmdbId = 1482938; // Union County (2026)

    private readonly CatalogSelectionService _selection;
    private readonly DiscoverPosterService _posterService;
    private readonly PlaceholderVideoGenerator _placeholderVideoGenerator;
    private readonly BridgeStateStore _stateStore;
    private readonly ILogger<SqlitePilotMaterializerService> _logger;

    public SqlitePilotMaterializerService(
        CatalogSelectionService selection,
        DiscoverPosterService posterService,
        PlaceholderVideoGenerator placeholderVideoGenerator,
        BridgeStateStore stateStore,
        ILogger<SqlitePilotMaterializerService> logger)
    {
        _selection = selection;
        _posterService = posterService;
        _placeholderVideoGenerator = placeholderVideoGenerator;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<SqlitePilotMaterializationResult> RunMoviePilotAsync(
        CancellationToken cancellationToken = default)
    {
        var existing = await _stateStore
            .GetMaterializedItemsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count != 0)
        {
            throw new InvalidOperationException(
                $"One-movie pilot requires an empty materialized_items table; found {existing.Count} row(s).");
        }

        var item = await _selection
            .GetDesiredByTmdbIdAsync(
                "movie",
                PilotMovieTmdbId,
                cancellationToken)
            .ConfigureAwait(false);

        if (item.TmdbId != PilotMovieTmdbId)
        {
            throw new InvalidOperationException(
                $"One-movie pilot expected TMDB {PilotMovieTmdbId} but selected {item.TmdbId}.");
        }
        var targetDirectory = item.TargetPath;

        if (Directory.Exists(targetDirectory)
            && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Pilot target directory is not empty: {targetDirectory}");
        }

        var directoryExisted = Directory.Exists(targetDirectory);

        try
        {
            Directory.CreateDirectory(targetDirectory);

            var nfoPath = Path.Combine(targetDirectory, "movie.nfo");
            await WriteMovieNfoAsync(
                item.CatalogItem,
                nfoPath,
                cancellationToken).ConfigureAwait(false);

            var posterPath = await _posterService
                .EnsurePosterAsync(
                    item.CatalogItem,
                    targetDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            var placeholderOk = await _placeholderVideoGenerator
                .GeneratePlaceholderMovieAsync(targetDirectory)
                .ConfigureAwait(false);

            if (!placeholderOk)
            {
                throw new InvalidOperationException(
                    $"Placeholder video generation failed for TMDB {item.TmdbId}.");
            }

            var placeholderPaths = Directory
                .EnumerateFiles(
                    targetDirectory,
                    "*.mp4",
                    SearchOption.TopDirectoryOnly)
                .ToArray();

            if (placeholderPaths.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one movie placeholder MP4; found {placeholderPaths.Length}.");
            }

            if (!File.Exists(nfoPath)
                || new FileInfo(nfoPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"movie.nfo was not created for TMDB {item.TmdbId}.");
            }

            if (!File.Exists(posterPath)
                || new FileInfo(posterPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"poster.jpg was not created for TMDB {item.TmdbId}.");
            }

            if (File.Exists(Path.Combine(targetDirectory, "metadata.json")))
            {
                throw new InvalidOperationException(
                    "SQLite pilot unexpectedly created legacy metadata.json.");
            }

            var now = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);

            await _stateStore.UpsertMaterializedItemAsync(
                new MaterializedItemState(
                    item.MediaType,
                    item.TmdbId,
                    item.TargetPath,
                    item.Fingerprint,
                    "pilot-materialized",
                    1,
                    now,
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);

            var result = new SqlitePilotMaterializationResult(
                item.MediaType,
                item.TmdbId,
                item.CatalogItem.Title,
                item.CatalogItem.Year,
                targetDirectory,
                nfoPath,
                posterPath,
                placeholderPaths[0]);

            _logger.LogInformation(
                "SQLITE PILOT MATERIALIZED | Media={MediaType} | TMDB={TmdbId} | Year={Year} | Title={Title} | Path={Path} | NFO={NfoPath} | Poster={PosterPath} | Placeholder={PlaceholderPath}",
                result.MediaType,
                result.TmdbId,
                result.Year,
                result.Title,
                result.TargetPath,
                result.NfoPath,
                result.PosterPath,
                result.PlaceholderPath);

            return result;
        }
        catch
        {
            if (!directoryExisted && Directory.Exists(targetDirectory))
            {
                try
                {
                    Directory.Delete(targetDirectory, recursive: true);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogError(
                        cleanupException,
                        "Pilot rollback could not delete {TargetDirectory}",
                        targetDirectory);
                }
            }

            throw;
        }
    }

    private static async Task WriteMovieNfoAsync(
        DiscoverCatalogItem item,
        string nfoPath,
        CancellationToken cancellationToken)
    {
        var movie = new XElement(
            "movie",
            new XElement("id", item.TmdbId),
            new XElement(
                "uniqueid",
                new XAttribute("type", "tmdb"),
                new XAttribute("default", "true"),
                item.TmdbId),
            new XElement("tmdbid", item.TmdbId),
            new XElement("title", item.Title ?? string.Empty),
            new XElement("originaltitle", item.OriginalTitle ?? string.Empty),
            item.Year.HasValue
                ? new XElement("year", item.Year.Value)
                : null,
            !string.IsNullOrWhiteSpace(item.ReleaseDate)
                ? new XElement("premiered", item.ReleaseDate)
                : null,
            !string.IsNullOrWhiteSpace(item.Overview)
                ? new XElement("plot", item.Overview)
                : null,
            item.VoteAverage.HasValue
                ? new XElement(
                    "rating",
                    item.VoteAverage.Value.ToString(
                        "0.0###",
                        CultureInfo.InvariantCulture))
                : null,
            item.GenreNames
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Select(static genre => new XElement("genre", genre)),
            item.OriginCountries
                .Where(static country => !string.IsNullOrWhiteSpace(country))
                .Select(static country => new XElement("country", country)),
            new XElement("watched", "false"));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            movie);

        var tempPath = nfoPath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                document.ToString(),
                cancellationToken).ConfigureAwait(false);

            if (new FileInfo(tempPath).Length == 0)
            {
                throw new InvalidOperationException("Generated movie.nfo is empty.");
            }

            File.Move(tempPath, nfoPath, overwrite: true);
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

public sealed record SqlitePilotMaterializationResult(
    string MediaType,
    long TmdbId,
    string Title,
    int? Year,
    string TargetPath,
    string NfoPath,
    string PosterPath,
    string PlaceholderPath);
