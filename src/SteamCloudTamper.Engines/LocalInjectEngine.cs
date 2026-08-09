using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Vdf;

namespace SteamCloudTamper.Engines;

public sealed class LocalInjectEngine
{
    public event Action<string>? Log;

    public string InjectFile(string userDataAppDir, string sourcePath, string? remoteName = null)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        Directory.CreateDirectory(remote);

        var name = remoteName ?? Path.GetFileName(sourcePath);
        var dest = Path.Combine(remote, name);
        File.Copy(sourcePath, dest, overwrite: true);

        RegenerateVdf(userDataAppDir);
        Log?.Invoke($"Injected {sourcePath} -> {dest}");
        return dest;
    }

    public void WriteBlank(string userDataAppDir, string remoteName, byte[]? template = null)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllBytes(Path.Combine(remote, remoteName), BlankSaveKit.CreateBlank(remoteName, template));
        RegenerateVdf(userDataAppDir);
        Log?.Invoke($"Blank written to {remoteName}");
    }

    public void RegenerateVdf(string userDataAppDir)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        var vdfPath = Path.Combine(userDataAppDir, "remotecache.vdf");
        RemotecacheVdf.WriteTo(vdfPath, remote);
        Log?.Invoke($"Regenerated {Path.GetFileName(vdfPath)}");
    }

    public string? RemoveBucket(string userDataAppDir, bool keepBackup = true)
    {
        if (!Directory.Exists(userDataAppDir)) return null;

        string? backupDir = null;
        if (keepBackup)
        {
            backupDir = Path.Combine(
                Path.GetDirectoryName(userDataAppDir)!,
                $"backup_{Path.GetFileName(userDataAppDir)}_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDirectory(userDataAppDir, backupDir);
            Log?.Invoke($"Backed up to {backupDir}");
        }

        foreach (var d in Directory.EnumerateDirectories(userDataAppDir)) Directory.Delete(d, true);
        foreach (var f in Directory.EnumerateFiles(userDataAppDir)) File.Delete(f);
        Log?.Invoke($"Cleared {userDataAppDir}");
        return backupDir;
    }

    public const string LockMarker = "SCT_LOCKOUT\n";

    public string? InstallLock(string userDataDir, uint appId)
    {
        if (!Directory.Exists(userDataDir))
        {
            Log?.Invoke($"userdata dir missing: {userDataDir}");
            return null;
        }

        var path = Path.Combine(userDataDir, appId.ToString());

        if (Directory.Exists(path))
        {
            var backup = Path.Combine(userDataDir, $"backup_{appId}_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDirectory(path, backup);
            ClearBucketDir(path);
            Log?.Invoke($"Moved {appId} files to {backup}");
        }

        if (File.Exists(path))
        {
            Log?.Invoke($"Lock file already present for {appId}");
            return null;
        }

        File.WriteAllText(path, LockMarker);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Log?.Invoke($"Locked {appId} (read-only file blocks folder re-creation)");
        return path;
    }

    public bool RemoveLock(string userDataDir, uint appId)
    {
        var path = Path.Combine(userDataDir, appId.ToString());
        if (!File.Exists(path)) return false;

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        Log?.Invoke($"Unlocked {appId}");
        return true;
    }

    private static void ClearBucketDir(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, true);
        foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
        Directory.Delete(dir, false);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}