using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSpeedChanger.Services;

/// <summary>Version info for an available update.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string DownloadUrl, string FileName, bool IsBeta);

/// <summary>
/// Checks a feed for newer releases and downloads the installer.
/// Supports the GitHub Releases API (default) and generic JSON feeds shaped as
/// { "version": "1.2.0", "notes": "...", "url": "https://…/Setup-….exe" }.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Beta builds are gated for supporters on this Patreon page.</summary>
    public const string PatreonPageUrl = "https://www.patreon.com/cw/EmAppleFlagship";

    /// <summary>Owner of Music Speed Changer and the EmAppleFlagship Patreon.</summary>
    public const string OwnerName = "EmAppleFlagship";
    public const string OwnerEmail = "ios11emiry@gmail.com";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Human-readable version (prefers InformationalVersion, e.g. "3.0.0 Beta 1").</summary>
    public static string DisplayVersion
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                if (plus >= 0) info = info[..plus];
                return info.Trim();
            }
            return CurrentVersion.ToString();
        }
    }

    public static async Task<UpdateInfo?> CheckForUpdateAsync(
        string feedUrl, bool includeBeta = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return null;

        // A feed pointing straight at a /releases list (array) — pick the best entry.
        if (IsGitHubListUrl(feedUrl))
        {
            using var listResponse = await Http.GetAsync(feedUrl, ct).ConfigureAwait(false);
            listResponse.EnsureSuccessStatusCode();
            using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return PickBestGitHub(listDoc.RootElement, includeBeta);
        }

        using var response = await Http.GetAsync(feedUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        if (!feedUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var generic = ParseGenericFeed(doc.RootElement);
            if (generic == null || !IsNewerThanCurrent(generic)) return null;
            if (generic.IsBeta && !includeBeta) return null;
            return generic;
        }

        var single = ParseGitHubRelease(doc.RootElement);
        if (single == null || !IsNewerThanCurrent(single)) return null;
        if (!includeBeta) return single.IsBeta ? null : single;
        if (single.IsBeta) return single;

        // Stable is latest, but the user opted into betas — check the release
        // list for a newer beta (GitHub's /latest endpoint never returns prereleases).
        try
        {
            string? listUrl = GetGitHubListUrl(feedUrl);
            if (listUrl == null) return single;
            using var listResponse = await Http.GetAsync(listUrl, ct).ConfigureAwait(false);
            listResponse.EnsureSuccessStatusCode();
            using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var best = PickBestGitHub(listDoc.RootElement, includeBeta: true);
            if (best != null && CompareReleases(best, single) >= 0) return best;
        }
        catch { /* list lookup is best-effort — fall back to stable */ }
        return single;
    }

    public static async Task DownloadAsync(
        string url, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(destination);
        byte[] buf = new byte[81920];
        long done = 0;
        int read;
        while ((read = await net.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total.HasValue && total > 0)
                progress?.Report((double)done / total.Value);
        }
    }

    private static UpdateInfo? ParseGitHubRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tag)) return null;
        string tagText = tag.GetString() ?? "";
        if (!TryParseVersion(tagText, out var version)) return null;
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        bool isBeta = (root.TryGetProperty("prerelease", out var pre) &&
                       pre.ValueKind == JsonValueKind.True) || IsBetaTag(tagText);

        if (root.TryGetProperty("assets", out var assets))
        {
            // Prefer the Setup installer; fall back to any .exe asset.
            JsonElement? pick = null, anyExe = null;
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n) ||
                    !a.TryGetProperty("browser_download_url", out var u)) continue;
                string name = n.GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                anyExe ??= a;
                if (name.StartsWith("Setup-", StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
            }
            var chosen = pick ?? anyExe;
            if (chosen.HasValue)
            {
                string name = chosen.Value.GetProperty("name").GetString() ?? "Setup update.exe";
                string url = chosen.Value.GetProperty("browser_download_url").GetString() ?? "";
                if (url.Length > 0) return new UpdateInfo(version, notes, url, name, isBeta);
            }
        }
        return null;
    }

    private static UpdateInfo? ParseGenericFeed(JsonElement root)
    {
        if (!root.TryGetProperty("version", out var v)) return null;
        string versionText = v.GetString() ?? "";
        if (!TryParseVersion(versionText, out var version)) return null;
        if (!root.TryGetProperty("url", out var u)) return null;
        string url = u.GetString() ?? "";
        if (url.Length == 0) return null;
        string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
        string file = root.TryGetProperty("fileName", out var f) && f.GetString() is string s && s.Length > 0
            ? s
            : "Setup update.exe";
        return new UpdateInfo(version, notes, url, file, IsBetaTag(versionText));
    }

    /// <summary>True when the feed URL points at a GitHub /releases list (JSON array).</summary>
    private static bool IsGitHubListUrl(string feedUrl) =>
        feedUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase) &&
        (feedUrl.Contains("/releases?", StringComparison.OrdinalIgnoreCase) ||
         feedUrl.TrimEnd('/').EndsWith("/releases", StringComparison.OrdinalIgnoreCase));

    private static string? GetGitHubListUrl(string feedUrl)
    {
        int i = feedUrl.IndexOf("/releases", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        return feedUrl[..(i + "/releases".Length)] + "?per_page=30";
    }

    /// <summary>Newest release in a GitHub list that is newer than the running build.</summary>
    private static UpdateInfo? PickBestGitHub(JsonElement array, bool includeBeta)
    {
        if (array.ValueKind != JsonValueKind.Array) return null;
        UpdateInfo? best = null;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("draft", out var draft) &&
                draft.ValueKind == JsonValueKind.True) continue;
            var info = ParseGitHubRelease(entry);
            if (info == null || !IsNewerThanCurrent(info)) continue;
            if (info.IsBeta && !includeBeta) continue;
            if (best == null || CompareReleases(info, best) > 0) best = info;
        }
        return best;
    }

    /// <summary>Orders releases: higher version wins; a stable build beats its own beta.</summary>
    private static int CompareReleases(UpdateInfo a, UpdateInfo b)
    {
        int cmp = a.Version.CompareTo(b.Version);
        if (cmp != 0) return cmp;
        if (a.IsBeta == b.IsBeta) return 0;
        return a.IsBeta ? -1 : 1;
    }

    private static bool IsNewerThanCurrent(UpdateInfo info) =>
        CompareReleases(info, new UpdateInfo(CurrentVersion, "", "", "", IsBeta: false)) > 0;

    /// <summary>Heuristic: prerelease markers like "-beta.1", "+build", "alpha", "rc".</summary>
    private static bool IsBetaTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.IndexOfAny(new[] { '-', '+' }) >= 0) return true;
        return text.Contains("beta", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("rc", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(1, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimStart('v', 'V');
        // Drop pre-release suffixes ("1.2.0-beta") for comparison purposes.
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];
        var parts = text.Split('.').Select(p =>
        {
            string digits = new(p.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out int n) ? n : -1;
        }).ToArray();
        if (parts.Length == 0 || parts.Any(p => p < 0)) return false;
        try
        {
            version = parts.Length switch
            {
                1 => new Version(parts[0], 0),
                2 => new Version(parts[0], parts[1]),
                3 => new Version(parts[0], parts[1], parts[2]),
                _ => new Version(parts[0], parts[1], parts[2], parts[3]),
            };
            return true;
        }
        catch { return false; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicSpeedChanger/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
