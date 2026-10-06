using System.Diagnostics;
using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Production SQLite-first synchronization pipeline.
/// Catalog -> desired set -> SQLite diff -> ADD/UPDATE/REMOVE -> exact Jellyfin path notify.
/// </summary>
public sealed class SqliteProductionSyncService
{
    private readonly CatalogSelectionService _selection;
    private readonly BridgeStateStore _stateStore;
    private readonly SyncPlanner _planner;
    private readonly SqliteMaterializerService _materializer;
    private readonly SqliteLibrarySyncService _librarySync;
    private readonly ILogger<SqliteProductionSyncService> _logger;

    public SqliteProductionSyncService(
        CatalogSelectionService selection,
        BridgeStateStore stateStore,
        SyncPlanner planner,
        SqliteMaterializerService materializer,
        SqliteLibrarySyncService librarySync,
        ILogger<SqliteProductionSyncService> logger)
    {
        _selection = selection;
        _stateStore = stateStore;
        _planner = planner;
        _materializer = materializer;
        _librarySync = librarySync;
        _logger = logger;
    }

    public async Task<SqliteProductionSyncResult> RunAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        progress?.Report(5);

        // Fail before any materialization if either Jellyfin root library is wrong.
        await _librarySync.EnsureLibrariesConfiguredAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(10);

