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
public sealed record UpdateInfo(Version Version, string Notes, string DownloadUrl, string FileName);

/// <summary>
/// Checks a feed for newer releases and downloads the installer.
/// Supports the GitHub Releases API (default) and generic JSON feeds shaped as
/// { "version": "1.2.0", "notes": "...", "url": "https://…/Setup-….exe" }.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static async Task<UpdateInfo?> CheckForUpdateAsync(
        string feedUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl)) return null;
        using var response = await Http.GetAsync(feedUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        UpdateInfo? info = feedUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? ParseGitHubRelease(doc.RootElement)
            : ParseGenericFeed(doc.RootElement);

        if (info != null && info.Version > CurrentVersion)
            return info;
        return null;
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
        if (!TryParseVersion(tag.GetString(), out var version)) return null;
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";

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
                if (url.Length > 0) return new UpdateInfo(version, notes, url, name);
            }
        }
        return null;
    }

    private static UpdateInfo? ParseGenericFeed(JsonElement root)
    {
        if (!root.TryGetProperty("version", out var v) ||
            !TryParseVersion(v.GetString(), out var version)) return null;
        if (!root.TryGetProperty("url", out var u)) return null;
        string url = u.GetString() ?? "";
        if (url.Length == 0) return null;
        string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
        string file = root.TryGetProperty("fileName", out var f) && f.GetString() is string s && s.Length > 0
            ? s
            : "Setup update.exe";
        return new UpdateInfo(version, notes, url, file);
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
