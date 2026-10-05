using Jellyfin.Plugin.JellyBridge.JellyfinModels;
using Jellyfin.Plugin.JellyBridge.Utils;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// Production Jellyfin library integration for SQLite-first Discover output.
/// Validates/configures the two root libraries and reports exact changed paths
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

        Directory.CreateDirectory(moviesPath);
        Directory.CreateDirectory(showsPath);

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

            var options = libraryFolder.GetLibraryOptions();
            options.EnableRealtimeMonitor = false;
            options.SaveLocalMetadata = false;
            options.MetadataSavers = Array.Empty<string>();
            options.DisabledLocalMetadataReaders = Array.Empty<string>();
            options.LocalMetadataReaderOrder = ["Nfo"];

            var typeOptions = options.TypeOptions?
                .Where(option => !string.Equals(option.Type, itemType, StringComparison.OrdinalIgnoreCase))
                .ToList()
                ?? new List<TypeOptions>();

            typeOptions.Add(new TypeOptions
            {
                Type = itemType,
                MetadataFetchers = Array.Empty<string>(),
                MetadataFetcherOrder = Array.Empty<string>(),
                ImageFetchers = Array.Empty<string>(),
                ImageFetcherOrder = Array.Empty<string>()
            });

            options.TypeOptions = typeOptions.ToArray();
            libraryFolder.UpdateLibraryOptions(options);

            var physicalRoot = _libraryManager.Inner.FindByPath(expectedPath, isFolder: true);
            if (physicalRoot is null)
            {
                _logger.LogWarning(
                    "SQLITE LIBRARY ROOT BOOTSTRAP | Library={Library} | Path={Path} | Method=ILibraryManager.ValidateTopLibraryFolders(recursive=false) | Scope=TopLevelRootsOnly | GlobalScan=NO",
                    libraryName,
                    expectedPath);

                // Jellyfin creates physical library roots while validating only the
                // immediate children of its aggregate root. This is not a recursive
                // media-library scan; recursive=false stops at the top-level roots.
                await _libraryManager.Inner
                    .ValidateTopLibraryFolders(cancellationToken, false)
                    .ConfigureAwait(false);

                physicalRoot = _libraryManager.Inner.FindByPath(expectedPath, isFolder: true);
                if (physicalRoot is not Folder physicalFolder)
                {
                    throw new InvalidOperationException(
                        $"Top-level Jellyfin validation did not create physical root '{expectedPath}' for '{libraryName}'.");
                }

                // Bootstrap only this newly-created physical root so existing files
                // below it are discovered. All later changes use exact-path monitor
                // notifications and do not repeat this bootstrap.
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

            _logger.LogInformation(
                "SQLITE LIBRARY READY | Library={Library} | Path={Path} | Type={Type} | RemoteMetadata=OFF | RemoteImages=OFF | LocalNfo=ON | MetadataSavers=OFF | RealtimeMonitor=OFF",
                libraryName,
                expectedPath,
                itemType);
        }

        await ConfigureOneAsync("Discover Movies", moviesPath, "Movie").ConfigureAwait(false);
        await ConfigureOneAsync("Discover Series", showsPath, "Series").ConfigureAwait(false);
    }

    public void NotifyChangedPath(string mediaType, string path, string operation)
    {
        var fullPath = ValidateManagedPath(mediaType, path);
        var notifyPath = ResolveNotificationPath(mediaType, fullPath);

        _logger.LogInformation(
            "SQLITE PATH NOTIFY | Operation={Operation} | Media={MediaType} | ManagedPath={ManagedPath} | NotifyPath={NotifyPath} | Method=ILibraryMonitor.ReportFileSystemChanged | GlobalScan=NO",
            operation,
            mediaType,
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

    private static string ValidateManagedPath(string mediaType, string path)
    {
        var basePath = Path.GetFullPath(FolderUtils.GetBaseDirectory());
        var root = Path.GetFullPath(Path.Combine(
            basePath,
            string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "Shows" : "Movies"));
        var full = Path.GetFullPath(path);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing Jellyfin notify outside managed root: {full}");
        }

        return full;
    }
}