        var movieTarget = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverMovieTargetCount));
        var seriesTarget = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverSeriesTargetCount));

        var movies = await _selection.GetDesiredAsync(
            "movie",
            movieTarget,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(12);

        var series = await _selection.GetDesiredAsync(
            "tv",
            seriesTarget,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(14);

        // 4K Discover is an exact tier mirror of the already-selected 1080p
        // working set. Do not query/rank the catalog a second time.
        var movies4k = movies
            .Select(item => _selection.WithTier(item, CatalogSelectionService.FourK))
            .ToArray();
        var series4k = series
            .Select(item => _selection.WithTier(item, CatalogSelectionService.FourK))
            .ToArray();

        var current = await _stateStore
            .GetMaterializedItemsAsync(cancellationToken)
            .ConfigureAwait(false);

        var desiredCatalog = movies.Concat(series).Concat(movies4k).Concat(series4k).ToArray();
        var desiredPlanner = desiredCatalog.Select(item => item.ToPlannerItem()).ToArray();
        var desiredByKey = desiredCatalog.ToDictionary(
            item => new BridgeItemKey(item.MediaType, item.TmdbId, item.Tier));

        var plan = _planner.Build(desiredPlanner, current);

        // First 4K bootstrap must be additive-only. Protect the already-working
        // default Discover tier from any accidental ADD/UPDATE/REMOVE.
        var has4kState = current.Values.Any(
            state => string.Equals(state.Tier, CatalogSelectionService.FourK, StringComparison.OrdinalIgnoreCase));
        if (!has4kState)
        {
            var defaultTierChanges =
                plan.Adds.Count(item => string.Equals(item.Tier, CatalogSelectionService.DefaultTier, StringComparison.OrdinalIgnoreCase))
                + plan.Updates.Count(item => string.Equals(item.Tier, CatalogSelectionService.DefaultTier, StringComparison.OrdinalIgnoreCase))
                + plan.Removes.Count(item => string.Equals(item.Tier, CatalogSelectionService.DefaultTier, StringComparison.OrdinalIgnoreCase));

            if (defaultTierChanges != 0)
            {
                throw new InvalidOperationException(
                    $"Initial 4K bootstrap refused because it would modify {defaultTierChanges} existing 1080p Discover item(s).");
            }
        }

        var generation = current.Count == 0
            ? 1
            : current.Values.Max(item => item.Generation) + 1;

        _logger.LogInformation(
            "SQLITE MAIN SYNC PLAN | Generation={Generation} | Movies1080p={Movies1080p} | Series1080p={Series1080p} | Movies4K={Movies4K} | Series4K={Series4K} | Existing={Existing} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | LegacyJson=OFF | GlobalScan=NO",
            generation,
            movies.Count,
            series.Count,
            movies4k.Length,
            series4k.Length,
            current.Count,
            plan.Adds.Count,
            plan.Updates.Count,
            plan.Removes.Count,
            plan.Unchanged.Count);

        var totalChanges = plan.ChangeCount;
        var completedChanges = 0;

        void ReportChangeProgress()
        {
            if (totalChanges <= 0)
            {
                progress?.Report(95);
                return;
            }

            var value = 15d + (80d * completedChanges / totalChanges);
            progress?.Report(Math.Min(95d, value));
        }

        foreach (var add in plan.Adds.OrderBy(item => string.Equals(item.Tier, CatalogSelectionService.DefaultTier, StringComparison.Ordinal) ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new BridgeItemKey(add.MediaType, add.TmdbId, add.Tier);
            var item = desiredByKey[key];

            var result = await _materializer
                .ApplyAsync(item, generation, null, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await _librarySync
                    .EnsurePhysicalRootReadyAsync(result.MediaType, result.Tier, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                if (string.Equals(result.Tier, CatalogSelectionService.FourK, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var rollbackState = await _stateStore
                            .GetMaterializedItemsAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        if (rollbackState.TryGetValue(key, out var createdState))
                        {
                            await _materializer
                                .RemoveAsync(createdState, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception rollbackException)
                    {
                        _logger.LogError(
                            rollbackException,
                            "Failed to roll back newly-created 4K item after physical-root bootstrap failure. Media={MediaType} TMDB={TmdbId}",
                            result.MediaType,
                            result.TmdbId);
                    }
                }

                throw;
            }

            _librarySync.NotifyChangedPath(result.MediaType, result.Tier, result.TargetPath, "ADD");
            completedChanges++;
            ReportChangeProgress();
        }

        foreach (var update in plan.Updates.OrderBy(item => string.Equals(item.Tier, CatalogSelectionService.DefaultTier, StringComparison.Ordinal) ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new BridgeItemKey(update.MediaType, update.TmdbId, update.Tier);
            var item = desiredByKey[key];
            var previous = current[key];

            var result = await _materializer
                .ApplyAsync(item, generation, previous, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.PreviousPath)
                && !string.Equals(result.PreviousPath, result.TargetPath, StringComparison.Ordinal))
            {
                _librarySync.NotifyChangedPath(result.MediaType, result.Tier, result.PreviousPath, "UPDATE_OLD_PATH_REMOVED");
            }

            _librarySync.NotifyChangedPath(result.MediaType, result.Tier, result.TargetPath, "UPDATE");
            completedChanges++;
            ReportChangeProgress();
        }

        foreach (var remove in plan.Removes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _materializer
                .RemoveAsync(remove, cancellationToken)
                .ConfigureAwait(false);

            _librarySync.NotifyChangedPath(result.MediaType, result.Tier, result.TargetPath, "REMOVE");
            completedChanges++;
            ReportChangeProgress();
        }

        watch.Stop();
        progress?.Report(100);

        var resultSummary = new SqliteProductionSyncResult(
            generation,
            movies.Count,
            series.Count,
            current.Count,
            plan.Adds.Count,
            plan.Updates.Count,
            plan.Removes.Count,
            plan.Unchanged.Count,
            completedChanges,
            watch.ElapsedMilliseconds);

        _logger.LogInformation(
            "SQLITE MAIN SYNC COMPLETE | Generation={Generation} | MoviesPerTier={Movies} | SeriesPerTier={Series} | TierMirror4K=YES | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | Applied={Applied} | TotalMs={TotalMs} | GlobalScan=NO | MetadataJson=NO",
            resultSummary.Generation,
            resultSummary.DesiredMovies,
            resultSummary.DesiredSeries,
            resultSummary.AddCount,
            resultSummary.UpdateCount,
            resultSummary.RemoveCount,
            resultSummary.UnchangedCount,
            resultSummary.AppliedChanges,
            resultSummary.TotalMilliseconds);

        return resultSummary;
    }
}

public sealed record SqliteProductionSyncResult(
    long Generation,
    int DesiredMovies,
    int DesiredSeries,
    int ExistingState,
    int AddCount,
    int UpdateCount,
    int RemoveCount,
    int UnchangedCount,
    int AppliedChanges,
    long TotalMilliseconds);
