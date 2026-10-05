namespace Jellyfin.Plugin.JellyBridge.Services.Sqlite;

/// <summary>
/// Quality tiers used as part of JellyBridge-SQLite materialization identity.
/// </summary>
public static class BridgeTier
{
    public const string FullHd = "1080p";
    public const string UltraHd = "4k";

    public static string Normalize(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)
            || string.Equals(tier, FullHd, StringComparison.OrdinalIgnoreCase))
        {
            return FullHd;
        }

        if (string.Equals(tier, UltraHd, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tier, "2160p", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tier, "uhd", StringComparison.OrdinalIgnoreCase))
        {
            return UltraHd;
        }

        throw new InvalidOperationException($"Unsupported JellyBridge quality tier '{tier}'.");
    }

    public static bool Is4k(string? tier) =>
        string.Equals(Normalize(tier), UltraHd, StringComparison.Ordinal);
}
