using SteamCloudTamper.Core;

namespace SteamCloudTamper.Engines;

public sealed class WipeEngine(AppConfig config)
{
    public event Action<string>? Log;

    public async Task<WipeOutcome> WipeAsync(
        CloudRpcClient rpc,
        uint appId,
        string fileName,
        bool blankInsteadOfDelete,
        byte[]? blankTemplate = null,
        CancellationToken ct = default)
    {
        var forced = Environment.GetEnvironmentVariable("SCT_FORCE")?.Equals("1", StringComparison.OrdinalIgnoreCase) == true;
        if (config.DryRun && !forced)
        {
            var dry = blankInsteadOfDelete ? "blank" : "delete";
            Log?.Invoke($"[dry-run] {appId}/{fileName}: would {dry}");
            return new WipeOutcome(appId.ToString(), fileName, dry, "dry-run (no-op)", true);
        }

        if (blankInsteadOfDelete)
        {
            var data = BlankSaveKit.CreateBlank(fileName, blankTemplate);
            var res = await rpc.UploadAsync(appId, fileName, data, ct);
            Log?.Invoke($"[{appId}/{fileName}] blank overwrite -> {res}");
            return new WipeOutcome(appId.ToString(), fileName, "blank", res.ToString(), res == SteamKit2.EResult.OK);
        }

        var (ok, eresult) = await rpc.DeleteAsync(appId, fileName, ct);
        Log?.Invoke($"[{appId}/{fileName}] delete -> {eresult}");
        if (ok)
        {
            config.GuardedAppIds.Add(appId);
        }

        return new WipeOutcome(appId.ToString(), fileName, "delete", eresult.ToString(), ok);
    }

    public async Task<WipeOutcome> WipeAsync(
        CloudProxyLane lane,
        string fileName,
        bool blankInsteadOfDelete,
        byte[]? blankTemplate = null,
        CancellationToken ct = default)
    {
        var wireName = CloudProxy.Apply(lane.GameAppId, fileName);
        var forced = Environment.GetEnvironmentVariable("SCT_FORCE")?.Equals("1", StringComparison.OrdinalIgnoreCase) == true;
        if (config.DryRun && !forced)
        {
            var dry = blankInsteadOfDelete ? "blank" : "delete";
            Log?.Invoke($"[dry-run] {lane.ProxyAppId}/{wireName}: would {dry}");
            return new WipeOutcome(lane.ProxyAppId.ToString(), wireName, dry, "dry-run (no-op)", true);
        }

        if (blankInsteadOfDelete)
        {
            var data = BlankSaveKit.CreateBlank(fileName, blankTemplate);
            var res = await lane.UploadAsync(fileName, data, ct);
            Log?.Invoke($"[{lane.ProxyAppId}/{wireName}] blank overwrite -> {res}");
            return new WipeOutcome(lane.ProxyAppId.ToString(), wireName, "blank", res.ToString(), res == SteamKit2.EResult.OK);
        }

        var (ok, eresult) = await lane.DeleteAsync(fileName, ct);
        Log?.Invoke($"[{lane.ProxyAppId}/{wireName}] delete -> {eresult}");
        if (ok)
        {
            config.GuardedAppIds.Add(lane.ProxyAppId);
        }

        return new WipeOutcome(lane.ProxyAppId.ToString(), wireName, "delete", eresult.ToString(), ok);
    }

    public async Task<List<WipeOutcome>> RunPlanAsync(
        CloudRpcClient rpc,
        IEnumerable<WipeTarget> targets,
        bool blankInsteadOfDelete,
        IProgress<WipeOutcome>? progress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<WipeOutcome>();
        foreach (var t in targets)
        {
            var o = await WipeAsync(rpc, t.AppId, t.FileName, t.Action == WipeAction.BlankReset, ct: ct);
            outcomes.Add(o);
            progress?.Report(o);
        }
        return outcomes;
    }
}