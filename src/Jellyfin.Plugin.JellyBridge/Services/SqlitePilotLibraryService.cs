using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Controlled one-item Jellyfin library pilot for the SQLite-first path.
/// The library structure follows the original JellyBridge setup, while the
/// refresh path mirrors JellyBridge's proven IProviderManager.QueueRefresh
/// sequence and is restricted to the Discover Movies library only.
/// </summary>
public sealed class SqlitePilotLibraryService
{
    public const string PilotLibraryName = "Discover Movies";
    public const long PilotMovieTmdbId = 1482938;

    private static readonly TimeSpan PilotRefreshTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PilotPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly JellyfinILibraryManager _libraryManager;
    private readonly JellyfinIProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<SqlitePilotLibraryService> _logger;

    public SqlitePilotLibraryService(
        JellyfinILibraryManager libraryManager,
        JellyfinIProviderManager providerManager,
        IDirectoryService directoryService,
        ILogger<SqlitePilotLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
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

        if (!Path.GetFileName(materializedFolders[0])
            .Contains(
                $"tmdbid-{PilotMovieTmdbId}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The one materialized movie is not pilot TMDB {PilotMovieTmdbId}: {materializedFolders[0]}");
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

        // The SQLite materializer provides the local NFO and compact poster.
        // Disable remote metadata/image providers for this Discover working set,
        // while keeping the local NFO and local image providers available.
        var libraryOptions = libraryFolder.GetLibraryOptions();
        libraryOptions.EnableRealtimeMonitor = false;
        libraryOptions.SaveLocalMetadata = false;
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
            "SQLITE PILOT LIBRARY OPTIONS | Library={Library} | RemoteMetadata=OFF | RemoteImages=OFF | LocalNfo=ON | LocalImages=ON | RealtimeMonitor=OFF",
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

        // This mirrors the original JellyBridge RefreshService sequence:
        // 1) Full refresh at High priority to remove stale Jellyfin index items.
        // 2) Default refresh at Normal priority for normal updates/user data.
        // 3) Full create refresh at Low priority for the newly materialized item.
        // Only Discover Movies is queued; no global Jellyfin scan is started.
        var removeOptions = new MetadataRefreshOptions(_directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            RegenerateTrickplay = false,
            ForceSave = true,
            IsAutomated = true,
            RemoveOldMetadata = false
        };

        var updateOptions = new MetadataRefreshOptions(_directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            RegenerateTrickplay = false,
            ForceSave = true,
            IsAutomated = true,
            RemoveOldMetadata = false
        };

        var createOptions = new MetadataRefreshOptions(_directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = true,
            ReplaceAllImages = false,
            RegenerateTrickplay = false,
            ForceSave = true,
            IsAutomated = false,
            RemoveOldMetadata = false
        };

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY REMOVE QUEUE | Library={Library} | LibraryId={LibraryId} | IndexedBefore={IndexedBefore} | GlobalScan=NO",
            PilotLibraryName,
            libraryItemId,
            indexedBefore.Count);

        _providerManager.QueueRefresh(
            libraryItemId,
            removeOptions,
            RefreshPriority.High);

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY UPDATE QUEUE | Library={Library} | LibraryId={LibraryId} | GlobalScan=NO",
            PilotLibraryName,
            libraryItemId);

        _providerManager.QueueRefresh(
            libraryItemId,
            updateOptions,
            RefreshPriority.Normal);

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY CREATE QUEUE | Library={Library} | LibraryId={LibraryId} | Path={Path} | TMDB={TmdbId} | GlobalScan=NO",
            PilotLibraryName,
            libraryItemId,
            expectedMoviesPath,
            PilotMovieTmdbId);

        _providerManager.QueueRefresh(
            libraryItemId,
            createOptions,
            RefreshPriority.Low);

        var deadline = DateTime.UtcNow + PilotRefreshTimeout;
        JellyfinMovie? movie = null;
        var lastCount = -1;
        long? lastTmdbId = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movies = _libraryManager
                .GetExistingItems<JellyfinMovie>(
                    includePaths: new HashSet<string>(
                        [expectedMoviesPath],
                        StringComparer.Ordinal));

            var currentTmdbId = movies.Count == 1
                ? movies[0].GetTmdbId()
                : null;

            if (movies.Count != lastCount
                || currentTmdbId != lastTmdbId)
            {
                _logger.LogInformation(
                    "SQLITE PILOT LIBRARY WAIT | Library={Library} | IndexedMovies={IndexedMovies} | SingleTmdb={SingleTmdb} | ExpectedTmdb={ExpectedTmdb}",
                    PilotLibraryName,
                    movies.Count,
                    currentTmdbId?.ToString() ?? "<none>",
                    PilotMovieTmdbId);

                lastCount = movies.Count;
                lastTmdbId = currentTmdbId;
            }

            // More than one item is expected while Jellyfin removes the stale
            // legacy index entries. Keep waiting instead of failing immediately.
            if (movies.Count == 1)
            {
                movie = movies[0];

                if (movie.GetTmdbId() == PilotMovieTmdbId)
                {
                    break;
                }
            }
            else
            {
                movie = null;
            }

            await Task.Delay(
                PilotPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        if (movie is null)
        {
            throw new TimeoutException(
                $"Timed out waiting for Jellyfin to converge to one pilot movie. Last indexed movie count: {lastCount}; expected TMDB {PilotMovieTmdbId}.");
        }

        if (movie.GetTmdbId() != PilotMovieTmdbId)
        {
            throw new InvalidOperationException(
                $"Targeted refresh converged to TMDB {movie.GetTmdbId()?.ToString() ?? "<none>"}; expected {PilotMovieTmdbId}.");
        }

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY REFRESH COMPLETE | Library={Library} | IndexedBefore={IndexedBefore} | IndexedAfter=1 | LibraryId={LibraryId} | JellyfinMovieId={MovieId} | TMDB={TmdbId} | Title={Title} | Path={Path} | GlobalScan=NO",
            PilotLibraryName,
            indexedBefore.Count,
            libraryItemId,
            movie.Id,
            PilotMovieTmdbId,
            movie.Name,
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
