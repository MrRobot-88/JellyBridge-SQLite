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
        const string tier = BridgeTier.FullHd;

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

        // The normal Discover task owns only the 1080p tier. Future 4K rows
        // share the same database but are planned independently and must never
        // appear as removals during a normal Discover sync.
        var current = await _stateStore
            .GetMaterializedItemsForTierAsync(tier, cancellationToken)
            .ConfigureAwait(false);

        var desiredCatalog = movies.Concat(series).ToArray();
        var desiredPlanner = desiredCatalog
            .Select(item => new DesiredBridgeItem(
                item.MediaType,
                item.TmdbId,
                item.TargetPath,
                item.Fingerprint,
                tier))
            .ToArray();
        var desiredByKey = desiredCatalog.ToDictionary(
            item => new BridgeItemKey(item.MediaType, item.TmdbId, tier));

        var plan = _planner.Build(desiredPlanner, current);
        var generation = current.Count == 0
            ? 1
            : current.Values.Max(item => item.Generation) + 1;

        _logger.LogInformation(
            "SQLITE MAIN SYNC PLAN | Tier={Tier} | Generation={Generation} | Movies={Movies} | Series={Series} | Existing={Existing} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | LegacyJson=OFF | GlobalScan=NO",
            tier,
            generation,
            movies.Count,
            series.Count,
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

        foreach (var add in plan.Adds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new BridgeItemKey(add.MediaType, add.TmdbId, tier);
            var item = desiredByKey[key];

            var result = await _materializer
                .ApplyAsync(item, generation, null, cancellationToken)
                .ConfigureAwait(false);

            _librarySync.NotifyChangedPath(result.MediaType, result.TargetPath, "ADD");
            completedChanges++;
            ReportChangeProgress();
        }

        foreach (var update in plan.Updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = new BridgeItemKey(update.MediaType, update.TmdbId, tier);
            var item = desiredByKey[key];
            var previous = current[key];

            var result = await _materializer
                .ApplyAsync(item, generation, previous, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result.PreviousPath)
                && !string.Equals(result.PreviousPath, result.TargetPath, StringComparison.Ordinal))
            {
                _librarySync.NotifyChangedPath(result.MediaType, result.PreviousPath, "UPDATE_OLD_PATH_REMOVED");
            }

            _librarySync.NotifyChangedPath(result.MediaType, result.TargetPath, "UPDATE");
            completedChanges++;
            ReportChangeProgress();
        }

        foreach (var remove in plan.Removes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _materializer
                .RemoveAsync(remove, cancellationToken)
                .ConfigureAwait(false);

            _librarySync.NotifyChangedPath(result.MediaType, result.TargetPath, "REMOVE");
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
            "SQLITE MAIN SYNC COMPLETE | Tier={Tier} | Generation={Generation} | Movies={Movies} | Series={Series} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | Applied={Applied} | TotalMs={TotalMs} | GlobalScan=NO | MetadataJson=NO",
            tier,
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
