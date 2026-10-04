using Jellyfin.Plugin.JellyBridge.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Tasks;

/// <summary>
/// Manual-only validation task for the SQLite-first sync core.
/// </summary>
public sealed class SqliteDryRunTask : IScheduledTask
{
    private readonly ILogger<SqliteDryRunTask> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SqliteDryRunTask(
        ILogger<SqliteDryRunTask> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public string Name => "JellyBridge-SQLite Dry Run";
    public string Key => "JellyBridgeSQLiteDryRun";
    public string Description =>
        "Fetches the configured Discover working set and computes the SQLite diff without materializing, updating or deleting library items.";
    public string Category => "JellyBridge-SQLite";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(5);

        using var scope = _scopeFactory.CreateScope();

        var service =
            scope.ServiceProvider.GetRequiredService<SqliteDryRunService>();

        progress.Report(10);

        var result =
            await service.RunAsync(cancellationToken).ConfigureAwait(false);

        progress.Report(100);

        _logger.LogInformation(
            "JellyBridge-SQLite Dry Run completed | Movies={Movies} | Series={Series} | ExistingState={ExistingState} | Add={Add} | Update={Update} | Remove={Remove} | Unchanged={Unchanged} | TotalMs={TotalMs}",
            result.DesiredMovies,
            result.DesiredSeries,
            result.ExistingState,
            result.AddCount,
            result.UpdateCount,
            result.RemoveCount,
            result.UnchangedCount,
            result.TotalMilliseconds);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Manual-only by design. It must never run automatically.
        return Array.Empty<TaskTriggerInfo>();
    }
}
