using Jellyfin.Plugin.JellyBridge.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyBridge.Services.Sqlite;

/// <summary>
/// SQLite-backed operational state for JellyBridge-SQLite.
///
/// This database is intentionally small. It tracks what JellyBridge-SQLite has
/// materialized for Jellyfin; it is not a copy of the UI ARR Discover catalog.
/// </summary>
public sealed class BridgeStateStore
{
    private const int SchemaVersion = 2;

    private readonly ILogger<BridgeStateStore> _logger;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private volatile bool _initialized;

    public BridgeStateStore(ILogger<BridgeStateStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the local state database path.
    /// </summary>
    public string DatabasePath
    {
        get
        {
            var baseDirectory = FolderUtils.GetBaseDirectory();
            return Path.Combine(baseDirectory, ".jellybridge-sqlite", "jellybridge.db");
        }
    }

    /// <summary>
    /// Creates the SQLite state database and migrates older SQLite-first state
    /// in-place when needed. There is deliberately no migration/import from
    /// legacy metadata.json state.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized)
            {
                return;
            }

            var databasePath = DatabasePath;
            var directory = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("Unable to resolve JellyBridge-SQLite state directory.");

            Directory.CreateDirectory(directory);

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                "PRAGMA journal_mode=WAL;",
                cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                "PRAGMA synchronous=NORMAL;",
                cancellationToken).ConfigureAwait(false);

