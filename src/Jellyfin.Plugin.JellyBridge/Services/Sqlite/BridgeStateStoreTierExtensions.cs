namespace Jellyfin.Plugin.JellyBridge.Services.Sqlite;

/// <summary>
/// Tier-scoped state reads keep the 1080p and 4K sync plans independent while
/// both tiers share the same SQLite state database.
/// </summary>
public static class BridgeStateStoreTierExtensions
{
    public static async Task<IReadOnlyDictionary<BridgeItemKey, MaterializedItemState>>
        GetMaterializedItemsForTierAsync(
            this BridgeStateStore stateStore,
            string tier,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        var normalizedTier = BridgeTier.Normalize(tier);
        var all = await stateStore
            .GetMaterializedItemsAsync(cancellationToken)
            .ConfigureAwait(false);

        return all
            .Where(pair => string.Equals(
                BridgeTier.Normalize(pair.Value.Tier),
                normalizedTier,
                StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
