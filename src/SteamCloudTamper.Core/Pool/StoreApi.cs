using System.Text.Json;

namespace SteamCloudTamper.Core.Pool;

public sealed record AppDetails(string Name, bool IsFree, string ReleaseDate, double? PriceUsd, bool Found);

/// <summary>
/// Anonymous read-only store API client for pool curation (name / free / release date).
/// No login, no writes - exactly the kind of traffic a plain browser does.
/// </summary>
public static class StoreApi
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static async Task<AppDetails> GetAppDetailsAsync(uint appId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct);
            if (!resp.IsSuccessStatusCode) return new AppDetails("?", false, "?", null, false);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var app) ||
                !app.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                return new AppDetails("?", false, "?", null, false);

            var data = app.GetProperty("data");
            var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var free = data.TryGetProperty("is_free", out var f) && f.GetBoolean();
            var date = data.TryGetProperty("release_date", out var rd) &&
                       rd.TryGetProperty("date", out var d) ? d.GetString() ?? "?" : "?";
            var price = data.TryGetProperty("price_overview", out var po) &&
                        po.TryGetProperty("final", out var pr) ? pr.GetDouble() / 100d : (double?)null;
            return new AppDetails(name, free, date, price, true);
        }
        catch
        {
            return new AppDetails("?", false, "?", null, false);
        }
    }

    public static async Task<List<(uint AppId, AppDetails Details)>> RefreshPoolAsync(
        IEnumerable<uint> appIds, IProgress<(uint, AppDetails)>? progress = null, CancellationToken ct = default)
    {
        var results = new List<(uint, AppDetails)>();
        foreach (var id in appIds)
        {
            var d = await GetAppDetailsAsync(id, ct);
            results.Add((id, d));
            progress?.Report((id, d));
        }
        return results;
    }
}