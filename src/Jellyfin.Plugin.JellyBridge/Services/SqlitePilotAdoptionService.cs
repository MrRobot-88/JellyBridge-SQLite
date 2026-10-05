using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Controlled migration of the existing Union County pilot from
/// pilot-materialized state to the production SQLite materializer.
/// Exactly one known movie is allowed.
/// </summary>
public sealed class SqlitePilotAdoptionService
{
    public const long PilotTmdbId = 1482938;

    private readonly CatalogSelectionService _selection;
    private readonly BridgeStateStore _stateStore;
    private readonly SqliteMaterializerService _materializer;
    private readonly SqlitePilotLibraryService _libraryService;
    private readonly ILogger<SqlitePilotAdoptionService> _logger;

    public SqlitePilotAdoptionService(
        CatalogSelectionService selection,
        BridgeStateStore stateStore,
        SqliteMaterializerService materializer,
        SqlitePilotLibraryService libraryService,
        ILogger<SqlitePilotAdoptionService> logger)
    {
        _selection = selection;
        _stateStore = stateStore;
        _materializer = materializer;
        _libraryService = libraryService;
        _logger = logger;
    }

    public async Task<SqlitePilotAdoptionResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await _stateStore
            .GetMaterializedItemsAsync(cancellationToken)
            .ConfigureAwait(false);

        var key = new BridgeItemKey("movie", PilotTmdbId, CatalogSelectionService.DefaultTier);

        if (state.Count != 1 || !state.TryGetValue(key, out var previous))
        {
            throw new InvalidOperationException(
                $"Union County adoption requires exactly one SQLite state row for movie TMDB {PilotTmdbId}; found {state.Count} total row(s).");
        }

        if (!string.Equals(previous.MaterializationState, "pilot-materialized", StringComparison.Ordinal)
            && !string.Equals(previous.MaterializationState, SqliteMaterializerService.MaterializedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected pilot ownership state: {previous.MaterializationState}");
        }

        var item = await _selection
            .GetDesiredByTmdbIdAsync(
                "movie",
                PilotTmdbId,
                2026,
                "U",
                cancellationToken)
            .ConfigureAwait(false);

        if (item.TmdbId != PilotTmdbId
            || !string.Equals(item.CatalogItem.Title, "Union County", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pilot selection mismatch. Expected Union County TMDB {PilotTmdbId}; got '{item.CatalogItem.Title}' TMDB {item.TmdbId}.");
        }

        var generation = Math.Max(previous.Generation + 1, 2);

        _logger.LogInformation(
            "SQLITE PILOT ADOPTION START | TMDB={TmdbId} | PreviousState={State} | PreviousGeneration={PreviousGeneration} | NewGeneration={Generation}",
            PilotTmdbId,
            previous.MaterializationState,
            previous.Generation,
            generation);

        var materialized = await _materializer
            .ApplyAsync(
                item,
                generation,
                previous,
                cancellationToken)
            .ConfigureAwait(false);

        var library = await _libraryService
            .EnsureLibraryAndScanAsync(cancellationToken)
            .ConfigureAwait(false);

        var after = await _stateStore
            .GetMaterializedItemsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (after.Count != 1
            || !after.TryGetValue(key, out var adopted)
            || !string.Equals(
                adopted.MaterializationState,
                SqliteMaterializerService.MaterializedState,
                StringComparison.Ordinal)
            || adopted.Generation != generation)
        {
            throw new InvalidOperationException(
                "Union County adoption completed filesystem/library work but final SQLite ownership state is not correct.");
        }

        _logger.LogInformation(
            "SQLITE PILOT ADOPTION COMPLETE | TMDB={TmdbId} | State={State} | Generation={Generation} | Path={Path} | JellyfinMovieId={JellyfinMovieId}",
            PilotTmdbId,
            adopted.MaterializationState,
            adopted.Generation,
            adopted.TargetPath,
            library.MovieItemId);

        return new SqlitePilotAdoptionResult(
            PilotTmdbId,
            adopted.MaterializationState,
            adopted.Generation,
            materialized.TargetPath,
            library.MovieItemId,
            library.MovieTitle,
            item.CatalogItem.Year);
    }
}

public sealed record SqlitePilotAdoptionResult(
    long TmdbId,
    string MaterializationState,
    long Generation,
    string TargetPath,
    Guid JellyfinMovieId,
    string Title,
    int? Year);
