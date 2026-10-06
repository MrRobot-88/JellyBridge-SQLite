using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Production Jellyfin library integration for SQLite-first Discover output.
/// Validates/configures the four tiered root libraries and reports exact changed paths
/// through ILibraryMonitor. No global library scan is used.
/// </summary>
public sealed class SqliteLibrarySyncService
{
    private readonly JellyfinILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<SqliteLibrarySyncService> _logger;

    public SqliteLibrarySyncService(
        JellyfinILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<SqliteLibrarySyncService> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    public async Task EnsureLibrariesConfiguredAsync(CancellationToken cancellationToken)
    {
        var baseDirectory = FolderUtils.GetBaseDirectory();
        var moviesPath = Path.GetFullPath(Path.Combine(baseDirectory, "Movies"));
        var showsPath = Path.GetFullPath(Path.Combine(baseDirectory, "Shows"));
        var movies4kPath = Path.GetFullPath(Path.Combine(baseDirectory, "Movies 4K"));
        var shows4kPath = Path.GetFullPath(Path.Combine(baseDirectory, "Shows 4K"));

        Directory.CreateDirectory(moviesPath);
        Directory.CreateDirectory(showsPath);
        Directory.CreateDirectory(movies4kPath);
        Directory.CreateDirectory(shows4kPath);

        var virtualFolders = _libraryManager.Inner.GetVirtualFolders(true).ToList();

        if (virtualFolders.Any(folder => string.Equals(folder.Name, "Discover", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Unexpected parent library named 'Discover'. JellyBridge-SQLite requires separate Discover Movies and Discover Series root libraries.");
        }

        async Task ConfigureOneAsync(string libraryName, string expectedPath, string itemType)
        {
            var matches = virtualFolders
                .Where(folder => string.Equals(folder.Name, libraryName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one existing '{libraryName}' library; found {matches.Count}.");
            }

            var folderInfo = matches[0];
            var locations = folderInfo.Locations?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal)
                .ToList()
                ?? new List<string>();

            if (locations.Count != 1 || !string.Equals(locations[0], expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"'{libraryName}' must contain exactly '{expectedPath}'. Actual: {string.Join(", ", locations)}");
            }

            if (string.IsNullOrWhiteSpace(folderInfo.ItemId)
                || !Guid.TryParse(folderInfo.ItemId, out var libraryItemId))
            {
                throw new InvalidOperationException($"'{libraryName}' does not have a valid Jellyfin ItemId.");
            }

            var libraryFolder = _libraryManager.Inner.GetItemById(libraryItemId) as CollectionFolder
                ?? throw new InvalidOperationException($"Could not resolve '{libraryName}' as a Jellyfin CollectionFolder.");

            // Existing Discover library configuration is intentionally not rewritten here.
            // Step 4C only validates the library shell/path and manages the new 4K tier.
            var options = libraryFolder.GetLibraryOptions();
            if (options.EnableRealtimeMonitor
                || options.SaveLocalMetadata
                || options.MetadataSavers?.Length > 0
                || options.LocalMetadataReaderOrder is null
                || !options.LocalMetadataReaderOrder.Contains("Nfo", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{libraryName}' metadata options are not in the expected JellyBridge-safe state; refusing to rewrite them automatically.");
            }

            var physicalRoot = _libraryManager.Inner.FindByPath(expectedPath, isFolder: true);
            if (physicalRoot is null)
            {
                var rootIsEmpty = !Directory.EnumerateFileSystemEntries(expectedPath).Any();
                if (rootIsEmpty)
                {
                    _logger.LogInformation(
                        "SQLITE LIBRARY ROOT BOOTSTRAP DEFERRED | Library={Library} | Path={Path} | Reason=EmptyRoot | GlobalScan=NO",
                        libraryName,
                        expectedPath);
                }
                else
                {
                    await BootstrapPhysicalRootAsync(
                        libraryName,
                        expectedPath,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation(
                "SQLITE LIBRARY READY | Library={Library} | Path={Path} | Type={Type} | RemoteMetadata=OFF | RemoteImages=OFF | LocalNfo=ON | MetadataSavers=OFF | RealtimeMonitor=OFF",
                libraryName,
                expectedPath,
                itemType);
        }

        await ConfigureOneAsync("Discover Movies", moviesPath, "Movie").ConfigureAwait(false);
        await ConfigureOneAsync("Discover Series", showsPath, "Series").ConfigureAwait(false);
        await ConfigureOneAsync("Discover Movies 4K", movies4kPath, "Movie").ConfigureAwait(false);
        await ConfigureOneAsync("Discover Series 4K", shows4kPath, "Series").ConfigureAwait(false);
    }

    public async Task EnsurePhysicalRootReadyAsync(
        string mediaType,
        string tier,
        CancellationToken cancellationToken)
    {
        var basePath = Path.GetFullPath(FolderUtils.GetBaseDirectory());
        var is4k = string.Equals(tier, CatalogSelectionService.FourK, StringComparison.OrdinalIgnoreCase);
        if (!is4k)
        {
            return;
        }

        var libraryName = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? "Discover Series 4K"
            : "Discover Movies 4K";
        var root = Path.GetFullPath(Path.Combine(
            basePath,
            string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? "Shows 4K"
                : "Movies 4K"));

        if (_libraryManager.Inner.FindByPath(root, isFolder: true) is not null)
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException(
                $"Cannot bootstrap '{libraryName}' before its first materialized item exists.");
        }

        await BootstrapPhysicalRootAsync(libraryName, root, cancellationToken).ConfigureAwait(false);
    }

    private async Task BootstrapPhysicalRootAsync(
        string libraryName,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "SQLITE LIBRARY ROOT BOOTSTRAP | Library={Library} | Path={Path} | Method=ILibraryManager.ValidateTopLibraryFolders(recursive=false) | Scope=TopLevelRootsOnly | GlobalScan=NO",
            libraryName,
            expectedPath);

        await _libraryManager.Inner
            .ValidateTopLibraryFolders(cancellationToken, false)
            .ConfigureAwait(false);

        var physicalRoot = _libraryManager.Inner.FindByPath(expectedPath, isFolder: true);
        if (physicalRoot is not Folder physicalFolder)
        {
            throw new InvalidOperationException(
                $"Top-level Jellyfin validation did not create physical root '{expectedPath}' for '{libraryName}'.");
        }

        await physicalFolder.RefreshMetadata(cancellationToken).ConfigureAwait(false);
        await physicalFolder
            .ValidateChildren(new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "SQLITE LIBRARY ROOT BOOTSTRAP PASS | Library={Library} | Path={Path} | RootId={RootId} | RootType={RootType} | Method=TopLevelRootValidation+TargetedPhysicalValidate | GlobalScan=NO",
            libraryName,
            expectedPath,
            physicalFolder.Id,
            physicalFolder.GetType().FullName);
    }

    public void NotifyChangedPath(string mediaType, string tier, string path, string operation)
    {
        var fullPath = ValidateManagedPath(mediaType, tier, path);
        var notifyPath = ResolveNotificationPath(mediaType, fullPath);

        _logger.LogInformation(
            "SQLITE PATH NOTIFY | Operation={Operation} | Media={MediaType} | Tier={Tier} | ManagedPath={ManagedPath} | NotifyPath={NotifyPath} | Method=ILibraryMonitor.ReportFileSystemChanged | GlobalScan=NO",
            operation,
            mediaType,
            tier,
            fullPath,
            notifyPath);

        _libraryMonitor.ReportFileSystemChanged(notifyPath);
    }

    private static string ResolveNotificationPath(string mediaType, string managedPath)
    {
        if (!string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return managedPath;
        }

        return Path.Combine(managedPath, "Season 00", "S00E9999.mp4");
    }

    private static string ValidateManagedPath(string mediaType, string tier, string path)
    {
        var basePath = Path.GetFullPath(FolderUtils.GetBaseDirectory());
        var is4k = string.Equals(tier, CatalogSelectionService.FourK, StringComparison.OrdinalIgnoreCase);
        if (!is4k && !string.Equals(tier, CatalogSelectionService.DefaultTier, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown Discover tier '{tier}'.");
        }

        var root = Path.GetFullPath(Path.Combine(
            basePath,
            string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                ? (is4k ? "Shows 4K" : "Shows")
                : (is4k ? "Movies 4K" : "Movies")));
        var full = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing Jellyfin notify outside managed root: {full}");
        }

        return full;
    }
}
