using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace MusicSpeedChanger.Services;

/// <summary>
/// Discord Rich Presence, Cider-style: shows the current track title, artist/album,
/// tempo + pitch, play/pause state and elapsed/remaining time on the user's Discord
/// profile. Talks to the local Discord client over its IPC named pipes
/// (discord-ipc-0…9) with framed JSON — no NuGet packages required.
/// All methods are best-effort and never throw.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    /// <summary>
    /// Image asset keys. Upload images with exactly these names under the
    /// Discord application's Rich Presence → Art Assets page:
    /// logo (app icon), play, pause.
    /// </summary>
    public const string LargeImageKey = "logo";
    public const string PlayImageKey = "play";
    public const string PauseImageKey = "pause";

    public const string DeveloperPortalUrl = "https://discord.com/developers/applications";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly struct Snapshot
    {
        public readonly string? FilePath;
        public readonly bool IsLoaded;
        public readonly bool IsPlaying;
        public readonly TimeSpan Position;
        public readonly TimeSpan Duration;
        public readonly double TempoPercent;
        public readonly double PitchSemitones;
        public readonly bool ShowTempoPitch;
        public readonly bool Force;

        public Snapshot(string? filePath, bool isLoaded, bool isPlaying,
            TimeSpan position, TimeSpan duration, double tempoPercent,
            double pitchSemitones, bool showTempoPitch, bool force)
        {
            FilePath = filePath;
            IsLoaded = isLoaded;
            IsPlaying = isPlaying;
            Position = position;
            Duration = duration;
            TempoPercent = tempoPercent;
            PitchSemitones = pitchSemitones;
            ShowTempoPitch = showTempoPitch;
            Force = force;
        }
    }

    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private bool _disposed;
    private bool _connecting;
    private bool _enabled;
    private string _clientId = "";
    private string? _connectedClientId;
    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;

    // Dedupes redundant SET_ACTIVITY frames (Discord rate-limits updates).
    private string _lastKey = "";
    private DateTime _lastSendUtc = DateTime.MinValue;
    private string _lastPath = "";
    private bool _lastPlaying;
    private Snapshot? _lastSnapshot;

    // Resolved file tags, cached per path (tags rarely change mid-session).
    private string? _metaPath;
    private string _metaTitle = "";
    private string _metaArtist = "";
    private string _metaAlbum = "";

    private string _status = "Not configured";
    public string StatusText
    {
        get { lock (_gate) { return _status; } }
    }

    public event EventHandler? StatusChanged;

    public void Configure(bool enabled, string? clientId)
    {
        clientId = (clientId ?? "").Trim();
        bool changed;
        lock (_gate)
        {
            changed = enabled != _enabled || !string.Equals(clientId, _clientId, StringComparison.Ordinal);
            _enabled = enabled;
            _clientId = clientId;
        }
        if (!enabled)
        {
            Disconnect();
            SetStatus("Disabled");
            return;
        }
        if (string.IsNullOrEmpty(clientId))
        {
            Disconnect();
            SetStatus("Set an Application ID to enable");
            return;
        }
        if (!changed && IsConnectedTo(clientId)) return;
        if (!changed) { _ = Task.Run(() => RefreshAsync(null)); return; }
        SetStatus("Connecting…");
        _ = Task.Run(() => RefreshAsync(null));
    }

    /// <summary>
    /// Pushes current playback state to Discord. Fire-and-forget; safe to call
    /// from the UI thread at any rate (transition updates send immediately,
    /// steady-state refreshes are throttled + deduped internally).
    /// </summary>
    public void Refresh(string? filePath, bool isLoaded, bool isPlaying,
        TimeSpan position, TimeSpan duration, double tempoPercent,
        double pitchSemitones, bool showTempoPitch, bool force = false)
    {
        var snap = new Snapshot(filePath, isLoaded, isPlaying, position, duration,
            tempoPercent, pitchSemitones, showTempoPitch, force);
        lock (_gate) { _lastSnapshot = snap; }
        _ = Task.Run(() => RefreshAsync(snap));
    }

    /// <summary>Clears the Discord activity (stopped / nothing loaded).</summary>
    public void Clear()
    {
        lock (_gate) { _lastSnapshot = null; }
        _ = Task.Run(() => ClearAsync());
    }

    /// <summary>Forces a disconnect + reconnect attempt, then re-pushes last state.</summary>
    public void Reconnect()
    {
        Snapshot? snap;
        string clientId;
        bool enabled;
        lock (_gate)
        {
            snap = _lastSnapshot;
            clientId = _clientId;
            enabled = _enabled;
            _lastConnectAttemptUtc = DateTime.MinValue; // bypass backoff
        }
        _ = Task.Run(async () =>
        {
            Disconnect();
            if (!enabled || string.IsNullOrEmpty(clientId) || _disposed) return;
            SetStatus("Connecting…");
            if (await ConnectAsync(clientId).ConfigureAwait(false) && snap.HasValue)
                await RefreshAsync(snap.Value).ConfigureAwait(false);
        });
    }

    private async Task RefreshAsync(Snapshot? snap)
    {
        if (_disposed) return;
        bool enabled;
        string clientId;
        lock (_gate) { enabled = _enabled; clientId = _clientId; }
        if (!enabled || string.IsNullOrEmpty(clientId) || _disposed) return;

        if (!IsConnectedTo(clientId))
        {
            // Back off when Discord isn't running: background refreshes retry
            // every 30 s, user-initiated (force) ones every 10 s.
            var now = DateTime.UtcNow;
            bool force = snap.HasValue && snap.Value.Force;
            double minGap = force ? 10 : 30;
            lock (_gate)
            {
                if ((now - _lastConnectAttemptUtc).TotalSeconds < minGap) return;
                _lastConnectAttemptUtc = now;
            }
            SetStatus("Connecting…");
            if (!await ConnectAsync(clientId).ConfigureAwait(false)) return;
        }

        if (!snap.HasValue) return; // Configure() probe — connection is enough
        var s = snap.Value;

        if (!s.IsLoaded || string.IsNullOrWhiteSpace(s.FilePath))
        {
            await ClearAsync().ConfigureAwait(false);
            return;
        }

        var (title, artist, album) = await ResolveMetadataAsync(s.FilePath).ConfigureAwait(false);
        if (_disposed) return;

        string key = BuildDedupeKey(s, title, artist, album);
        bool transition;
        lock (_gate)
        {
            transition = s.Force
                || !string.Equals(s.FilePath, _lastPath, StringComparison.OrdinalIgnoreCase)
                || s.IsPlaying != _lastPlaying;
        }

        lock (_gate)
        {
            if (!transition && key == _lastKey) return;
            // Steady-state refreshes (position drift) at most every 4 s;
            // transitions (track / play-pause / seek / tempo-pitch jumps) go now.
            if (!transition && (DateTime.UtcNow - _lastSendUtc).TotalSeconds < 4) return;
        }

        string payload = BuildActivityPayload(s, title, artist, album);
        if (await SendFrameAsync(1, payload).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _lastKey = key;
                _lastSendUtc = DateTime.UtcNow;
                _lastPath = s.FilePath ?? "";
                _lastPlaying = s.IsPlaying;
            }
        }
    }

    private async Task ClearAsync()
    {
        bool enabled;
        string clientId;
        lock (_gate) { enabled = _enabled; clientId = _clientId; }
        if (!enabled || string.IsNullOrEmpty(clientId) || _disposed) return;
        if (!IsConnectedTo(clientId)) return; // nothing shown while disconnected
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity = (object?)null },
                nonce = Guid.NewGuid().ToString(),
            }, JsonOptions);
            if (await SendFrameAsync(1, payload).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _lastKey = "";
                    _lastSendUtc = DateTime.UtcNow;
                    _lastPlaying = false;
                }
            }
        }
        catch { /* best-effort */ }
    }

    // ---------- Presence content (Cider-style) ----------

    private static string BuildDedupeKey(Snapshot s, string title, string artist, string album)
    {
        // Position bucketed to 10 s: steady playback needs no resends, Discord
        // counts down the end timestamp on its own. Paused has no timestamps.
        long posBucket = s.IsPlaying ? (long)(s.Position.TotalSeconds / 10) : 0;
        return string.Join("\n",
            s.FilePath ?? "", s.IsPlaying, $"{s.TempoPercent:0.0}",
            $"{s.PitchSemitones:0.00}", posBucket, s.ShowTempoPitch,
            title, artist, album);
    }

    private static string BuildActivityPayload(Snapshot s, string title, string artist, string album)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double tempo = Math.Clamp(s.TempoPercent / 100.0, 0.25, 3.0);
        if (!(tempo > 0)) tempo = 1;

        // Tempo-aware timestamps: wall-clock remaining = source remaining / tempo,
        // so the Discord progress bar stays truthful at 50% / 150% speed.
        long? start = null, end = null;
        if (s.IsPlaying && s.Duration > TimeSpan.Zero)
        {
            double posSec = Math.Clamp(s.Position.TotalSeconds, 0, s.Duration.TotalSeconds);
            start = now - (long)(posSec / tempo);
            end = now + (long)Math.Max(1, (s.Duration.TotalSeconds - posSec) / tempo);
            if (end <= start) end = start + 1;
        }

        string details = Truncate(string.IsNullOrWhiteSpace(title) ? "Unknown track" : title, 128);

        string who;
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album))
            who = $"{artist} • {album}";
        else if (!string.IsNullOrWhiteSpace(artist))
            who = artist;
        else if (!string.IsNullOrWhiteSpace(album))
            who = album;
        else
            who = "Music Speed Changer";

        string extra = BuildTempoPitchSuffix(s);
        string state = Truncate(who + extra, 128);
        string smallText = Truncate(s.IsPlaying ? "Playing" + extra : "Paused", 128);
        string largeText = Truncate(
            !string.IsNullOrWhiteSpace(album) ? album : "Music Speed Changer", 128);

        var activity = new
        {
            type = 2, // LISTENING — Discord renders "Listening to Music Speed Changer"
            details,
            state,
            timestamps = (start.HasValue && end.HasValue)
                ? new { start = start.Value, end = end.Value }
                : null,
            assets = new
            {
                large_image = LargeImageKey,
                large_text = largeText,
                small_image = s.IsPlaying ? PlayImageKey : PauseImageKey,
                small_text = smallText,
            },
        };
        var cmd = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString(),
        };
        return JsonSerializer.Serialize(cmd, JsonOptions);
    }

    private static string BuildTempoPitchSuffix(Snapshot s)
    {
        if (!s.ShowTempoPitch) return "";
        bool customTempo = Math.Abs(s.TempoPercent - 100) >= 0.5;
        bool customPitch = Math.Abs(s.PitchSemitones) >= 0.05;
        string pitch = (s.PitchSemitones >= 0 ? "+" : "") + $"{s.PitchSemitones:0.0} st";
        if (customTempo && customPitch) return $" • {s.TempoPercent:0}% • {pitch}";
        if (customTempo) return $" • {s.TempoPercent:0}%";
        if (customPitch) return $" • {pitch}";
        return "";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = value.Trim();
        return value.Length <= max ? value : value[..max].TrimEnd();
    }

    private async Task<(string title, string artist, string album)> ResolveMetadataAsync(string? filePath)
    {
        string fallback = "Unknown track";
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try { fallback = Path.GetFileNameWithoutExtension(filePath); }
            catch { /* keep fallback */ }
        }
        if (string.IsNullOrWhiteSpace(filePath)) return (fallback, "", "");

        lock (_gate)
        {
            if (string.Equals(_metaPath, filePath, StringComparison.OrdinalIgnoreCase))
                return (_metaTitle, _metaArtist, _metaAlbum);
        }

        string title = fallback, artist = "", album = "";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var music = await file.Properties.GetMusicPropertiesAsync();
            if (!string.IsNullOrWhiteSpace(music.Title)) title = music.Title.Trim();
            if (!string.IsNullOrWhiteSpace(music.Artist)) artist = music.Artist.Trim();
            if (!string.IsNullOrWhiteSpace(music.Album)) album = music.Album.Trim();
        }
        catch { /* tags are optional — fall back to the file name */ }

        lock (_gate)
        {
            _metaPath = filePath;
            _metaTitle = title;
            _metaArtist = artist;
            _metaAlbum = album;
        }
        return (title, artist, album);
    }

    // ---------- Discord IPC framing ----------

    private bool IsConnectedTo(string clientId)
    {
        lock (_gate)
        {
            return _pipe != null && _pipe.IsConnected
                && string.Equals(_connectedClientId, clientId, StringComparison.Ordinal);
        }
    }

    private async Task<bool> ConnectAsync(string clientId)
    {
        lock (_gate)
        {
            if (_connecting || _disposed) return IsConnectedTo(clientId);
            _connecting = true;
        }
        try
        {
            for (int i = 0; i < 10; i++)
            {
                if (_disposed) return false;
                lock (_gate)
                {
                    if (!_enabled || !string.Equals(_clientId, clientId, StringComparison.Ordinal))
                        return false; // superseded by a settings change
                }
                NamedPipeClientStream? pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}",
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var cts = new CancellationTokenSource(2000);
                    await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

                    string handshake = JsonSerializer.Serialize(
                        new { v = 1, client_id = clientId }, JsonOptions);
                    await WriteFrameAsync(pipe, 0, handshake, CancellationToken.None).ConfigureAwait(false);

                    var (_, json) = await ReadFrameAsync(pipe, 3000).ConfigureAwait(false);
                    if (IsErrorResponse(json, out string message))
                    {
                        try { pipe.Dispose(); } catch { /* ignore */ }
                        SetStatus(string.IsNullOrEmpty(message)
                            ? "Discord rejected the connection"
                            : $"Discord: {message}");
                        return false; // wrong client ID etc. — other pipes won't help
                    }

                    lock (_gate)
                    {
                        if (_disposed || !_enabled || !string.Equals(_clientId, clientId, StringComparison.Ordinal))
                        {
                            try { pipe.Dispose(); } catch { /* ignore */ }
                            return false;
                        }
                        _pipe = pipe;
                        _connectedClientId = clientId;
                        _readerCts?.Cancel();
                        _readerCts?.Dispose();
                        _readerCts = new CancellationTokenSource();
                    }
                    StartReader();
                    SetStatus("Connected ✓");
                    return true;
                }
                catch (OperationCanceledException) { try { pipe?.Dispose(); } catch { /* ignore */ } }
                catch (IOException) { try { pipe?.Dispose(); } catch { /* ignore */ } }
                catch (UnauthorizedAccessException) { try { pipe?.Dispose(); } catch { /* ignore */ } }
                catch { try { pipe?.Dispose(); } catch { /* ignore */ } }
            }
            SetStatus("Discord not running — start the desktop app");
            return false;
        }
        finally
        {
            lock (_gate) { _connecting = false; }
        }
    }

    private void StartReader()
    {
        NamedPipeClientStream? pipe;
        CancellationToken token;
        lock (_gate)
        {
            pipe = _pipe;
            if (pipe == null || _readerCts == null) return;
            token = _readerCts.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && !_disposed)
                {
                    // Drain Discord replies (READY, activity acks, errors) so the
                    // pipe never blocks; any failure means the client went away.
                    await ReadFrameAsync(pipe, Timeout.Infinite, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch
            {
                if (!_disposed) HandleDisconnect();
            }
        });
    }

    private void HandleDisconnect()
    {
        bool wasConnected;
        lock (_gate)
        {
            wasConnected = _pipe != null;
            try { _readerCts?.Cancel(); } catch { /* ignore */ }
            try { _pipe?.Dispose(); } catch { /* ignore */ }
            _pipe = null;
            _connectedClientId = null;
            _lastKey = ""; // force a full re-push on reconnect
        }
        if (wasConnected && !_disposed)
            SetStatus("Disconnected — will retry");
    }

    private void Disconnect()
    {
        lock (_gate)
        {
            try { _readerCts?.Cancel(); } catch { /* ignore */ }
            try { _readerCts?.Dispose(); } catch { /* ignore */ }
            _readerCts = null;
            try { _pipe?.Dispose(); } catch { /* ignore */ }
            _pipe = null;
            _connectedClientId = null;
        }
    }

    private async Task<bool> SendFrameAsync(int opcode, string json)
    {
        NamedPipeClientStream? pipe;
        lock (_gate) { pipe = _pipe; }
        if (pipe == null || _disposed) return false;
        try
        {
            if (!pipe.IsConnected) { HandleDisconnect(); return false; }
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(data.Length).CopyTo(header, 4);
            await pipe.WriteAsync(header.AsMemory()).ConfigureAwait(false);
            await pipe.WriteAsync(data.AsMemory()).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            HandleDisconnect();
            return false;
        }
    }

    private static async Task WriteFrameAsync(NamedPipeClientStream pipe, int opcode,
        string json, CancellationToken ct)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(data.Length).CopyTo(header, 4);
        await pipe.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
        await pipe.WriteAsync(data.AsMemory(), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(int opcode, string json)> ReadFrameAsync(
        NamedPipeClientStream pipe, int timeoutMs, CancellationToken outer = default)
    {
        using var cts = timeoutMs == Timeout.Infinite
            ? CancellationTokenSource.CreateLinkedTokenSource(outer)
            : CancellationTokenSource.CreateLinkedTokenSource(outer,
                new CancellationTokenSource(timeoutMs).Token);
        byte[] header = await ReadExactAsync(pipe, 8, cts.Token).ConfigureAwait(false);
        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);
        if (length < 0 || length > 10 * 1024 * 1024)
            throw new IOException("Bad Discord IPC frame.");
        byte[] body = await ReadExactAsync(pipe, length, cts.Token).ConfigureAwait(false);
        return (opcode, Encoding.UTF8.GetString(body));
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct)
                .ConfigureAwait(false);
            if (read == 0) throw new IOException("Discord IPC pipe closed.");
            offset += read;
        }
        return buffer;
    }

    private static bool IsErrorResponse(string json, out string message)
    {
        message = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("evt", out var evt) &&
                string.Equals(evt.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("message", out var msg))
                    message = msg.GetString() ?? "";
                return true;
            }
        }
        catch { /* unparsable — treat as success */ }
        return false;
    }

    private void SetStatus(string status)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_status, status, StringComparison.Ordinal);
            if (changed) _status = status;
        }
        if (changed)
        {
            try { StatusChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* UI handler must never break presence */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