            var previousVersion = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA user_version;",
                cancellationToken).ConfigureAwait(false);

            if (previousVersion > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"JellyBridge-SQLite state schema {previousVersion} is newer than supported schema {SchemaVersion}.");
            }

            // Tables whose shape is unchanged between v1 and v2.
            await ExecuteNonQueryAsync(
                connection,
                """
                CREATE TABLE IF NOT EXISTS bridge_meta (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sync_runs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    generation INTEGER NOT NULL,
                    started_utc TEXT NOT NULL,
                    completed_utc TEXT NULL,
                    desired_movies INTEGER NOT NULL DEFAULT 0,
                    desired_series INTEGER NOT NULL DEFAULT 0,
                    add_count INTEGER NOT NULL DEFAULT 0,
                    update_count INTEGER NOT NULL DEFAULT 0,
                    remove_count INTEGER NOT NULL DEFAULT 0,
                    unchanged_count INTEGER NOT NULL DEFAULT 0,
                    status TEXT NOT NULL,
                    error TEXT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_sync_runs_generation
                    ON sync_runs(generation);
                """,
                cancellationToken).ConfigureAwait(false);

            var hasMaterializedTable = await TableExistsAsync(
                connection,
                "materialized_items",
                cancellationToken).ConfigureAwait(false);

            if (!hasMaterializedTable)
            {
                await CreateMaterializedItemsV2Async(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!await TableHasColumnAsync(
                         connection,
                         "materialized_items",
                         "tier",
                         cancellationToken).ConfigureAwait(false))
            {
                await MigrateMaterializedItemsV1ToV2Async(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EnsureMaterializedIndexesAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA user_version={SchemaVersion};",
                cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                """
                INSERT INTO bridge_meta(key, value)
                VALUES('state_model', 'sqlite-first-v2-tiered')
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """,
                cancellationToken).ConfigureAwait(false);

            _initialized = true;

            _logger.LogInformation(
                "JellyBridge-SQLite state database ready at {DatabasePath} (schema {SchemaVersion}, previous={PreviousVersion})",
                databasePath,
                SchemaVersion,
                previousVersion);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    /// <summary>
    /// Reads all materialized state rows from SQLite.
    /// This replaces reconstructing plugin state by scanning metadata.json files.
    /// </summary>
    public async Task<IReadOnlyDictionary<BridgeItemKey, MaterializedItemState>> GetMaterializedItemsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<BridgeItemKey, MaterializedItemState>();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                media_type,
                tmdb_id,
                target_path,
                fingerprint,
                materialization_state,
                generation,
                first_seen_utc,
                last_seen_utc,
                last_materialized_utc,
                tier
            FROM materialized_items;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = new MaterializedItemState(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                BridgeTier.Normalize(reader.GetString(9)));

            result[new BridgeItemKey(state.MediaType, state.TmdbId, state.Tier)] = state;
        }

        return result;
    }

    /// <summary>
    /// Upserts one materialized state row.
    /// Filesystem output is derived from desired catalog state; SQLite remains
    /// the operational source of truth for what this plugin materialized.
    /// </summary>
    public async Task UpsertMaterializedItemAsync(
        MaterializedItemState item,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var tier = BridgeTier.Normalize(item.Tier);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO materialized_items (
                media_type,
                tmdb_id,
                tier,
                target_path,
                fingerprint,
                materialization_state,
                generation,
                first_seen_utc,
                last_seen_utc,
                last_materialized_utc
            )
            VALUES (
                $media_type,
                $tmdb_id,
                $tier,
                $target_path,
                $fingerprint,
                $materialization_state,
                $generation,
                $first_seen_utc,
                $last_seen_utc,
                $last_materialized_utc
            )
            ON CONFLICT(media_type, tmdb_id, tier) DO UPDATE SET
                target_path=excluded.target_path,
                fingerprint=excluded.fingerprint,
                materialization_state=excluded.materialization_state,
                generation=excluded.generation,
                last_seen_utc=excluded.last_seen_utc,
                last_materialized_utc=excluded.last_materialized_utc;
            """;

        command.Parameters.AddWithValue("$media_type", item.MediaType);
        command.Parameters.AddWithValue("$tmdb_id", item.TmdbId);
        command.Parameters.AddWithValue("$tier", tier);
        command.Parameters.AddWithValue("$target_path", item.TargetPath);
        command.Parameters.AddWithValue("$fingerprint", item.Fingerprint);
        command.Parameters.AddWithValue("$materialization_state", item.MaterializationState);
        command.Parameters.AddWithValue("$generation", item.Generation);
        command.Parameters.AddWithValue("$first_seen_utc", item.FirstSeenUtc);
        command.Parameters.AddWithValue("$last_seen_utc", item.LastSeenUtc);
        command.Parameters.AddWithValue(
            "$last_materialized_utc",
            item.LastMaterializedUtc is null ? DBNull.Value : item.LastMaterializedUtc);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMaterializedItemAsync(
        BridgeItemKey key,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            DELETE FROM materialized_items
            WHERE media_type=$media_type
              AND tmdb_id=$tmdb_id
              AND tier=$tier;
            """;

        command.Parameters.AddWithValue("$media_type", key.MediaType);
        command.Parameters.AddWithValue("$tmdb_id", key.TmdbId);
        command.Parameters.AddWithValue("$tier", BridgeTier.Normalize(key.Tier));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private async Task MigrateMaterializedItemsV1ToV2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();

        try
        {
            var beforeCount = await ExecuteScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM materialized_items;",
                cancellationToken,
                transaction).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                """
                DROP TABLE IF EXISTS materialized_items_v2;

                CREATE TABLE materialized_items_v2 (
                    media_type TEXT NOT NULL,
                    tmdb_id INTEGER NOT NULL,
                    tier TEXT NOT NULL CHECK (tier IN ('1080p', '4k')),
                    target_path TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    materialization_state TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    last_materialized_utc TEXT NULL,
                    PRIMARY KEY (media_type, tmdb_id, tier)
                );

                INSERT INTO materialized_items_v2 (
                    media_type,
                    tmdb_id,
                    tier,
                    target_path,
                    fingerprint,
                    materialization_state,
                    generation,
                    first_seen_utc,
                    last_seen_utc,
                    last_materialized_utc
                )
                SELECT
                    media_type,
                    tmdb_id,
                    '1080p',
                    target_path,
                    fingerprint,
                    materialization_state,
                    generation,
                    first_seen_utc,
                    last_seen_utc,
                    last_materialized_utc
                FROM materialized_items;
                """,
                cancellationToken,
                transaction).ConfigureAwait(false);

            var afterCount = await ExecuteScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM materialized_items_v2;",
                cancellationToken,
                transaction).ConfigureAwait(false);

            if (beforeCount != afterCount)
            {
                throw new InvalidOperationException(
                    $"SQLite v1->v2 tier migration row-count mismatch: before={beforeCount}, after={afterCount}.");
            }

            await ExecuteNonQueryAsync(
                connection,
                """
                DROP TABLE materialized_items;
                ALTER TABLE materialized_items_v2 RENAME TO materialized_items;

                CREATE INDEX idx_materialized_items_generation
                    ON materialized_items(generation);

                CREATE INDEX idx_materialized_items_state
                    ON materialized_items(materialization_state);

                CREATE INDEX idx_materialized_items_tier
                    ON materialized_items(tier);
                """,
                cancellationToken,
                transaction).ConfigureAwait(false);

            transaction.Commit();

            _logger.LogInformation(
                "JellyBridge-SQLite state migration v1->v2 complete: rows={RowCount}, defaultTier={Tier}",
                afterCount,
                BridgeTier.FullHd);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static async Task CreateMaterializedItemsV2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE materialized_items (
                media_type TEXT NOT NULL,
                tmdb_id INTEGER NOT NULL,
                tier TEXT NOT NULL CHECK (tier IN ('1080p', '4k')),
                target_path TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                materialization_state TEXT NOT NULL,
                generation INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_materialized_utc TEXT NULL,
                PRIMARY KEY (media_type, tmdb_id, tier)
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await EnsureMaterializedIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMaterializedIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE INDEX IF NOT EXISTS idx_materialized_items_generation
                ON materialized_items(generation);

            CREATE INDEX IF NOT EXISTS idx_materialized_items_state
                ON materialized_items(materialization_state);

            CREATE INDEX IF NOT EXISTS idx_materialized_items_tier
                ON materialized_items(tier);
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value ?? 0) > 0;
    }

    private static async Task<bool> TableHasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(tableName, "materialized_items", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported SQLite schema inspection table '{tableName}'.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(materialized_items);";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value ?? 0);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public readonly record struct BridgeItemKey(
    string MediaType,
    long TmdbId,
    string Tier = BridgeTier.FullHd);

public sealed record MaterializedItemState(
    string MediaType,
    long TmdbId,
    string TargetPath,
    string Fingerprint,
    string MaterializationState,
    long Generation,
    string FirstSeenUtc,
    string LastSeenUtc,
    string? LastMaterializedUtc,
    string Tier = BridgeTier.FullHd);
