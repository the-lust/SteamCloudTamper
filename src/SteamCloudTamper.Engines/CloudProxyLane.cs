using SteamCloudTamper.Core;
using SteamKit2;

namespace SteamCloudTamper.Engines;

/// <summary>
/// The proxy lane: every Cloud.* call for an unowned game goes out with the
/// proxy appid and a "sls-&lt;game&gt;/" filename prefix, so the server only ever
/// sees a bucket the account owns. Callers see logical (prefix-stripped) names.
/// The Steam client never learns these files exist - no AutoCloud tick needed,
/// unlike the 480 client lane. Needs a real session (anonymous uploads are
/// denied server-side even for owned buckets).
/// </summary>
public sealed class CloudProxyLane(CloudRpcClient rpc, uint gameAppId, uint proxyAppId)
{
    public static string Prefix(uint game) => CloudProxy.Prefix(game);

    public async Task<List<CloudFileEntry>> EnumerateAsync(CancellationToken ct = default)
    {
        // the whole proxy bucket comes back - keep only this game's namespace
        return (await rpc.EnumerateAsync(proxyAppId, ct))
            .Where(f => f.FileName.StartsWith(CloudProxy.Prefix(gameAppId), StringComparison.Ordinal))
            .Select(f => f with { FileName = f.FileName[CloudProxy.Prefix(gameAppId).Length..] })
            .ToList();
    }

    public Task<EResult> UploadAsync(string logicalName, byte[] data, CancellationToken ct = default)
        => rpc.UploadAsync(proxyAppId, CloudProxy.Apply(gameAppId, logicalName), data, ct);

    public Task<(bool Ok, EResult Result)> DeleteAsync(string logicalName, CancellationToken ct = default)
        => rpc.DeleteAsync(proxyAppId, CloudProxy.Apply(gameAppId, logicalName), ct);

    public Task<byte[]?> DownloadAsync(string logicalName, CancellationToken ct = default)
        => rpc.DownloadAsync(proxyAppId, CloudProxy.Apply(gameAppId, logicalName), ct);

    public Task<Quota> QuotaAsync(CancellationToken ct = default)
        => rpc.QuotaAsync(proxyAppId, ct);

    public uint GameAppId => gameAppId;
    public uint ProxyAppId => proxyAppId;
}