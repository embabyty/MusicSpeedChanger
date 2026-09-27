using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace MusicSpeedChanger.Services;

/// <summary>
/// Exposes the current song + transport state to Windows (Action Center /
/// volume flyout / lock screen media controls) via System Media Transport
/// Controls (SMTC). Works for unpackaged WinUI 3 apps through GetForWindow.
/// All methods are best-effort and never throw.
/// </summary>
public sealed class SmtcService : IDisposable
{
    private SystemMediaTransportControls? _smtc;
    private bool _disposed;

    public event EventHandler? PlayPressed;
    public event EventHandler? PausePressed;
    public event EventHandler? StopPressed;
    public event EventHandler? NextPressed;
    public event EventHandler? PreviousPressed;
    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<double>? RateRequested;
    public event EventHandler<bool>? ShuffleRequested;
    public event EventHandler<int>? RepeatRequested; // 0 = Off, 1 = All, 2 = One

    public bool IsInitialized => _smtc != null;

    /// <summary>Bind SMTC to a WinUI window. Call once after the window exists.</summary>
    public void Initialize(IntPtr hwnd)
    {
        if (_smtc != null || hwnd == IntPtr.Zero) return;
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            // Seeking from the flyout timeline writes here.
            _smtc.IsRewindEnabled = false;
            _smtc.IsFastForwardEnabled = false;
            _smtc.IsRecordEnabled = false;
            _smtc.IsChannelUpEnabled = false;
            _smtc.IsChannelDownEnabled = false;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
            _smtc.PlaybackRateChangeRequested += OnPlaybackRateChangeRequested;
            _smtc.ShuffleEnabledChangeRequested += OnShuffleChangeRequested;
            _smtc.AutoRepeatModeChangeRequested += OnRepeatChangeRequested;
        }
        catch
        {
            _smtc = null;
        }
    }

    /// <summary>Publish title/artist/album + artwork for the given file.</summary>
    public async Task UpdateTrackAsync(string? filePath)
    {
        var smtc = _smtc;
        if (smtc == null || string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            string title = Path.GetFileNameWithoutExtension(filePath);
            string artist = "";
            string album = "";
            RandomAccessStreamReference? thumbnail = null;

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var music = await file.Properties.GetMusicPropertiesAsync();
                if (!string.IsNullOrWhiteSpace(music.Title))
                    title = music.Title.Trim();
                if (!string.IsNullOrWhiteSpace(music.Artist))
                    artist = music.Artist.Trim();
                if (!string.IsNullOrWhiteSpace(music.Album))
                    album = music.Album.Trim();

                try
                {
                    var thumb = await file.GetThumbnailAsync(
                        ThumbnailMode.MusicView, 512, ThumbnailOptions.UseCurrentScale);
                    if (thumb != null && thumb.Size > 0)
                        thumbnail = (RandomAccessStreamReference)RandomAccessStreamReference.CreateFromStream(thumb);
                }
                catch { /* artwork is optional */ }
            }
            catch { /* file tags are optional — fall back to the file name */ }

            if (thumbnail == null)
                thumbnail = await TryGetAppLogoAsync();

            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = "MusicSpeedChanger";
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            if (thumbnail != null)
            {
                try { updater.Thumbnail = thumbnail; }
                catch { /* older builds may reject some sources */ }
            }
            updater.Update();
        }
        catch { /* never break playback for metadata */ }
    }

    /// <summary>Clear the flyout (no file loaded).</summary>
    public void ClearTrack()
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            var updater = smtc.DisplayUpdater;
            updater.ClearAll();
            updater.Update();
        }
        catch { /* ignore */ }
    }

    public void UpdatePlaybackStatus(bool isLoaded, bool isPlaying)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = !isLoaded
                ? MediaPlaybackStatus.Closed
                : isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch { /* ignore */ }
    }

    public void SetNextPreviousEnabled(bool next, bool previous)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            smtc.IsNextEnabled = next;
            smtc.IsPreviousEnabled = previous;
        }
        catch { /* ignore */ }
    }

    public void UpdateShuffleRepeat(bool shuffle, int repeatMode)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            // repeatMode: 0 = Off, 1 = All, 2 = One.
            smtc.AutoRepeatMode = repeatMode switch
            {
                2 => MediaPlaybackAutoRepeatMode.Track,
                1 => MediaPlaybackAutoRepeatMode.List,
                _ => MediaPlaybackAutoRepeatMode.None,
            };
        }
        catch { /* not supported on older builds */ }
        try { smtc.ShuffleEnabled = shuffle; }
        catch { /* ignore */ }
    }

    /// <summary>Push position/duration so the flyout progress bar moves.</summary>
    public void UpdateTimeline(TimeSpan position, TimeSpan duration, double rate = 1.0)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        try
        {
            if (duration <= TimeSpan.Zero) return;
            position = Clamp(position, TimeSpan.Zero, duration);
            if (rate is <= 0 or > 8) rate = 1.0;
            try { smtc.PlaybackRate = rate; } catch { /* ignore */ }
            var timeline = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = position,
                MaxSeekTime = duration,
                EndTime = duration,
            };
            smtc.UpdateTimelineProperties(timeline);
        }
        catch { /* ignore */ }
    }

    private static async Task<RandomAccessStreamReference?> TryGetAppLogoAsync()
    {
        try
        {
            string logo = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicSpeed.png");
            if (File.Exists(logo))
            {
                var sf = await StorageFile.GetFileFromPathAsync(logo);
                return (RandomAccessStreamReference)RandomAccessStreamReference.CreateFromFile(sf);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    PlayPressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    PausePressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    StopPressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextPressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PreviousPressed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch { /* ignore */ }
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args)
    {
        try { SeekRequested?.Invoke(this, args.RequestedPlaybackPosition); }
        catch { /* ignore */ }
    }

    private void OnPlaybackRateChangeRequested(SystemMediaTransportControls sender,
        PlaybackRateChangeRequestedEventArgs args)
    {
        try { RateRequested?.Invoke(this, args.RequestedPlaybackRate); }
        catch { /* ignore */ }
    }

    private void OnShuffleChangeRequested(SystemMediaTransportControls sender,
        ShuffleEnabledChangeRequestedEventArgs args)
    {
        try { ShuffleRequested?.Invoke(this, args.RequestedShuffleEnabled); }
        catch { /* ignore */ }
    }

    private void OnRepeatChangeRequested(SystemMediaTransportControls sender,
        AutoRepeatModeChangeRequestedEventArgs args)
    {
        try
        {
            int mode = args.RequestedAutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => 2,
                MediaPlaybackAutoRepeatMode.List => 1,
                _ => 0,
            };
            RepeatRequested?.Invoke(this, mode);
        }
        catch { /* ignore */ }
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) =>
        v < min ? min : v > max ? max : v;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var smtc = _smtc;
        _smtc = null;
        if (smtc == null) return;
        try
        {
            smtc.ButtonPressed -= OnButtonPressed;
            smtc.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
            smtc.PlaybackRateChangeRequested -= OnPlaybackRateChangeRequested;
            smtc.ShuffleEnabledChangeRequested -= OnShuffleChangeRequested;
            smtc.AutoRepeatModeChangeRequested -= OnRepeatChangeRequested;
            smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        }
        catch { /* ignore */ }
    }
}
