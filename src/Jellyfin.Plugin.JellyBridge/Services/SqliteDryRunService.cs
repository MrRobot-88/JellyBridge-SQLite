using System.Diagnostics;
using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Executes a read-only desired-set + SQLite diff pass.
/// It does not materialize, update or remove any Jellyfin filesystem items.
/// </summary>
public sealed class SqliteDryRunService
{
    private readonly CatalogSelectionService _selection;
    private readonly BridgeStateStore _stateStore;
    private readonly SyncPlanner _planner;
    private readonly ILogger<SqliteDryRunService> _logger;

    public SqliteDryRunService(
        CatalogSelectionService selection,
        BridgeStateStore stateStore,
        SyncPlanner planner,
        ILogger<SqliteDryRunService> logger)
    {
        _selection = selection;
        _stateStore = stateStore;
        _planner = planner;
        _logger = logger;
    }

    public async Task<SqliteDryRunResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var totalWatch = Stopwatch.StartNew();

        var movieTarget = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverMovieTargetCount));

        var seriesTarget = Plugin.GetConfigOrDefault<int>(
            nameof(PluginConfiguration.DiscoverSeriesTargetCount));

        var movieWatch = Stopwatch.StartNew();

        var movies = await _selection.GetDesiredAsync(
            "movie",
            movieTarget,
            cancellationToken).ConfigureAwait(false);

        movieWatch.Stop();

        var seriesWatch = Stopwatch.StartNew();

        var series = await _selection.GetDesiredAsync(
            "tv",
            seriesTarget,
            cancellationToken).ConfigureAwait(false);

        seriesWatch.Stop();

        var stateWatch = Stopwatch.StartNew();

        var materialized =
            await _stateStore.GetMaterializedItemsAsync(
                cancellationToken).ConfigureAwait(false);

        stateWatch.Stop();

        var desired = movies
            .Concat(series)
            .Select(item => item.ToPlannerItem())
            .ToArray();

        var planWatch = Stopwatch.StartNew();

        var plan = _planner.Build(
            desired,
            materialized);

        planWatch.Stop();
        totalWatch.Stop();

        var result = new SqliteDryRunResult(
            movies.Count,
            series.Count,
            materialized.Count,
            plan.Adds.Count,
            plan.Updates.Count,
            plan.Removes.Count,
            plan.Unchanged.Count,
            movieWatch.ElapsedMilliseconds,
            seriesWatch.ElapsedMilliseconds,
            stateWatch.ElapsedMilliseconds,
            planWatch.ElapsedMilliseconds,
            totalWatch.ElapsedMilliseconds);

        _logger.LogInformation(
            "SQLITE DRY RUN | Movies={Movies} | Series={Series} | ExistingState={ExistingState} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | MoviesMs={MoviesMs} | SeriesMs={SeriesMs} | StateMs={StateMs} | PlanMs={PlanMs} | TotalMs={TotalMs}",
            result.DesiredMovies,
            result.DesiredSeries,
            result.ExistingState,
            result.AddCount,
            result.UpdateCount,
            result.RemoveCount,
            result.UnchangedCount,
            result.MovieSelectionMilliseconds,
            result.SeriesSelectionMilliseconds,
            result.StateReadMilliseconds,
            result.PlanMilliseconds,
            result.TotalMilliseconds);

        foreach (var sample in movies.Take(5))
        {
            _logger.LogInformation(
                "SQLITE DRY RUN MOVIE | TMDB={TmdbId} | Year={Year} | Title={Title}",
                sample.TmdbId,
                sample.CatalogItem.Year,
                sample.CatalogItem.Title);
        }

        foreach (var sample in series.Take(5))
        {
            _logger.LogInformation(
                "SQLITE DRY RUN SERIES | TMDB={TmdbId} | Year={Year} | Title={Title}",
                sample.TmdbId,
                sample.CatalogItem.Year,
                sample.CatalogItem.Title);
        }

        return result;
    }
}

public sealed record SqliteDryRunResult(
    int DesiredMovies,
    int DesiredSeries,
    int ExistingState,
    int AddCount,
    int UpdateCount,
    int RemoveCount,
    int UnchangedCount,
    long MovieSelectionMilliseconds,
    long SeriesSelectionMilliseconds,
    long StateReadMilliseconds,
    long PlanMilliseconds,
    long TotalMilliseconds);
