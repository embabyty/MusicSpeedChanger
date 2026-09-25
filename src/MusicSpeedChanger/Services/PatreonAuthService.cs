using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicSpeedChanger.Services;

/// <summary>Verified Patreon account: usable tokens plus the display name.</summary>
public sealed record PatreonAccount(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiryUtc,
    string? FullName);

/// <summary>
/// "Login with Patreon" (OAuth2 authorization-code flow) used to gate beta builds.
/// The app listens on the registered loopback redirect URI, so no admin rights or
/// firewall exception are needed. The browser round-trip ends at a local port and
/// the membership check runs against Patreon's servers.
///
/// Note: the client secret ships inside the exe (extractable by design for
/// installed apps) — acceptable for beta gating since verification is server-side.
/// Refresh tokens persist in settings.json in plaintext; treat like a session cookie.
/// </summary>
public static class PatreonAuthService
{
    public const string ClientId = "OlhmBbp1BAb78sLTCtfXqfLa-MiiSq05ChoosHdfxCED-bxPnirrpq6tw0Sq8FDx";
    public const string CampaignId = "14934463";
    public const string RedirectUri = "http://127.0.0.1:51234/callback";

    // Extractable from the exe — fine for beta gating, never for real secrets.
    private const string ClientSecret = "FaQI4ygb2KpT27eMagMkZoQXt7tNszgU2cly4LkbYv_HzHRGetjJjiuUWuV0X_ja";

    private const int CallbackPort = 51234;
    private const string AuthorizeUrl = "https://www.patreon.com/oauth2/authorize";
    private const string TokenUrl = "https://www.patreon.com/api/oauth2/token";
    private const string IdentityUrl =
        "https://www.patreon.com/api/oauth2/v2/identity" +
        "?include=memberships.campaign&fields[member]=patron_status&fields[user]=full_name";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Full login: opens the browser, waits for the loopback callback, exchanges the
    /// code, and checks for an active membership to this campaign.
    /// Returns null when cancelled, timed out, denied, errored, or not a patron.
    /// </summary>
    public static async Task<PatreonAccount?> LoginAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        string state = RandomHex(16);
        string authorize =
            $"{AuthorizeUrl}?response_type=code&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString("identity identity.memberships")}" +
            $"&state={state}";

        progress?.Report("Waiting for Patreon login in your browser…");
        string? code = await WaitForCallbackAsync(authorize, state, progress, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(code)) return null;

        progress?.Report("Exchanging code for token…");
        var account = await ExchangeCodeAsync(code, ct).ConfigureAwait(false);
        if (account == null) return null;

