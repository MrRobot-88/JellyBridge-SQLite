using Jellyfin.Plugin.JellyBridge.Configuration;
using Jellyfin.Plugin.JellyBridge.JellyseerrModel;
using Jellyfin.Plugin.JellyBridge.Services;
using Jellyfin.Plugin.JellyBridge.BridgeModels;
using Jellyfin.Plugin.JellyBridge.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyBridge.Controllers
{
    [ApiController]
    [Route("JellyBridge")]
    public class ImportDiscoverContentController : ControllerBase
    {
        private readonly DebugLogger<ImportDiscoverContentController> _logger;
        private readonly ApiService _apiService;
        private readonly SqliteProductionSyncService _sqliteProductionSyncService;
        private readonly SqliteDryRunService _sqliteDryRunService;
        private readonly ITaskManager _taskManager;

        public ImportDiscoverContentController(
            ILoggerFactory loggerFactory,
            ApiService apiService,
            SqliteProductionSyncService sqliteProductionSyncService,
            SqliteDryRunService sqliteDryRunService,
            ITaskManager taskManager)
        {
            _logger =
                new DebugLogger<ImportDiscoverContentController>(
                    loggerFactory.CreateLogger<ImportDiscoverContentController>());

            _apiService = apiService;
            _sqliteProductionSyncService = sqliteProductionSyncService;
            _sqliteDryRunService = sqliteDryRunService;
            _taskManager = taskManager;
        }

        [HttpGet("Regions")]
        public async Task<IActionResult> GetRegions()
        {
            _logger.LogInformation(
                "Regions requested from plugin configuration page.");

            try
            {
                var config = Plugin.GetConfiguration();

                var regions =
                    await _apiService.CallEndpointAsync(
                        JellyseerrEndpoint.WatchProvidersRegions,
                        config);

                var typedRegions =
                    regions as List<JellyseerrWatchProviderRegion>
                    ?? new List<JellyseerrWatchProviderRegion>();

                if (typedRegions.Count == 0)
                {
                    return NotFound(
                        new
                        {
                            success = false,
                            message =
                                "No regions returned from Jellyseerr API.",
                            regions = new List<object>(),
                            errorCode = "NO_REGIONS_FOUND"
                        });
                }

                return Ok(
                    new
                    {
                        success = true,
                        regions = typedRegions
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to get watch regions.");

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            $"Failed to get watch regions: {ex.Message}",
                        errorCode = "REGIONS_EXCEPTION"
                    });
            }
        }

        [HttpGet("Networks")]
        public async Task<IActionResult> GetNetworks(
            [FromQuery] string? region = null)
        {
            _logger.LogInformation(
                "Networks requested for region {Region}.",
                region);

            try
            {
                var config = Plugin.GetConfiguration();

                var targetRegion =
                    region
                    ?? Plugin.GetConfigOrDefault<string>(
                        nameof(PluginConfiguration.Region));

                config.Region = targetRegion;

                var movieNetworks =
                    (List<JellyseerrNetwork>)
                    await _apiService.CallEndpointAsync(
                        JellyseerrEndpoint.WatchProvidersMovies,
                        config);

                var showNetworks =
                    (List<JellyseerrNetwork>)
                    await _apiService.CallEndpointAsync(
                        JellyseerrEndpoint.WatchProvidersTv,
                        config);

                var combined =
                    new List<JellyseerrNetwork>();

                combined.AddRange(
                    movieNetworks
                    ?? new List<JellyseerrNetwork>());

                combined.AddRange(
                    showNetworks
                    ?? new List<JellyseerrNetwork>());

                if (combined.Count == 0)
                {
                    return StatusCode(
                        503,
                        new
                        {
                            success = false,
                            message =
                                "No networks returned from Jellyseerr API.",
                            errorCode = "NO_NETWORKS_FOUND"
                        });
                }

                combined.ForEach(
                    network =>
                        network.Country = targetRegion);

                return Ok(combined);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to get watch networks.");

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = ex.Message,
                        errorCode = "NETWORKS_EXCEPTION"
                    });
            }
        }

        /// <summary>
        /// SQLite-first manual Discover entry point.
        ///
        /// Stage 1 computes the desired catalog working set and SQLite
        /// incremental plan only. It deliberately performs no filesystem
        /// materialization yet.
        /// </summary>
        [HttpPost("QueueSyncDiscover")]
        public IActionResult QueueSyncDiscover()
        {
            if (!Plugin.GetConfigOrDefault<bool>(nameof(PluginConfiguration.IsEnabled)))
            {
                return StatusCode(409, new
                {
                    success = false,
                    queued = false,
                    message = "JellyBridge-SQLite is disabled. Enable the plugin before starting sync."
                });
            }

            var worker = _taskManager.ScheduledTasks.FirstOrDefault(
                task => task.ScheduledTask.Key == "JellyBridgeSync");

            if (worker is null)
            {
                return StatusCode(503, new
                {
                    success = false,
                    queued = false,
                    message = "JellyBridge SQLite scheduled task was not found."
                });
            }

            if (worker.State == TaskState.Running
                || worker.State == TaskState.Cancelling)
            {
                return Ok(new
                {
                    success = true,
                    queued = false,
                    alreadyRunning = true,
                    taskId = worker.Id,
                    movieTarget = Plugin.GetConfigOrDefault<int>(nameof(PluginConfiguration.DiscoverMovieTargetCount)),
                    seriesTarget = Plugin.GetConfigOrDefault<int>(nameof(PluginConfiguration.DiscoverSeriesTargetCount)),
                    message = "JellyBridge SQLite is already running."
                });
            }

            _taskManager.QueueScheduledTask<Tasks.SyncTask>();

            _logger.LogInformation(
                "SQLite-first Discover sync queued from plugin configuration UI.");

            return Accepted(new
            {
                success = true,
                queued = true,
                alreadyRunning = false,
                taskId = worker.Id,
                movieTarget = Plugin.GetConfigOrDefault<int>(nameof(PluginConfiguration.DiscoverMovieTargetCount)),
                seriesTarget = Plugin.GetConfigOrDefault<int>(nameof(PluginConfiguration.DiscoverSeriesTargetCount)),
                message = "JellyBridge SQLite sync queued in the background."
            });
        }
        [HttpGet("DryRunDiscover")]
        public async Task<IActionResult> DryRunDiscover()
        {
            _logger.LogInformation("SQLite-first Discover dry run requested.");

            if (!Plugin.GetConfigOrDefault<bool>(nameof(PluginConfiguration.IsEnabled)))
            {
                return StatusCode(409, new
                {
                    success = false,
                    sqliteFirst = true,
                    dryRun = true,
                    message = "JellyBridge-SQLite is disabled."
                });
            }

            try
            {
                var plan = await _sqliteDryRunService
                    .RunAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                return Ok(new
                {
                    success = true,
                    sqliteFirst = true,
                    dryRun = true,
                    filesystemWrites = false,
                    sqliteWrites = false,
                    tierMirror4K = true,
                    desiredMoviesPerTier = plan.DesiredMovies,
                    desiredSeriesPerTier = plan.DesiredSeries,
                    existingState = plan.ExistingState,
                    add = plan.AddCount,
                    update = plan.UpdateCount,
                    remove = plan.RemoveCount,
                    unchanged = plan.UnchangedCount,
                    totalMilliseconds = plan.TotalMilliseconds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SQLite-first Discover dry run failed.");
                return StatusCode(500, new
                {
                    success = false,
                    sqliteFirst = true,
                    dryRun = true,
                    message = $"SQLite Discover dry run failed: {ex.Message}"
                });
            }
        }

        [HttpPost("SyncDiscover")]
        public async Task<IActionResult> SyncDiscover()
        {
            _logger.LogInformation(
                "SQLite-first manual Discover production sync requested.");

            if (!Plugin.GetConfigOrDefault<bool>(nameof(PluginConfiguration.IsEnabled)))
            {
                return StatusCode(409, new
                {
                    success = false,
                    sqliteFirst = true,
                    message = "JellyBridge-SQLite is disabled. Enable the plugin before running a manual sync."
                });
            }

            try
            {
                var result =
                    await Plugin.ExecuteWithLockAsync(
                        async () =>
                        {
                            var plan =
                                await _sqliteProductionSyncService
                                    .RunAsync(
                                        progress: null,
                                        cancellationToken: HttpContext.RequestAborted)
                                    .ConfigureAwait(false);

                            return new
                            {
                                success = true,
                                sqliteFirst = true,
                                filesystemWrites = true,

                                message =
                                    "SQLite-first Discover synchronization completed.",

                                desiredMovies =
                                    plan.DesiredMovies,

                                desiredSeries =
                                    plan.DesiredSeries,

                                existingState =
                                    plan.ExistingState,

                                add =
                                    plan.AddCount,

                                update =
                                    plan.UpdateCount,

                                remove =
                                    plan.RemoveCount,

                                unchanged =
                                    plan.UnchangedCount,

                                totalMilliseconds =
                                    plan.TotalMilliseconds
                            };
                        },
                        _logger,
                        "JellyBridge SQLite Manual Sync");

                return Ok(result);
            }
            catch (TimeoutException)
            {
                var timeout =
                    Plugin.GetConfigOrDefault<int>(
                        nameof(
                            PluginConfiguration.TaskTimeoutMinutes));

                return StatusCode(
                    408,
                    new
                    {
                        success = false,
                        error = "Request timeout",
                        timeoutMinutes = timeout
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "SQLite-first Discover synchronization failed.");

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            $"SQLite Discover synchronization failed: {ex.Message}"
                    });
            }
        }
    }
}

