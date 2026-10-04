using Jellyfin.Plugin.JellyBridge.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Tasks;

/// <summary>
/// Manual-only native Jellyfin library pilot for the one materialized movie.
/// </summary>
public sealed class SqlitePilotLibraryTask : IScheduledTask
{
    private readonly ILogger<SqlitePilotLibraryTask> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SqlitePilotLibraryTask(
        ILogger<SqlitePilotLibraryTask> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public string Name => "JellyBridge-SQLite One Movie Library Scan";
    public string Key => "JellyBridgeSQLiteMovieLibraryScan";
    public string Description =>
        "Creates/verifies the Discover Movies root library and scans only the one SQLite pilot movie. No global library scan. Manual-only.";
    public string Category => "JellyBridge-SQLite";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(5);

        using var scope = _scopeFactory.CreateScope();

        var service = scope.ServiceProvider
            .GetRequiredService<SqlitePilotLibraryService>();

        progress.Report(10);

        var result = await service
            .EnsureLibraryAndScanAsync(cancellationToken)
            .ConfigureAwait(false);

        progress.Report(100);

        _logger.LogInformation(
            "JellyBridge-SQLite One Movie Library Scan completed | Library={Library} | Created={Created} | LibraryId={LibraryId} | MovieId={MovieId} | TMDB={TmdbId} | Title={Title} | Path={Path}",
            result.LibraryName,
            result.LibraryCreated,
            result.LibraryItemId,
            result.MovieItemId,
            result.TmdbId,
            result.MovieTitle,
            result.MoviePath);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
