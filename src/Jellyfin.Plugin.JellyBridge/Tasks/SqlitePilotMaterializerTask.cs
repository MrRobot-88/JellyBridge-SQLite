using Jellyfin.Plugin.JellyBridge.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Tasks;

/// <summary>
/// Manual-only one-movie materialization pilot.
/// </summary>
public sealed class SqlitePilotMaterializerTask : IScheduledTask
{
    private readonly ILogger<SqlitePilotMaterializerTask> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SqlitePilotMaterializerTask(
        ILogger<SqlitePilotMaterializerTask> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public string Name => "JellyBridge-SQLite One Movie Pilot";
    public string Key => "JellyBridgeSQLiteMoviePilot";
    public string Description =>
        "Materializes Union County (TMDB 1482938) using SQLite state, movie.nfo, one compact w342 poster and one placeholder video. Manual-only.";
    public string Category => "JellyBridge-SQLite";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(5);

        using var scope = _scopeFactory.CreateScope();

        var service = scope.ServiceProvider
            .GetRequiredService<SqlitePilotMaterializerService>();

        progress.Report(10);

        var result = await service
            .RunMoviePilotAsync(cancellationToken)
            .ConfigureAwait(false);

        progress.Report(100);

        _logger.LogInformation(
            "JellyBridge-SQLite One Movie Pilot completed | TMDB={TmdbId} | Year={Year} | Title={Title} | Path={Path}",
            result.TmdbId,
            result.Year,
            result.Title,
            result.TargetPath);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
