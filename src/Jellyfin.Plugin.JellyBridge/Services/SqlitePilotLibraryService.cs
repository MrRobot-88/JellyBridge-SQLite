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
/// pattern and is restricted to the Discover Movies library only.
/// </summary>
public sealed class SqlitePilotLibraryService
{
    public const string PilotLibraryName = "Discover Movies";
    public const long PilotMovieTmdbId = 1482938;

    private static readonly TimeSpan PilotRefreshTimeout = TimeSpan.FromMinutes(2);
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

        // The original JellyBridge creates libraries through Jellyfin's native
        // /Library/VirtualFolders API from the configuration UI. For this pilot
        // we deliberately do not invent another creation path; the two root
        // Discover libraries must already exist.
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

        // Keep Jellyfin from reaching out to remote metadata/image providers for
        // this Discover working set. The SQLite materializer already provides
        // the local NFO and compact poster.jpg that Jellyfin should consume.
        // Preserve unrelated library settings.
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

        // Mirror the original JellyBridge refresh path. Its RefreshService uses
        // IProviderManager.QueueRefresh on the Jellyfin library item instead of
        // manually walking/validating children. We keep the same proven Jellyfin
        // integration but queue ONLY Discover Movies.
        var refreshOptions = new MetadataRefreshOptions(_directoryService)
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

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY REFRESH QUEUE | Library={Library} | LibraryId={LibraryId} | Path={Path} | TMDB={TmdbId} | GlobalScan=NO",
            PilotLibraryName,
            libraryItemId,
            expectedMoviesPath,
            PilotMovieTmdbId);

        _providerManager.QueueRefresh(
            libraryItemId,
            refreshOptions,
            RefreshPriority.High);

        var deadline = DateTime.UtcNow + PilotRefreshTimeout;
        JellyfinMovie? movie = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movies = _libraryManager
                .GetExistingItems<JellyfinMovie>(
                    includePaths: new HashSet<string>(
                        [expectedMoviesPath],
                        StringComparer.Ordinal));

            if (movies.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Targeted '{PilotLibraryName}' refresh expected at most one Jellyfin movie; found {movies.Count}.");
            }

            if (movies.Count == 1)
            {
                movie = movies[0];

                if (movie.GetTmdbId() == PilotMovieTmdbId)
                {
                    break;
                }
            }

            await Task.Delay(
                PilotPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        if (movie is null)
        {
            throw new TimeoutException(
                $"Timed out waiting for Jellyfin to import pilot TMDB {PilotMovieTmdbId} into '{PilotLibraryName}'.");
        }

        if (movie.GetTmdbId() != PilotMovieTmdbId)
        {
            throw new InvalidOperationException(
                $"Targeted refresh produced TMDB {movie.GetTmdbId()?.ToString() ?? "<none>"}; expected {PilotMovieTmdbId}.");
        }

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY REFRESH COMPLETE | Library={Library} | LibraryId={LibraryId} | JellyfinMovieId={MovieId} | TMDB={TmdbId} | Title={Title} | Path={Path} | GlobalScan=NO",
            PilotLibraryName,
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
