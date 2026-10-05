using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Services;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Tasks;

/// <summary>
/// Production scheduled sync entry point for JellyBridge-SQLite.
/// UI ARR catalog -> SQLite planner -> ADD/UPDATE/REMOVE -> exact Jellyfin path notifications.
/// </summary>
public sealed class SyncTask : IScheduledTask
{
    private readonly DebugLogger<SyncTask> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SyncTask(
        ILogger<SyncTask> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = new DebugLogger<SyncTask>(logger);
        _scopeFactory = scopeFactory;

        _logger.LogInformation(
            "JellyBridge-SQLite SyncTask initialized - production SQLite-first orchestration active");
    }

    public string Name => "JellyBridge SQLite";

    public string Key => "JellyBridgeSync";

    public string Description =>
        "Synchronizes Discover Movies and Discover Series from the UI ARR catalog using SQLite state.";

    public string Category => "JellyBridge";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var isEnabled = Plugin.GetConfigOrDefault<bool>(
            nameof(PluginConfiguration.IsEnabled));

        if (!isEnabled)
        {
            _logger.LogInformation(
                "JellyBridge-SQLite is disabled; production sync skipped.");

            progress.Report(100);
            return;
        }

        await Plugin.ExecuteWithLockAsync(
            async () =>
            {
                using var scope = _scopeFactory.CreateScope();

                var sqliteSync = scope.ServiceProvider
                    .GetRequiredService<SqliteProductionSyncService>();

                _logger.LogInformation(
                    "SQLITE MAIN SYNC START | LegacyJsonSync=OFF | LegacyCleanup=OFF | GlobalScan=NO");

                var result = await sqliteSync
                    .RunAsync(progress, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "SQLITE MAIN SYNC TASK PASS | Generation={Generation} | Movies={Movies} | Series={Series} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | Applied={Applied} | TotalMs={TotalMs}",
                    result.Generation,
                    result.DesiredMovies,
                    result.DesiredSeries,
                    result.AddCount,
                    result.UpdateCount,
                    result.RemoveCount,
                    result.UnchangedCount,
                    result.AppliedChanges,
                    result.TotalMilliseconds);

                return result;
            },
            _logger,
            "JellyBridge SQLite");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var isEnabled = Plugin.GetConfigOrDefault<bool>(
            nameof(PluginConfiguration.IsEnabled));

        if (!isEnabled)
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        var intervalHours = Plugin.GetConfigOrDefault<double>(
            nameof(PluginConfiguration.SyncIntervalHours));

        return
        [
            JellyfinTaskTrigger.Interval(TimeSpan.FromHours(intervalHours))
        ];
    }
}
