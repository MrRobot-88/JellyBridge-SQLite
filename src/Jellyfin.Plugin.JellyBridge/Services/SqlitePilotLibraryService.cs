using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Controlled one-item library pilot for the SQLite-first path.
/// Creates exactly one native Jellyfin library named "Discover Movies" when
/// absent and validates only its physical Movies folder. It never starts a
/// global Jellyfin library scan.
/// </summary>
public sealed class SqlitePilotLibraryService
{
    public const string PilotLibraryName = "Discover Movies";
    public const long PilotMovieTmdbId = 1482938;

    private readonly JellyfinILibraryManager _libraryManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<SqlitePilotLibraryService> _logger;

    public SqlitePilotLibraryService(
        JellyfinILibraryManager libraryManager,
        IDirectoryService directoryService,
        ILogger<SqlitePilotLibraryService> logger)
    {
        _libraryManager = libraryManager;
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
                $"Pilot library scan requires exactly one materialized movie folder; found {materializedFolders.Length}.");
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

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"More than one '{PilotLibraryName}' library exists.");
        }

        var created = false;

        if (matches.Count == 0)
        {
            var options = new LibraryOptions
            {
                PathInfos =
                [
                    new MediaPathInfo(moviesPath)
                ],
                EnableRealtimeMonitor = false,
                SaveLocalMetadata = false,
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type = "Movie"
                    }
                ]
            };

            await _libraryManager.Inner
                .AddVirtualFolder(
                    PilotLibraryName,
                    CollectionTypeOptions.movies,
                    options,
                    refreshLibrary: false)
                .ConfigureAwait(false);

            created = true;

            virtualFolders = _libraryManager.Inner
                .GetVirtualFolders(true)
                .ToList();

            matches = virtualFolders
                .Where(
                    folder => string.Equals(
                        folder.Name,
                        PilotLibraryName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one '{PilotLibraryName}' library after setup; found {matches.Count}.");
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

        var physicalFolders = libraryFolder
            .GetPhysicalFolders()
            .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
            .ToList();

        var targetPhysicalFolders = physicalFolders
            .Where(
                folder => string.Equals(
                    Path.GetFullPath(folder.Path),
                    expectedMoviesPath,
                    StringComparison.Ordinal))
            .ToList();

        if (targetPhysicalFolders.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one physical folder for '{expectedMoviesPath}'; found {targetPhysicalFolders.Count}.");
        }

        var refreshOptions = new MetadataRefreshOptions(_directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            RegenerateTrickplay = false,
            ForceSave = false,
            IsAutomated = false,
            RemoveOldMetadata = false
        };

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY SCAN START | Library={Library} | LibraryId={LibraryId} | Path={Path} | GlobalScan=NO",
            PilotLibraryName,
            libraryItemId,
            expectedMoviesPath);

        await targetPhysicalFolders[0]
            .ValidateChildren(
                new Progress<double>(),
                refreshOptions,
                recursive: true,
                allowRemoveRoot: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var movies = _libraryManager
            .GetExistingItems<JellyfinMovie>(
                includePaths: new HashSet<string>(
                    [expectedMoviesPath],
                    StringComparer.Ordinal));

        if (movies.Count != 1)
        {
            throw new InvalidOperationException(
                $"Targeted '{PilotLibraryName}' scan expected exactly one Jellyfin movie; found {movies.Count}.");
        }

        var movie = movies[0];

        if (movie.GetTmdbId() != PilotMovieTmdbId)
        {
            throw new InvalidOperationException(
                $"Targeted scan produced TMDB {movie.GetTmdbId()?.ToString() ?? "<none>"}; expected {PilotMovieTmdbId}.");
        }

        _logger.LogInformation(
            "SQLITE PILOT LIBRARY SCAN COMPLETE | Library={Library} | Created={Created} | LibraryId={LibraryId} | JellyfinMovieId={MovieId} | TMDB={TmdbId} | Title={Title} | Path={Path} | GlobalScan=NO",
            PilotLibraryName,
            created,
            libraryItemId,
            movie.Id,
            PilotMovieTmdbId,
            movie.Name,
            movie.Path);

        return new SqlitePilotLibraryResult(
            created,
            PilotLibraryName,
            libraryItemId,
            movie.Id,
            movie.Name,
            PilotMovieTmdbId,
            movie.Path);
    }
}

public sealed record SqlitePilotLibraryResult(
    bool LibraryCreated,
    string LibraryName,
    Guid LibraryItemId,
    Guid MovieItemId,
    string MovieTitle,
    long TmdbId,
    string MoviePath);
