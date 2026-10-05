using Jellyfin.Plugin.JellyBridge.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Controllers
{
    [ApiController]
    [Route("JellyBridge")]
    public class AdvancedSettingsController : ControllerBase
    {
        private readonly DebugLogger<AdvancedSettingsController> _logger;

        public AdvancedSettingsController(
            ILoggerFactory loggerFactory)
        {
            _logger =
                new DebugLogger<AdvancedSettingsController>(
                    loggerFactory.CreateLogger<AdvancedSettingsController>());
        }

        /// <summary>
        /// Legacy metadata.json cleanup is deliberately disabled.
        ///
        /// JellyBridge-SQLite owns state through jellybridge.db and must
        /// never delete a materialized directory simply because
        /// metadata.json is absent.
        /// </summary>
        [HttpPost("CleanupMetadata")]
        public IActionResult CleanupMetadata()
        {
            _logger.LogWarning(
                "Legacy CleanupMetadata endpoint blocked by JellyBridge-SQLite.");

            return StatusCode(
                409,
                new
                {
                    success = false,
                    sqliteFirst = true,
                    legacyOperationDisabled = true,
                    message =
                        "Legacy metadata.json cleanup is disabled in JellyBridge-SQLite."
                });
        }

        /// <summary>
        /// Legacy recycle deletes the complete JellyBridge tree.
        /// It is disabled until an SQLite-state-aware replacement exists.
        /// </summary>
        [HttpPost("RecycleLibrary")]
        public IActionResult RecycleLibrary()
        {
            _logger.LogWarning(
                "Legacy RecycleLibrary endpoint blocked by JellyBridge-SQLite.");

            return StatusCode(
                409,
                new
                {
                    success = false,
                    sqliteFirst = true,
                    legacyOperationDisabled = true,
                    message =
                        "Legacy filesystem recycle is disabled in JellyBridge-SQLite."
                });
        }
    }
}
