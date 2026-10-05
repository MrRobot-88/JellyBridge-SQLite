using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyBridge.Services.Catalog;
using Jellyfin.Plugin.JellyBridge.Services.Sqlite;
using Jellyfin.Plugin.JellyBridge.Utils;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services;

/// <summary>
/// SQLite-first filesystem materializer.
///
/// The UI ARR catalog provides desired metadata, jellybridge.db records what
/// this plugin owns, and the filesystem is derived output for Jellyfin.
/// No metadata.json state is read or written.
/// </summary>
public sealed class SqliteMaterializerService
{
    public const string MaterializedState = "sqlite-materialized";

    private readonly DiscoverPosterService _posterService;
    private readonly DiscoverCatalogClient _catalogClient;
    private readonly PlaceholderVideoGenerator _placeholderVideoGenerator;
    private readonly BridgeStateStore _stateStore;
    private readonly ILogger<SqliteMaterializerService> _logger;

    public SqliteMaterializerService(
        DiscoverPosterService posterService,
        DiscoverCatalogClient catalogClient,
        PlaceholderVideoGenerator placeholderVideoGenerator,
        BridgeStateStore stateStore,
        ILogger<SqliteMaterializerService> logger)
    {
        _posterService = posterService;
        _catalogClient = catalogClient;
        _placeholderVideoGenerator = placeholderVideoGenerator;
        _stateStore = stateStore;
        _logger = logger;
    }

