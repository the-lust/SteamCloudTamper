using System.Security.Cryptography;
using System.Text;

namespace SteamCloudTamper.Core.Vdf;

public static class RemotecacheVdf
{
    public static VdfNode Build(string remoteDir, DateTime? now = null)
    {
        var root = new VdfNode();
        var ts = ((DateTimeOffset)(now ?? DateTime.UtcNow)).ToUnixTimeSeconds();

        foreach (var file in Directory.EnumerateFiles(remoteDir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase)) continue;

            var info = new FileInfo(file);
            var entry = root.AddChild(name);
            var mtime = ((DateTimeOffset)info.LastWriteTimeUtc).ToUnixTimeSeconds();
            entry.Add("root", "0");
            entry.Add("size", info.Length.ToString());
            entry.Add("localtime", mtime.ToString());
            entry.Add("time", mtime.ToString());
            entry.Add("remotetime", mtime.ToString());
            entry.Add("sha", Sha1Hex(file));
            entry.Add("syncstate", "1");
            entry.Add("persiststate", "0");
            entry.Add("platformstosync2", "-1");
            entry.Add("total", ts.ToString());
        }

        return root;
    }

    public static void WriteTo(string targetPath, string remoteDir)
    {
        var vdf = Build(remoteDir);
        File.WriteAllText(targetPath, VdfWriter.Write(vdf));
    }

    public static byte[] Sha1(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA1.HashData(fs);
    }

    public static string Sha1Hex(string path) => Hex.Encode(Sha1(path));

    public static string Sha1Hex(ReadOnlySpan<byte> data) => Hex.Encode(SHA1.HashData(data));
}

public static class Hex
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static byte[] Decode(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}