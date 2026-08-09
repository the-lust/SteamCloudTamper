namespace SteamCloudTamper.Core;

public static class Ferry
{
    public const uint SpacewarApp = 480;
    public const uint ScreenshotsApp = 760;
    public const uint SteamClientApp = 7;

    /// <summary>
    /// Prefix used when parking a foreign game's save under our owned AppID 480 bucket,
    /// so the original AppID is visible: "&lt;origAppId&gt;_&lt;originalName&gt;".
    /// </summary>
    public static string ParkName(uint originalAppId, string originalName)
        => $"{originalAppId}_{originalName}";

    public static (uint SourceAppId, string OriginalName) UnparkName(string parked)
    {
        var idx = parked.IndexOf('_');
        if (idx <= 0 || !uint.TryParse(parked[..idx], out var appId))
            return (0, parked);
        return (appId, parked[(idx + 1)..]);
    }
}