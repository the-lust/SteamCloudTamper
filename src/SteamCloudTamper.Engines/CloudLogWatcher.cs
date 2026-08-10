using System.Text.RegularExpressions;

namespace SteamCloudTamper.Engines;

public enum CloudVerdict
{
    /// <summary>Still waiting - no relevant log line appeared in the window.</summary>
    Unknown,
    /// <summary>The running Steam client uploaded at least one file (Upload complete, result OK).</summary>
    Success,
    /// <summary>The running Steam client reported Access Denied for the app (unentitled slot).</summary>
    Denied,
    /// <summary>Upload failed for another reason.</summary>
    Failed,
}

public sealed record CloudWatchResult(CloudVerdict Verdict, string? MatchLine, int NewLines);

/// <summary>
/// Watches the RUNNING Steam client's cloud log (logs/cloud_log.txt) for upload verdicts.
/// This is how SCT uses the already-signed-in Steam session without logging in itself:
/// files are staged locally, the client syncs them, and we read the server's answer.
/// Private single-file probes only - never floods.
/// </summary>
public sealed class CloudLogWatcher
{
    private readonly string _logPath;

    public CloudLogWatcher(string steamPath, uint appId)
    {
        _logPath = Path.Combine(steamPath, "logs", "cloud_log.txt");
        AppId = appId;
    }

    public uint AppId { get; }

    public event Action<string>? Log;

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a verdict about <see cref="AppId"/>.
    /// Reads the log incrementally (own watermark, never rewinds), so an old line from
    /// a previous run can only be missed, never re-credited.
    /// </summary>
    public async Task<CloudWatchResult> WaitForVerdictAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var watermark = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
        var deadline = DateTime.UtcNow + timeout;
        var seen = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            var lines = ReadNewLines(watermark);
            if (lines.Count > 0)
            {
                watermark += lines.Sum(l => (long)l.Length);
                seen.AddRange(lines);
                foreach (var l in lines) Log?.Invoke(l);

                var verdict = Classify(seen);
                if (verdict != CloudVerdict.Unknown)
                {
                    return new CloudWatchResult(verdict, seen.LastOrDefault(), seen.Count);
                }
            }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }

        return new CloudWatchResult(Classify(seen), seen.LastOrDefault(), seen.Count);
    }

    private List<string> ReadNewLines(long watermark)
    {
        var list = new List<string>();
        if (!File.Exists(_logPath)) return list;
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= watermark) return list;
            fs.Seek(watermark, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains($"[AppID {AppId}]", StringComparison.OrdinalIgnoreCase)
                    || line.Contains($"[appid {AppId}]", StringComparison.OrdinalIgnoreCase))
                    list.Add(line);
            }
        }
        catch (IOException) { }
        return list;
    }

    private static CloudVerdict Classify(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return CloudVerdict.Unknown;

        var denied = false;
        var ok = false;
        var started = false;
        foreach (var line in lines)
        {
            if (line.Contains("Access Denied", StringComparison.OrdinalIgnoreCase))
                denied = true;
            if (line.Contains("result OK", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Upload success", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Uploaded file", StringComparison.OrdinalIgnoreCase))
                ok = true;
            if (line.Contains("Need to upload", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Upload batch", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Uploading file", StringComparison.OrdinalIgnoreCase))
                started = true;
        }
        if (denied) return CloudVerdict.Denied;   // Access Denied is final for that app
        if (ok) return CloudVerdict.Success;
        if (started) return CloudVerdict.Failed;  // upload in flight but no verdict yet
        return CloudVerdict.Unknown;
    }
}