    public async Task<SqliteMaterializationResult> ApplyAsync(
        DesiredCatalogItem item,
        long generation,
        MaterializedItemState? previous,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(item, previous);

        var targetPath = GetManagedPath(item.MediaType, item.TargetPath);
        ValidateFolderIdentity(targetPath, item.TmdbId);

        string? previousPath = null;
        if (previous is not null)
        {
            previousPath = GetManagedPath(previous.MediaType, previous.TargetPath);
            ValidateFolderIdentity(previousPath, previous.TmdbId);
        }

        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Cannot resolve parent path for {targetPath}.");

        Directory.CreateDirectory(parent);

        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var stagePath = Path.Combine(
            parent,
            $".jellybridge-stage-{item.MediaType}-{item.TmdbId}-{token}");
        var backupPath = Path.Combine(
            parent,
            $".jellybridge-backup-{item.MediaType}-{item.TmdbId}-{token}");

        if (Directory.Exists(stagePath) || Directory.Exists(backupPath))
        {
            throw new InvalidOperationException("Unexpected SQLite materializer staging collision.");
        }

        var targetExisted = Directory.Exists(targetPath);
        var targetMovedToBackup = false;
        var stageMovedToTarget = false;

        try
        {
            if (previous is null
                && targetExisted
                && Directory.EnumerateFileSystemEntries(targetPath).Any())
            {
                throw new InvalidOperationException(
                    $"Refusing ADD because target already contains files and has no SQLite ownership state: {targetPath}");
            }

            if (previous is not null
                && targetExisted
                && !string.Equals(previousPath, targetPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing UPDATE because new target already exists while previous target is different: {targetPath}");
            }

            Directory.CreateDirectory(stagePath);

            var nfoPath = await WriteNfoAsync(
                item,
                stagePath,
                cancellationToken).ConfigureAwait(false);

            var posterPath = await _posterService
                .EnsurePosterAsync(
                    item.CatalogItem,
                    stagePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var backdropPath = await _posterService
                .EnsureBackdropAsync(
                    item.CatalogItem,
                    stagePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var placeholderOk = string.Equals(
                    item.MediaType,
                    "tv",
                    StringComparison.OrdinalIgnoreCase)
                ? await _placeholderVideoGenerator
                    .GeneratePlaceholderSeasonAsync(stagePath)
                    .ConfigureAwait(false)
                : await _placeholderVideoGenerator
                    .GeneratePlaceholderMovieAsync(stagePath)
                    .ConfigureAwait(false);

            if (!placeholderOk)
            {
                throw new InvalidOperationException(
                    $"Placeholder generation failed for {item.MediaType} TMDB {item.TmdbId}.");
            }

            ValidateStage(item, stagePath, nfoPath, posterPath, backdropPath);

            if (Directory.Exists(targetPath))
            {
                Directory.Move(targetPath, backupPath);
                targetMovedToBackup = true;
            }

            Directory.Move(stagePath, targetPath);
            stageMovedToTarget = true;

            // If an update changed the canonical path, keep the old item until
            // the new tree is fully materialized, then remove only the path
            // already recorded as owned by SQLite.
            if (previous is not null
                && !string.Equals(previousPath, targetPath, StringComparison.Ordinal)
                && Directory.Exists(previousPath))
            {
                DeleteOwnedDirectory(previous);
            }

            if (targetMovedToBackup && Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
                targetMovedToBackup = false;
            }

            var now = DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);

            await _stateStore.UpsertMaterializedItemAsync(
                new MaterializedItemState(
                    item.MediaType,
                    item.TmdbId,
                    targetPath,
                    item.Fingerprint,
                    MaterializedState,
                    generation,
                    previous?.FirstSeenUtc ?? now,
                    now,
                    now),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "SQLITE MATERIALIZED | Media={MediaType} | TMDB={TmdbId} | Generation={Generation} | Previous={Previous} | Path={Path}",
                item.MediaType,
                item.TmdbId,
                generation,
                previous is null ? "NO" : "YES",
                targetPath);

            return new SqliteMaterializationResult(
                item.MediaType,
                item.TmdbId,
                targetPath,
                previousPath,
                previous is null ? "add" : "update");
        }
        catch
        {
            if (stageMovedToTarget && Directory.Exists(targetPath))
            {
                try
                {
                    Directory.Delete(targetPath, recursive: true);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "SQLite materializer rollback could not delete new target {TargetPath}",
                        targetPath);
                }
            }

            if (targetMovedToBackup
                && Directory.Exists(backupPath)
                && !Directory.Exists(targetPath))
            {
                try
                {
                    Directory.Move(backupPath, targetPath);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "SQLite materializer rollback could not restore {BackupPath} to {TargetPath}",
                        backupPath,
                        targetPath);
                }
            }

            if (Directory.Exists(stagePath))
            {
                try
                {
                    Directory.Delete(stagePath, recursive: true);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "SQLite materializer rollback could not delete staging path {StagePath}",
                        stagePath);
                }
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagePath))
            {
                try
                {
                    Directory.Delete(stagePath, recursive: true);
                }
                catch
                {
                    // The primary exception/result is more important than best-effort cleanup.
                }
            }
        }
    }

    public async Task<SqliteRemovalResult> RemoveAsync(
        MaterializedItemState state,
        CancellationToken cancellationToken = default)
    {
        var targetPath = GetManagedPath(state.MediaType, state.TargetPath);
        ValidateFolderIdentity(targetPath, state.TmdbId);
        ValidateOwnedState(state);

        if (!Directory.Exists(targetPath))
        {
            await _stateStore.DeleteMaterializedItemAsync(
                new BridgeItemKey(state.MediaType, state.TmdbId),
                cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "SQLITE REMOVE reconciled missing path | Media={MediaType} | TMDB={TmdbId} | Path={Path}",
                state.MediaType,
                state.TmdbId,
                targetPath);

            return new SqliteRemovalResult(
                state.MediaType,
                state.TmdbId,
                targetPath,
                false);
        }

        ValidateOwnedDirectory(state, targetPath);

        var parent = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException($"Cannot resolve parent path for {targetPath}.");

        var tombstone = Path.Combine(
            parent,
            $".jellybridge-delete-{state.MediaType}-{state.TmdbId}-{Guid.NewGuid():N}");

        Directory.Move(targetPath, tombstone);

        try
        {
            Directory.Delete(tombstone, recursive: true);

            await _stateStore.DeleteMaterializedItemAsync(
                new BridgeItemKey(state.MediaType, state.TmdbId),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(tombstone) && !Directory.Exists(targetPath))
            {
                try
                {
                    Directory.Move(tombstone, targetPath);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(
                        rollbackException,
                        "SQLite remove rollback could not restore {Tombstone} to {TargetPath}",
                        tombstone,
                        targetPath);
                }
            }

            throw;
        }

        _logger.LogInformation(
            "SQLITE REMOVED | Media={MediaType} | TMDB={TmdbId} | Path={Path}",
            state.MediaType,
            state.TmdbId,
            targetPath);

        return new SqliteRemovalResult(
            state.MediaType,
            state.TmdbId,
            targetPath,
            true);
    }

    private static void ValidateIdentity(
        DesiredCatalogItem item,
        MaterializedItemState? previous)
    {
        if (item.TmdbId <= 0)
        {
            throw new InvalidOperationException("Cannot materialize an item without a valid TMDB id.");
        }

        if (previous is null)
        {
            return;
        }

        if (!string.Equals(item.MediaType, previous.MediaType, StringComparison.Ordinal)
            || item.TmdbId != previous.TmdbId)
        {
            throw new InvalidOperationException("SQLite update identity does not match existing state.");
        }

        ValidateOwnedState(previous);
    }

    private static void ValidateOwnedState(MaterializedItemState state)
    {
        if (!string.Equals(state.MaterializationState, MaterializedState, StringComparison.Ordinal)
            && !string.Equals(state.MaterializationState, "pilot-materialized", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing filesystem mutation for unrecognized ownership state '{state.MaterializationState}'.");
        }
    }

    private static string GetManagedPath(string mediaType, string path)
    {
        var basePath = Path.GetFullPath(FolderUtils.GetBaseDirectory());
        var mediaRoot = Path.GetFullPath(
            Path.Combine(
                basePath,
                string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    ? "Shows"
                    : "Movies"));

        var fullPath = Path.GetFullPath(path);
        var prefix = mediaRoot.EndsWith(Path.DirectorySeparatorChar)
            ? mediaRoot
            : mediaRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing filesystem mutation outside managed {mediaType} root: {fullPath}");
        }

        return fullPath;
    }

    private static void ValidateFolderIdentity(string path, long tmdbId)
    {
        var expected = $"[tmdbid-{tmdbId}]";
        if (!Path.GetFileName(path).Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Managed path does not contain expected TMDB identity {expected}: {path}");
        }
    }

    private static void ValidateOwnedDirectory(
        MaterializedItemState state,
        string targetPath)
    {
        var nfoName = string.Equals(state.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? "tvshow.nfo"
            : "movie.nfo";
        var nfoPath = Path.Combine(targetPath, nfoName);

        if (!File.Exists(nfoPath))
        {
            throw new InvalidOperationException(
                $"Refusing REMOVE because expected owned NFO is missing: {nfoPath}");
        }

        var document = XDocument.Load(nfoPath);
        var ids = document
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Name.LocalName, "tmdbid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.Name.LocalName, "uniqueid", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim());

        if (!ids.Contains(state.TmdbId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing REMOVE because NFO identity does not match TMDB {state.TmdbId}: {nfoPath}");
        }
    }

    private static void DeleteOwnedDirectory(MaterializedItemState state)
    {
        var path = GetManagedPath(state.MediaType, state.TargetPath);
        ValidateFolderIdentity(path, state.TmdbId);
        ValidateOwnedState(state);
        ValidateOwnedDirectory(state, path);
        Directory.Delete(path, recursive: true);
    }

    private async Task<string> WriteNfoAsync(
        DesiredCatalogItem item,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var isTv = string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase);
        var nfoName = isTv ? "tvshow.nfo" : "movie.nfo";
        var nfoPath = Path.Combine(targetDirectory, nfoName);
        var rootName = isTv ? "tvshow" : "movie";
        var catalog = item.CatalogItem;

        var trailer = await _catalogClient
            .GetTrailerAsync(
                item.MediaType,
                item.TmdbId,
                cancellationToken)
            .ConfigureAwait(false);

        var trailerUrl = trailer?.HasTrailer == true
            && !string.IsNullOrWhiteSpace(trailer.YoutubeKey)
            ? $"plugin://plugin.video.youtube/play/?video_id={trailer.YoutubeKey}"
            : null;

        var root = new XElement(
            rootName,
            new XElement("id", catalog.TmdbId),
            new XElement(
                "uniqueid",
                new XAttribute("type", "tmdb"),
                new XAttribute("default", "true"),
                catalog.TmdbId),
            new XElement("tmdbid", catalog.TmdbId),
            new XElement("title", catalog.Title ?? string.Empty),
            new XElement("originaltitle", catalog.OriginalTitle ?? string.Empty),
            catalog.Year.HasValue
                ? new XElement("year", catalog.Year.Value)
                : null,
            !string.IsNullOrWhiteSpace(catalog.ReleaseDate)
                ? new XElement("premiered", catalog.ReleaseDate)
                : null,
            !string.IsNullOrWhiteSpace(catalog.Overview)
                ? new XElement("plot", catalog.Overview)
                : null,
            catalog.VoteAverage.HasValue
                ? new XElement(
                    "rating",
                    catalog.VoteAverage.Value.ToString(
                        "0.0###",
                        CultureInfo.InvariantCulture))
                : null,
            catalog.GenreNames
                .Where(static genre => !string.IsNullOrWhiteSpace(genre))
                .Select(static genre => new XElement("genre", genre)),
            catalog.OriginCountries
                .Where(static country => !string.IsNullOrWhiteSpace(country))
                .Select(static country => new XElement("country", country)),
            !string.IsNullOrWhiteSpace(trailerUrl)
                ? new XElement("trailer", trailerUrl)
                : null,
            new XElement("watched", "false"));

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            root);

        var tempPath = nfoPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                document.ToString(),
                cancellationToken).ConfigureAwait(false);

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"Generated {nfoName} is empty for TMDB {item.TmdbId}.");
            }

            File.Move(tempPath, nfoPath, overwrite: true);
            return nfoPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ValidateStage(
        DesiredCatalogItem item,
        string stagePath,
        string nfoPath,
        string posterPath,
        string? backdropPath)
    {
        if (!File.Exists(nfoPath) || new FileInfo(nfoPath).Length == 0)
        {
            throw new InvalidOperationException($"Materialized NFO is missing for TMDB {item.TmdbId}.");
        }

        if (!File.Exists(posterPath) || new FileInfo(posterPath).Length == 0)
        {
            throw new InvalidOperationException($"Materialized poster is missing for TMDB {item.TmdbId}.");
        }

        if (backdropPath is not null
            && (!File.Exists(backdropPath) || new FileInfo(backdropPath).Length == 0))
        {
            throw new InvalidOperationException($"Materialized backdrop is missing for TMDB {item.TmdbId}.");
        }

        var placeholders = Directory
            .EnumerateFiles(stagePath, "*.mp4", SearchOption.AllDirectories)
            .ToArray();

        if (placeholders.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one placeholder MP4 for TMDB {item.TmdbId}; found {placeholders.Length}.");
        }

        if (Directory.EnumerateFiles(
                stagePath,
                "metadata.json",
                SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException(
                $"SQLite materializer unexpectedly produced legacy metadata.json for TMDB {item.TmdbId}.");
        }
    }
}

public sealed record SqliteMaterializationResult(
    string MediaType,
    long TmdbId,
    string TargetPath,
    string? PreviousPath,
    string Operation);

public sealed record SqliteRemovalResult(
    string MediaType,
    long TmdbId,
    string TargetPath,
    bool DirectoryDeleted);