        progress?.Report("Verifying membership…");
        var (active, name) = await VerifyAsync(account.AccessToken, ct).ConfigureAwait(false);
        if (!active) return null;
        return account with { FullName = name };
    }

    /// <summary>Silent re-verification from a stored refresh token. Null = relogin needed.</summary>
    public static async Task<PatreonAccount?> RefreshAndVerifyAsync(
        string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
            });
            using var resp = await Http.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var account = ParseTokenResponse(
                await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (account == null) return null;
            var (active, name) = await VerifyAsync(account.AccessToken, ct).ConfigureAwait(false);
            return active ? account with { FullName = name } : null;
        }
        catch { return null; }
    }

    /// <summary>True when the token belongs to an active patron of this campaign.</summary>
    public static async Task<(bool IsActive, string? FullName)> VerifyAsync(
        string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, IdentityUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, null);
            return ParseIdentity(
                await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch { return (false, null); }
    }

    // ---------- Loopback callback (plain TCP: no admin URL-ACL needed) ----------

    private static async Task<string?> WaitForCallbackAsync(
        string authorizeUrl, string state, IProgress<string>? progress, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try { listener.Start(); }
        catch
        {
            progress?.Report($"Couldn't open local port {CallbackPort} for Patreon login.");
            return null;
        }

        try
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(authorizeUrl)); }
            catch { progress?.Report("Couldn't open your browser for Patreon login."); return null; }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            while (true)
            {
                var acceptTask = listener.AcceptTcpClientAsync();
                var winner = await Task.WhenAny(
                    acceptTask, Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)).ConfigureAwait(false);
                if (winner != acceptTask)
                {
                    // Timed out or cancelled — observe the pending accept before leaving.
                    try { listener.Stop(); } catch { /* ignore */ }
                    _ = acceptTask.ContinueWith(
                        t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    progress?.Report("Patreon login timed out.");
                    return null;
                }
                using var client = await acceptTask.ConfigureAwait(false);
                var (done, code) = await HandleCallbackAsync(client, state, linked.Token).ConfigureAwait(false);
                if (done) return code; // code set, or user denied (null)
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
        finally { try { listener.Stop(); } catch { /* ignore */ } }
    }

    /// <returns>(done, code): done=false keeps waiting (e.g. favicon); done=true ends it.</returns>
    private static async Task<(bool done, string? code)> HandleCallbackAsync(
        TcpClient client, string state, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            var buf = new byte[8192];
            var sb = new StringBuilder();
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeout.Token);
            int total = 0;
            while (!sb.ToString().Contains("\r\n\r\n") && total < buf.Length)
            {
                int read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), linked.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                sb.Clear();
                sb.Append(Encoding.ASCII.GetString(buf, 0, total));
            }
            string request = sb.ToString();
            string requestLine = request.Split("\r\n")[0];
            string[] parts = requestLine.Split(' ');
            string target = parts.Length >= 2 ? parts[1] : "";

            if (target.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
                if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
                {
                    await WritePageAsync(stream, "Music Speed Changer",
                        "Patreon login was cancelled — you can close this tab and return to the app.", ct)
                        .ConfigureAwait(false);
                    return (true, null);
                }
                if (query.TryGetValue("code", out var code) &&
                    query.TryGetValue("state", out var gotState) &&
                    string.Equals(gotState, state, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(code))
                {
                    await WritePageAsync(stream, "Music Speed Changer",
                        "Patreon login complete — you can close this tab and return to the app.", ct)
                        .ConfigureAwait(false);
                    return (true, code);
                }
                await WritePageAsync(stream, "Music Speed Changer",
                    "Invalid login response — you can close this tab and try again.", ct)
                    .ConfigureAwait(false);
                return (true, null);
            }

            await WritePageAsync(stream, "Music Speed Changer", "Not found.", ct, "404 Not Found")
                .ConfigureAwait(false);
            return (false, null);
        }
        catch { return (false, null); }
    }

    private static async Task WritePageAsync(
        NetworkStream stream, string title, string message, CancellationToken ct,
        string status = "200 OK")
    {
        try
        {
            string html = $"<html><body style=\"font-family:sans-serif\">" +
                $"<h2>{WebUtility.HtmlEncode(title)}</h2>" +
                $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch { /* browser may have gone away */ }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string val = eq < 0 ? "" : pair[(eq + 1)..];
            dict[Uri.UnescapeDataString(key.Replace("+", "%20"))] =
                Uri.UnescapeDataString(val.Replace("+", "%20"));
        }
        return dict;
    }

    // ---------- Token + identity ----------

    private static async Task<PatreonAccount?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = RedirectUri,
            });
            using var resp = await Http.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseTokenResponse(
                await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch { return null; }
    }

    private static PatreonAccount? ParseTokenResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) ||
                !root.TryGetProperty("refresh_token", out var rt)) return null;
            string access = at.GetString() ?? "", refresh = rt.GetString() ?? "";
            if (access.Length == 0 || refresh.Length == 0) return null;
            double lifetime = 30 * 24 * 3600; // fallback: ~30 days
            if (root.TryGetProperty("expires_in", out var exp))
            {
                if (exp.ValueKind == JsonValueKind.Number && exp.TryGetDouble(out double n))
                    lifetime = n;
                else if (exp.ValueKind == JsonValueKind.String &&
                         double.TryParse(exp.GetString(), out double s))
                    lifetime = s;
            }
            return new PatreonAccount(access, refresh,
                DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(lifetime, 60, 366 * 24 * 3600)), null);
        }
        catch { return null; }
    }

    private static (bool IsActive, string? FullName) ParseIdentity(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? name = null;
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("attributes", out var attrs) &&
                attrs.TryGetProperty("full_name", out var fn))
                name = fn.GetString();

            if (root.TryGetProperty("included", out var included) &&
                included.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in included.EnumerateArray())
                {
                    if (!entry.TryGetProperty("type", out var type) ||
                        type.GetString() != "member") continue;
                    if (!entry.TryGetProperty("attributes", out var mAttrs) ||
                        !mAttrs.TryGetProperty("patron_status", out var status) ||
                        status.GetString() != "active_patron") continue;
                    if (!entry.TryGetProperty("relationships", out var rels) ||
                        !rels.TryGetProperty("campaign", out var campaign) ||
                        !campaign.TryGetProperty("data", out var campaignData) ||
                        !campaignData.TryGetProperty("id", out var campaignId)) continue;
                    if (campaignId.GetString() == CampaignId)
                        return (true, name);
                }
            }
            return (false, name);
        }
        catch { return (false, null); }
    }

    private static string RandomHex(int bytes)
    {
        byte[] b = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicSpeedChanger/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
