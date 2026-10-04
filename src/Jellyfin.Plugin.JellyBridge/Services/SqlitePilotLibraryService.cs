using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Controlled one-item Jellyfin library pilot for the SQLite-first path.
/// JellyBridge-SQLite owns the NFO/poster/placeholder files; Jellyfin is configured
/// to read those local files without writing metadata back to the materialization tree.
/// New materialized paths are reported directly through ILibraryMonitor so no global
/// library scan and no broad QueueRefresh sequence is required.
/// </summary>
public sealed class SqlitePilotLibraryService
{
    public const string PilotLibraryName = "Discover Movies";
    public const long PilotMovieTmdbId = 1482938;

    private static readonly TimeSpan PilotRefreshTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PilotPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly JellyfinILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<SqlitePilotLibraryService> _logger;

    public SqlitePilotLibraryService(
        JellyfinILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<SqlitePilotLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    public async Task<SqlitePilotLibraryResult> EnsureLibraryAndScanAsync(
        CancellationToken cancellationToken = default)
    {
        var moviesPath = Path.Combine(
            FolderUtils.GetBaseDirectory(),
            "Movies");

        if (!Directory.Exists(moviesPath))
        {
            throw new DirectoryNotFoundException(
                $"Pilot Movies directory does not exist: {moviesPath}");
        }

        var materializedFolders = Directory
            .GetDirectories(moviesPath, "*", SearchOption.TopDirectoryOnly);

        if (materializedFolders.Length != 1)
        {
            throw new InvalidOperationException(
                $"Pilot library refresh requires exactly one materialized movie folder; found {materializedFolders.Length}.");
        }

        var materializedMoviePath = Path.GetFullPath(materializedFolders[0]);

        if (!Path.GetFileName(materializedMoviePath)
            .Contains(
                $"tmdbid-{PilotMovieTmdbId}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The one materialized movie is not pilot TMDB {PilotMovieTmdbId}: {materializedMoviePath}");
        }

        var virtualFolders = _libraryManager.Inner
            .GetVirtualFolders(true)
            .ToList();

        if (virtualFolders.Any(
            folder => string.Equals(
                folder.Name,
                "Discover",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Unexpected parent library named 'Discover' exists. The SQLite design requires separate root libraries.");
        }

        var matches = virtualFolders
            .Where(
                folder => string.Equals(
                    folder.Name,
                    PilotLibraryName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one existing '{PilotLibraryName}' library; found {matches.Count}.");
        }

        var discoverMovies = matches[0];

        var locations = discoverMovies.Locations?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToList()
            ?? new List<string>();

        var expectedMoviesPath = Path.GetFullPath(moviesPath);

        if (locations.Count != 1
            || !string.Equals(
                locations[0],
                expectedMoviesPath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{PilotLibraryName}' must contain exactly '{expectedMoviesPath}'. Actual: {string.Join(", ", locations)}");
        }

        if (string.IsNullOrWhiteSpace(discoverMovies.ItemId)
            || !Guid.TryParse(discoverMovies.ItemId, out var libraryItemId))
        {
            throw new InvalidOperationException(
                $"'{PilotLibraryName}' does not have a valid Jellyfin ItemId.");
        }

        var libraryFolder = _libraryManager.Inner
            .GetItemById(libraryItemId) as CollectionFolder
            ?? throw new InvalidOperationException(
                $"Could not resolve '{PilotLibraryName}' as a Jellyfin CollectionFolder.");

        // JellyBridge-SQLite owns the local metadata files. Jellyfin may read them,
        // but must never write NFO metadata back into the materialization tree.
        // Keeping MetadataSavers as an explicit empty array is important: on Jellyfin
        // 12, ReplaceAllMetadata can otherwise suppress local NFO readers when an NFO
        // saver is considered available.
        var libraryOptions = libraryFolder.GetLibraryOptions();
        libraryOptions.EnableRealtimeMonitor = false;
        libraryOptions.SaveLocalMetadata = false;
        libraryOptions.MetadataSavers = Array.Empty<string>();
        libraryOptions.DisabledLocalMetadataReaders = Array.Empty<string>();
        libraryOptions.LocalMetadataReaderOrder = ["Nfo"];

        var typeOptions = libraryOptions.TypeOptions?
            .Where(
                option => !string.Equals(
                    option.Type,
                    "Movie",
                    StringComparison.OrdinalIgnoreCase))
            .ToList()
            ?? new List<TypeOptions>();

        typeOptions.Add(
            new TypeOptions
            {
                Type = "Movie",
                MetadataFetchers = Array.Empty<string>(),
                MetadataFetcherOrder = Array.Empty<string>(),
                ImageFetchers = Array.Empty<string>(),
                ImageFetcherOrder = Array.Empty<string>()
            });

        libraryOptions.TypeOptions = typeOptions.ToArray();
        libraryFolder.UpdateLibraryOptions(libraryOptions);

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY OPTIONS | Library={Library} | RemoteMetadata=OFF | RemoteImages=OFF | LocalNfo=ON | MetadataSavers=OFF | LocalImages=ON | RealtimeMonitor=OFF",
            PilotLibraryName);

        var indexedBefore = _libraryManager
            .GetExistingItems<JellyfinMovie>(
                includePaths: new HashSet<string>(
                    [expectedMoviesPath],
                    StringComparer.Ordinal));

        _logger.LogInformation(
            "SQLITE PILOT INDEX BASELINE | Library={Library} | IndexedMovies={IndexedMovies} | MaterializedFolders=1 | TMDB={TmdbId}",
            PilotLibraryName,
            indexedBefore.Count,
            PilotMovieTmdbId);

        // This is the same Jellyfin-internal path reached by POST /Library/Media/Updated,
        // but called directly from the plugin. It reports exactly the new materialized
        // movie directory and lets Jellyfin's FileRefresher discover/index that branch.
        // No API key, HTTP call, global scan, or whole-library QueueRefresh is needed.
        _logger.LogInformation(
            "SQLITE PILOT PATH NOTIFY | Library={Library} | Path={Path} | TMDB={TmdbId} | Method=ILibraryMonitor.ReportFileSystemChanged | GlobalScan=NO",
            PilotLibraryName,
            materializedMoviePath,
            PilotMovieTmdbId);

        _libraryMonitor.ReportFileSystemChanged(materializedMoviePath);

        var deadline = DateTime.UtcNow + PilotRefreshTimeout;
        JellyfinMovie? movie = null;
        var lastCount = -1;
        long? lastTmdbId = null;
        int? lastYear = null;
        string lastGenres = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movies = _libraryManager
                .GetExistingItems<JellyfinMovie>(
                    includePaths: new HashSet<string>(
                        [expectedMoviesPath],
                        StringComparer.Ordinal));

            var candidate = movies.Count == 1
                ? movies[0]
                : null;

            var currentTmdbId = candidate?.GetTmdbId();
            var currentMovie = candidate?.GetMovie();
            var currentYear = currentMovie?.ProductionYear;
            var currentGenres = currentMovie?.Genres ?? Array.Empty<string>();
            var currentGenresText = string.Join(",", currentGenres);

            if (movies.Count != lastCount
                || currentTmdbId != lastTmdbId
                || currentYear != lastYear
                || !string.Equals(currentGenresText, lastGenres, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "SQLITE PILOT PATH WAIT | Library={Library} | IndexedMovies={IndexedMovies} | SingleTmdb={SingleTmdb} | Year={Year} | Genres={Genres} | ExpectedTmdb={ExpectedTmdb}",
                    PilotLibraryName,
                    movies.Count,
                    currentTmdbId?.ToString() ?? "<none>",
                    currentYear?.ToString() ?? "<none>",
                    string.IsNullOrEmpty(currentGenresText) ? "<none>" : currentGenresText,
                    PilotMovieTmdbId);

                lastCount = movies.Count;
                lastTmdbId = currentTmdbId;
                lastYear = currentYear;
                lastGenres = currentGenresText;
            }

            if (candidate is not null
                && candidate.GetTmdbId() == PilotMovieTmdbId
                && string.Equals(candidate.Name, "Union County", StringComparison.Ordinal)
                && currentYear == 2026
                && currentGenres.Contains("Drama", StringComparer.OrdinalIgnoreCase))
            {
                movie = candidate;
                break;
            }

            await Task.Delay(
                PilotPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        if (movie is null)
        {
            throw new TimeoutException(
                $"Timed out waiting for Jellyfin targeted path import. Last indexed movie count: {lastCount}; TMDB={lastTmdbId?.ToString() ?? "<none>"}; Year={lastYear?.ToString() ?? "<none>"}; Genres={lastGenres}; expected TMDB {PilotMovieTmdbId}, Year=2026, Genre=Drama.");
        }

        var resolvedMovie = movie.GetMovie()
            ?? throw new InvalidOperationException(
                "Jellyfin pilot movie wrapper does not contain a Movie instance.");

        _logger.LogInformation(
            "SQLITE PILOT PATH IMPORT COMPLETE | Library={Library} | IndexedBefore={IndexedBefore} | IndexedAfter=1 | LibraryId={LibraryId} | JellyfinMovieId={MovieId} | TMDB={TmdbId} | Title={Title} | Year={Year} | Genres={Genres} | Path={Path} | GlobalScan=NO",
            PilotLibraryName,
            indexedBefore.Count,
            libraryItemId,
            movie.Id,
            PilotMovieTmdbId,
            movie.Name,
            resolvedMovie.ProductionYear,
            string.Join(",", resolvedMovie.Genres),
            movie.Path);

        return new SqlitePilotLibraryResult(
            PilotLibraryName,
            libraryItemId,
            movie.Id,
            movie.Name,
            PilotMovieTmdbId,
            movie.Path);
    }
}

public sealed record SqlitePilotLibraryResult(
    string LibraryName,
    Guid LibraryItemId,
    Guid MovieItemId,
    string MovieTitle,
    long TmdbId,
    string MoviePath);
