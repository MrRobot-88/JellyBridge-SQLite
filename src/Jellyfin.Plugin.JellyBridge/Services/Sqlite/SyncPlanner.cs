namespace Jellyfin.Plugin.JellyBridge.Services.Sqlite;

/// <summary>
/// Computes an O(n) sync plan from the desired catalog working set and the
/// SQLite materialization state. No filesystem scan is required.
/// </summary>
public sealed class SyncPlanner
{
    public SyncPlan Build(
        IReadOnlyCollection<DesiredBridgeItem> desiredItems,
        IReadOnlyDictionary<BridgeItemKey, MaterializedItemState> materializedItems)
    {
        var desiredByKey = desiredItems.ToDictionary(
            item => new BridgeItemKey(item.MediaType, item.TmdbId, item.Tier));

        var adds = new List<DesiredBridgeItem>();
        var updates = new List<DesiredBridgeItem>();
        var unchanged = new List<DesiredBridgeItem>();
        var removes = new List<MaterializedItemState>();

        foreach (var pair in desiredByKey)
        {
            if (!materializedItems.TryGetValue(pair.Key, out var current))
            {
                adds.Add(pair.Value);
                continue;
            }

            if (!string.Equals(
                    current.Fingerprint,
                    pair.Value.Fingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    current.TargetPath,
                    pair.Value.TargetPath,
                    StringComparison.Ordinal))
            {
                updates.Add(pair.Value);
            }
            else
            {
                unchanged.Add(pair.Value);
            }
        }

        foreach (var pair in materializedItems)
        {
            if (!desiredByKey.ContainsKey(pair.Key))
            {
                removes.Add(pair.Value);
            }
        }

        return new SyncPlan(adds, updates, removes, unchanged);
    }
}

public sealed record DesiredBridgeItem(
    string MediaType,
    long TmdbId,
    string TargetPath,
    string Fingerprint,
    string Tier = BridgeTier.FullHd);

public sealed record SyncPlan(
    IReadOnlyList<DesiredBridgeItem> Adds,
    IReadOnlyList<DesiredBridgeItem> Updates,
    IReadOnlyList<MaterializedItemState> Removes,
    IReadOnlyList<DesiredBridgeItem> Unchanged)
{
    public int ChangeCount => Adds.Count + Updates.Count + Removes.Count;
}
