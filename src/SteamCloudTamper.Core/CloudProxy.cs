namespace SteamCloudTamper.Core;

/// <summary>
/// The appid proxy (Ace SLS's trick, adapted - see docs/APPID-PROXY.md):
/// unowned-game saves ride inside a bucket you DO own, namespaced by
/// "sls-&lt;game&gt;/" path prefixes. SCT does this in the request fields it
/// controls (no client hook needed - the rpc lane writes the proxy appid
/// itself); the Steam client never sees the unowned appid, so the april 2025
/// AccessDenied wall never triggers.
/// </summary>
public static class CloudProxy
{
    public const string PrefixMagic = "sls-";

    public static string Prefix(uint gameAppId) => $"{PrefixMagic}{gameAppId}/";

    public static string Apply(uint gameAppId, string name) => Prefix(gameAppId) + name;

    /// <summary>True when the name carries a proxy prefix. Returns the game appid it belongs to.</summary>
    public static bool TryStrip(string name, out uint gameAppId, out string stripped)
    {
        gameAppId = 0;
        stripped = name;
        if (!name.StartsWith(PrefixMagic, StringComparison.Ordinal))
            return false;

        var slash = name.IndexOf('/', PrefixMagic.Length);
        if (slash <= PrefixMagic.Length)
            return false;

        if (!uint.TryParse(name.AsSpan(PrefixMagic.Length, slash - PrefixMagic.Length), out gameAppId))
            return false;

        stripped = name[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// The changelist-filter lesson from the SLS patch: when several games share
    /// one bucket you MUST drop foreign paths, or the client/downloader starts
    /// pulling other games' saves. Names come back stripped of the prefix.
    /// </summary>
    public static IEnumerable<T> FilterToGame<T>(
        IEnumerable<T> entries,
        uint gameAppId,
        Func<T, string> nameOf,
        Func<T, string, T> stripped)
    {
        var prefix = Prefix(gameAppId);
        foreach (var e in entries)
        {
            var name = nameOf(e);
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue; // foreign game's save - not ours
            yield return stripped(e, name[prefix.Length..]);
        }
    }
}