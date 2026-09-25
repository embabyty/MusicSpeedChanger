using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NAudio.Wave;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using MusicSpeedChanger.Audio;
using MusicSpeedChanger.Controls;
using MusicSpeedChanger.Services;

namespace MusicSpeedChanger;

public sealed partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly Brush _reverseActiveBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6A, 0x3F, 0xB5));
    private Brush? _reverseIdleBackground;
    private bool _seekDragging;
    private bool _updatingSeek;
    private bool _exporting;

    // ----- Navigation -----
    private Brush? _navIdleBackground;

    // ----- Effects chain UI state (EQ view) -----
    private bool _rebuildingFx;

    // ----- Settings / appearance / updates -----
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly UISettings _uiSettings = new();

    // ----- Queue -----
    private readonly ObservableCollection<TrackItem> _tracks = new();
    private bool _playlistSync;
    private bool _playlistAutoPlay = true;

    // ----- Playback modes (shuffle / repeat) -----
    private bool _shuffle;
    private int _repeatMode; // 0 = Off, 1 = Repeat All, 2 = Repeat One
    private const int RepeatOff = 0, RepeatAll = 1, RepeatOne = 2;
    private readonly Random _rng = new();
    private Brush? _shuffleIdleBackground;

    // ----- Windows media controls (Action Center / flyout / lock screen) -----
    private readonly SmtcService _smtc = new();
    private DateTime _lastSmtcTimeline = DateTime.MinValue;

    // ----- Beta gate (Patreon login required to run beta builds) -----
    private bool _betaGateActive;
    private static readonly string[] AudioExtensions =
        { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".flac" };

    // Preset gains are stored 31-band; resampled for 15-band blocks.
    private static readonly Dictionary<string, float[]> EqPresets = new()
    {
        ["Flat"] = new float[31],
        ["Rock"] = new float[31] { 5,5,4,4,3,3,2,1,0,-1,-2,-2,-1,-1,0,0,1,1,1,1,2,2,2,3,3,4,4,5,5,5,6 },
        ["Pop"] = new float[31] { 3,3,2,2,1,1,0,0,-1,-1,-1,0,0,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,3,3,3 },
        ["Jazz"] = new float[31] { 4,4,3,3,2,2,1,1,1,1,1,2,2,2,2,2,1,1,2,2,2,3,3,3,3,3,2,2,2,1,1 },
        ["Classical"] = new float[31] { 4,3,3,2,2,1,1,0,0,0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,3,3,4,4,4,5,5 },
        ["Dance"] = new float[31] { 7,6,6,5,5,4,3,2,1,0,-1,-1,0,0,1,1,1,2,2,2,3,3,4,4,5,5,6,6,5,5,4 },
        ["Bass Boost"] = new float[31] { 8,8,7,7,6,6,5,5,4,3,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 },
        ["Treble Boost"] = new float[31] { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,2,2,3,3,4,5,6,7,7,8 },
        ["Vocal"] = new float[31] { -3,-3,-2,-2,-1,-1,0,0,0,1,1,1,2,2,2,3,3,3,4,4,4,4,3,3,2,2,1,1,0,0,0 },
    };

    public MainWindow()
    {
        InitializeComponent();
        _reverseIdleBackground = ReverseButton.Background;
        _shuffleIdleBackground = ShuffleButton.Background;

        // Mica backdrop: visible in the transparent title-bar strip (content keeps
        // its dark background below). Falls back to a solid color off Windows 11.
        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();

        // WinUI windows have no XAML Width/Height — size the AppWindow instead.
        TrySizeWindow(1120, 950);
        TrySetIcon();

        ApplyStartupState();
        FileListView.ItemsSource = _tracks;
        RestoreEffects();
        RebuildEffectsPanel();
        _navIdleBackground = NavPlayerButton.Background;
        ShowView("player");
        ApplyAccent();
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_settings.UseSystemAccent) ApplyAccent();
        });

        // Suppress live-seek feedback while the user drags the seek slider.
        SeekSlider.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seekDragging = true), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                _seekDragging = false;
                if (_engine.IsLoaded)
                    _engine.Progress = SeekSlider.Value / 1000.0;
                RefreshPosition();
                RefreshSmtcTimeline(force: true);
            }), true);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => RefreshPosition();
        _timer.Start();

        _shuffle = _settings.ShuffleEnabled;
        _repeatMode = Math.Clamp(_settings.RepeatMode, RepeatOff, RepeatOne);

        _engine.PlaybackEnded += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            await OnTrackEndedAsync();
        });

        Closed += (_, _) =>
        {
            CaptureEffectState();
            CapturePlaylistState();
            _settings.ShuffleEnabled = _shuffle;
            _settings.RepeatMode = _repeatMode;
            _settings.Save();
            _smtc.Dispose();
            _engine.Dispose();
        };
        UpdateTransportState();
        UpdateShuffleRepeatUi();
        UpdateNextUp();
        InitSmtc();
        MaybeShowBetaGateAsync();

        if (_settings.AutoCheckUpdates)
            CheckForUpdatesOnStartupAsync();

        RestoreFileListAsync();
    }

    private void TrySizeWindow(int width, int height)
    {
        try
        {
            // Extra height for the custom 40px title-bar strip (content size + caption).
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height + 40));
        }
        catch { /* ignore — window manager decides */ }
    }

    private void TrySetIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path))
                AppWindow.SetIcon(path);
        }
        catch { /* icon is optional */ }
    }

    private void StyleCaptionButtons()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = Colors.White;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 0xB8, 0xB8, 0xD0);
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
            tb.ButtonHoverForegroundColor = Colors.White;
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(70, 255, 255, 255);
        }
        catch { /* system draws the default caption buttons */ }
    }

    private IntPtr WindowHandle => WindowNative.GetWindowHandle(this);

    // ---------- Settings ----------

    /// <summary>One-time restore of persisted UI state (slider positions, panels, steps).</summary>
    private void ApplyStartupState()
    {
        if (_settings.RememberEffects)
        {
            TempoSlider.Value = _settings.LastTempoPercent;
            PitchSlider.Value = _settings.LastPitchSemitones;
            VolumeSlider.Value = _settings.LastVolumePercent;
        }
        ApplyLiveSettings();
    }

    /// <summary>Applies settings that take effect immediately (also after the dialog closes).</summary>
    private void ApplyLiveSettings()
    {
        TempoSlider.StepFrequency = _settings.TempoSliderStep;
        PitchSlider.StepFrequency = _settings.PitchSliderStep;
        TempoCard.Visibility = _settings.ShowTempoPanel ? Visibility.Visible : Visibility.Collapsed;
        PitchCard.Visibility = _settings.ShowPitchPanel ? Visibility.Visible : Visibility.Collapsed;
        LoopCard.Visibility = _settings.ShowLoopPanel ? Visibility.Visible : Visibility.Collapsed;
        if (NavEqButton != null)
            NavEqButton.Visibility = _settings.ShowEqPanel ? Visibility.Visible : Visibility.Collapsed;
        Waveform.IsSeekEnabled = _settings.ClickToSeek;
        ToolTipService.SetToolTip(Waveform, null);
    }

    /// <summary>Restore the effects chain (or migrate pre-chain EQ settings once).</summary>
    private void RestoreEffects()
    {
        if (_settings.RememberEffects && _settings.EffectChain.Count > 0)
        {
            _engine.ReplaceEffects(_settings.EffectChain);
        }
        else if (_settings.RememberEffects && _settings.LastEqGains != null &&
                 _settings.LastEqGains.Length == GraphicEqualizer.BandCount &&
                 _settings.LastEqGains.Any(g => Math.Abs(g) > 0.001f))
        {
            _engine.ReplaceEffects(new[]
            {
                new EffectBlock
                {
                    Kind = "eq",
                    Enabled = _settings.LastEqEnabled,
                    Bands = GraphicEqualizer.BandCount,
                    Gains = (float[])_settings.LastEqGains.Clone(),
                    Preset = "",
                }
            });
            _settings.LastEqGains = null;
        }
        else if (_engine.SnapshotEffects().Count == 0)
        {
            _engine.ReplaceEffects(new[] { EffectBlock.NewEq() });
        }
    }

    private void SaveFxSettings()
    {
        _settings.EffectChain = _engine.SnapshotEffects();
        _settings.Save();
    }

    private void CaptureEffectState()
    {
        _settings.LastTempoPercent = TempoSlider.Value;
        _settings.LastPitchSemitones = PitchSlider.Value;
        _settings.LastVolumePercent = VolumeSlider.Value;
        _settings.EffectChain = _engine.SnapshotEffects();
    }

    private void ApplyAccent()
    {
        Windows.UI.Color accent = _settings.UseSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.Accent)
            : ParseHex(_settings.CustomAccentHex, Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
        var brush = new SolidColorBrush(accent);
        StatusLabel.Foreground = brush;
        EffectiveTimeLabel.Foreground = brush;
        Waveform.PlayedColor = accent;
    }

    private static Windows.UI.Color ParseHex(string hex, Windows.UI.Color fallback)
    {
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        catch { /* fall through */ }
        return fallback;
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
        dlg.InstallUpdateRequested += async (_, info) =>
        {
            dlg.Hide();
            await DownloadAndInstallAsync(info);
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.CopyFrom(dlg.Draft);
            ApplyLiveSettings();
            ApplyAccent();
            _settings.Save();
        }
    }

    // ---------- Automatic updates ----------

    private async void CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (_betaGateActive) return; // gated: user verifies (or exits) first
            var info = await UpdateService.CheckForUpdateAsync(_settings.UpdateFeedUrl, _settings.IncludeBetaUpdates);
            if (info == null) return;
            await PromptUpdateAsync(info);
        }
        catch { /* silent: updates are best-effort */ }
    }

    private async Task PromptUpdateAsync(UpdateInfo info)
    {
        // Beta builds are Patreon-gated: supporters unlock them in Settings.
        if (info.IsBeta && !_settings.BetaAccessUnlocked)
        {
            await PromptBetaGateAsync(info);
            return;
        }
        var dlg = new ContentDialog
        {
            Title = info.IsBeta ? $"Beta update available — {info.Version}" : $"Update available — {info.Version}",
            Content = string.IsNullOrWhiteSpace(info.Notes)
                ? $"Version {info.Version} is ready to install."
                : $"Version {info.Version} is ready to install.\n\n{info.Notes}",
            PrimaryButtonText = "Download & install",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await DownloadAndInstallAsync(info);
    }

    /// <summary>Verified Patreon gate for beta builds (OAuth login + active-membership check).</summary>
    private async Task PromptBetaGateAsync(UpdateInfo info)
    {
        // A stored login re-verifies silently — no browser needed.
        if (!string.IsNullOrEmpty(_settings.PatreonRefreshToken))
        {
            StatusLabel.Text = "Re-verifying Patreon membership…";
            var silent = await PatreonAuthService.RefreshAndVerifyAsync(_settings.PatreonRefreshToken);
            if (silent != null)
            {
                ApplyPatreonAccount(silent);
                StatusLabel.Text = "";
                await PromptUpdateAsync(info); // unlocked now → normal prompt
                return;
            }
            ClearPatreonLink(); // token dead or membership lapsed
            StatusLabel.Text = "";
        }
        var dlg = new ContentDialog
        {
            Title = $"Beta {info.Version} is for Patreon supporters",
            Content = string.IsNullOrWhiteSpace(info.Notes)
                ? "Beta builds are gated for Patreon supporters.\nLog in to verify your membership, or wait for the stable release."
                : $"Beta {info.Version} is gated for Patreon supporters.\n\n{info.Notes}\n\nLog in to verify your membership, or wait for the stable release.",
            PrimaryButtonText = "Login with Patreon",
            SecondaryButtonText = "Open Patreon page",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            try { await Launcher.LaunchUriAsync(new Uri(UpdateService.PatreonPageUrl)); }
            catch { /* ignore */ }
        }
        else if (result == ContentDialogResult.Primary)
        {
            var statusProgress = new Progress<string>(s => StatusLabel.Text = s);
            var (account, error) = await PatreonAuthService.LoginAsync(statusProgress);
            if (account == null)
            {
                StatusLabel.Text = "";
                await ShowErrorAsync(error ?? "Patreon login didn't complete.");
                return;
            }
            ApplyPatreonAccount(account);
            StatusLabel.Text = "";
            await DownloadAndInstallAsync(info);
        }
    }

    private void ApplyPatreonAccount(PatreonAccount account)
    {
        _settings.BetaAccessUnlocked = true;
        _settings.IncludeBetaUpdates = true;
        _settings.PatreonRefreshToken = account.RefreshToken;
        _settings.PatreonFullName = account.FullName;
        _settings.Save();
    }

    private void ClearPatreonLink()
    {
        _settings.BetaAccessUnlocked = false;
        _settings.PatreonRefreshToken = null;
        _settings.PatreonFullName = null;
        _settings.Save();
    }

    // ---------- Beta gate (startup Patreon requirement for beta builds) ----------

    private static bool IsBetaBuild() =>
        UpdateService.DisplayVersion.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Beta builds require an active Patreon membership to run at all. Linked
    /// users re-verify silently; everyone else gets the blocking gate overlay
    /// (no dismiss affordance — login or exit). Stable builds skip this entirely.
    /// </summary>
    private async void MaybeShowBetaGateAsync()
    {
        try
        {
            if (!IsBetaBuild() || BetaGateOverlay == null) return;
            if (!string.IsNullOrEmpty(_settings.PatreonRefreshToken))
            {
                var silent = await PatreonAuthService.RefreshAndVerifyAsync(_settings.PatreonRefreshToken);
                if (silent != null)
                {
                    ApplyPatreonAccount(silent);
                    return;
                }
                ClearPatreonLink();
            }
            _betaGateActive = true;
            BetaGateOverlay.Visibility = Visibility.Visible;
        }
        catch { /* gate is best-effort; a failure here must not crash startup */ }
    }

    private async void BetaGateLoginButton_Click(object sender, RoutedEventArgs e)
    {
        BetaGateLoginButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => BetaGateStatus.Text = s);
            var (account, error) = await PatreonAuthService.LoginAsync(progress);
            if (account == null)
            {
                BetaGateStatus.Text = error ?? "Login didn't complete.";
                return;
            }
            ApplyPatreonAccount(account);
            _betaGateActive = false;
            BetaGateOverlay.Visibility = Visibility.Collapsed;
        }
        finally { BetaGateLoginButton.IsEnabled = true; }
    }

    private void BetaGateDeclineButton_Click(object sender, RoutedEventArgs e) =>
        Application.Current.Exit();

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        var progress = new Progress<double>(p => StatusLabel.Text = $"Downloading update… {p * 100:0}%");
        StatusLabel.Text = "Downloading update…";
        try
        {
            string dest = Path.Combine(Path.GetTempPath(), info.FileName);
            await UpdateService.DownloadAsync(info.DownloadUrl, dest, progress);
            StatusLabel.Text = "Installing update…";
            Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Update failed";
            await ShowErrorAsync($"Update failed:\n{ex.Message}");
        }
    }

    // ---------- File ----------

    private FileOpenPicker CreateAudioPicker()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (var ext in AudioExtensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowHandle);
        return picker;
    }

    // ---------- Queue ----------

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var files = await CreateAudioPicker().PickMultipleFilesAsync();
        if (files.Count == 0) return;
        await AddFiles(files.Select(f => f.Path), select: true, autoplay: false);
    }

    private async Task AddFiles(IEnumerable<string> paths, bool select, bool autoplay)
    {
        var incoming = paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) &&
                        AudioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (incoming.Count == 0) return;

        foreach (var p in incoming)
        {
            if (_tracks.Any(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase)))
                continue;
            _tracks.Add(new TrackItem(p));
        }
        UpdatePlaylistUi();

        // Resolve durations lazily so adding stays instant.
        foreach (var t in _tracks.Where(t => t.DurationText == "…").ToList())
            t.DurationText = await Task.Run(() => TryGetDurationText(t.Path));

        if (select)
        {
            var target = _tracks.LastOrDefault(t => incoming.Any(p =>
                string.Equals(p, t.Path, StringComparison.OrdinalIgnoreCase)));
            if (target == null) return;
            if (ReferenceEquals(FileListView.SelectedItem, target))
                await LoadTrackAsync(target, autoplay);
            else
            {
                _playlistAutoPlay = autoplay;
                try { FileListView.SelectedItem = target; }
                finally { _playlistAutoPlay = true; }
            }
        }
    }

    private async void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_playlistSync) return;
        if (FileListView.SelectedItem is not TrackItem item) return;
        await LoadTrackAsync(item, _playlistAutoPlay);
        UpdateNextUp();
    }

    private async Task LoadTrackAsync(TrackItem item, bool autoplay)
    {
        _settings.LastFilePath = item.Path;
        await LoadFileAsync(item.Path);
        if (autoplay && _engine.IsLoaded && _engine.FilePath == item.Path)
        {
            _engine.Play();
            SetPlayIcon(playing: true);
            RefreshSmtcPlayback();
        }
    }

    private void RemoveFileButton_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (FileListView.SelectedItem is TrackItem item)
            _tracks.Remove(item);
        UpdatePlaylistUi();
    }

    private void ClearFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0) return;
        _playlistSync = true;
        try
        {
            _tracks.Clear();
            FileListView.SelectedItem = null;
        }
        finally { _playlistSync = false; }
        UpdatePlaylistUi();
    }

    private void FileListView_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void FileList_DragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = DataPackageOperation.Copy;

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            await AddFiles(items.OfType<StorageFile>().Select(f => f.Path), select: true, autoplay: false);
        }
        catch { /* drop is best-effort */ }
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSelection(-1, wrap: _repeatMode == RepeatAll);

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0) return;
        int current = FileListView.SelectedIndex;
        int next = PickNextIndex(current, wrap: _repeatMode == RepeatAll);
        if (next < 0) return;
        if (next == current && _tracks[next] is TrackItem same)
            await LoadTrackAsync(same, autoplay: true);
        else
            FileListView.SelectedIndex = next; // SelectionChanged auto-plays
    }

    private async Task MoveSelection(int delta, bool wrap = false)
    {
        if (_tracks.Count == 0) return;
        int i = FileListView.SelectedIndex;
        if (i < 0)
            i = delta > 0 ? 0 : _tracks.Count - 1;
        else if (wrap)
            i = (i + delta + _tracks.Count) % _tracks.Count;
        else
            i = Math.Clamp(i + delta, 0, _tracks.Count - 1);
        if (i == FileListView.SelectedIndex && _tracks[i] is TrackItem same)
            await LoadTrackAsync(same, autoplay: true);
        else
            FileListView.SelectedIndex = i; // SelectionChanged auto-plays
    }

    private void UpdatePlaylistUi()
    {
        if (PrevButton == null) return;
        bool any = _tracks.Count > 0;
        PrevButton.IsEnabled = any;
        NextButton.IsEnabled = any;
        ShuffleButton.IsEnabled = any;
        RepeatButton.IsEnabled = true; // Repeat One works even on a single loaded file
        _smtc.SetNextPreviousEnabled(CanGoNext(), CanGoPrevious());
        UpdateNextUp();
    }

    // ---------- Shuffle / repeat ----------

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        _settings.ShuffleEnabled = _shuffle;
        _settings.Save();
        UpdateShuffleRepeatUi();
        UpdateNextUp();
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = (_repeatMode + 1) % 3;
        _settings.RepeatMode = _repeatMode;
        _settings.Save();
        UpdateShuffleRepeatUi();
        UpdateNextUp();
    }

    private void UpdateShuffleRepeatUi()
    {
        if (ShuffleButton == null || RepeatButton == null) return;
        ShuffleButton.Background = _shuffle ? _reverseActiveBackground : _shuffleIdleBackground;
        if (RepeatIcon != null)
        {
            RepeatIcon.Glyph = _repeatMode == RepeatOne ? "\uE8ED" : "\uE8EE";
            RepeatIcon.Foreground = _repeatMode != RepeatOff
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A));
        }
        RepeatButton.Background = _repeatMode != RepeatOff ? _reverseActiveBackground : _shuffleIdleBackground;
        _smtc.UpdateShuffleRepeat(_shuffle, _repeatMode);
        _smtc.SetNextPreviousEnabled(CanGoNext(), CanGoPrevious());
    }

    /// <summary>Index of the track that follows <paramref name="current"/>; -1 = stop.</summary>
    private int PickNextIndex(int current, bool wrap)
    {
        if (_tracks.Count == 0) return -1;
        if (_shuffle && _tracks.Count > 1)
        {
            int next;
            do { next = _rng.Next(_tracks.Count); } while (next == current);
            return next;
        }
        int sequential = current < 0 ? 0 : current + 1;
        if (sequential < _tracks.Count) return sequential;
        return wrap ? 0 : -1;
    }

    private async Task OnTrackEndedAsync()
    {
        // Repeat One: restart the same track.
        if (_repeatMode == RepeatOne && _engine.IsLoaded)
        {
            _engine.Progress = 0;
            _engine.Play();
            SetPlayIcon(playing: true);
            RefreshPosition();
            RefreshSmtcPlayback();
            return;
        }
        int next = PickNextIndex(FileListView.SelectedIndex, wrap: _repeatMode == RepeatAll);
        if (next < 0)
        {
            _engine.Stop();
            SetPlayIcon(playing: false);
            RefreshPosition();
            RefreshSmtcPlayback();
            return;
        }
        if (next == FileListView.SelectedIndex && _tracks[next] is TrackItem same)
            await LoadTrackAsync(same, autoplay: true);
        else
            FileListView.SelectedIndex = next; // SelectionChanged auto-plays
    }

    private void UpdateNextUp()
    {
        if (NextUpLabel == null) return;
        if (_tracks.Count == 0) { NextUpLabel.Text = ""; return; }
        int current = FileListView.SelectedIndex;
        if (_repeatMode == RepeatOne && current >= 0 && current < _tracks.Count)
        {
            NextUpLabel.Text = $"🔂 Repeating: {_tracks[current].Name}";
            return;
        }
        if (_shuffle)
        {
            NextUpLabel.Text = _tracks.Count > 1
                ? "🔀 Shuffle on — random up next"
                : "🔀 Shuffle on";
            return;
        }
        int next = current + 1;
        if (next < _tracks.Count)
            NextUpLabel.Text = $"Playing Next: {_tracks[next].Name}";
        else if (_repeatMode == RepeatAll)
            NextUpLabel.Text = $"Playing Next: {_tracks[0].Name} (repeat all)";
        else
            NextUpLabel.Text = current >= 0 ? "End of files" : "";
    }

    private void CapturePlaylistState()
    {
        if (_settings.RememberFileList)
            _settings.FileListPaths = _tracks.Select(t => t.Path).ToList();
        else
        {
            _settings.FileListPaths = new List<string>();
            _settings.LastFilePath = null;
        }
    }

    private async void RestoreFileListAsync()
    {
        try
        {
            if (!_settings.RememberFileList) return;
            var paths = _settings.FileListPaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) return;

            _playlistSync = true;
            _playlistAutoPlay = false;
            try
            {
                foreach (var p in paths)
                    _tracks.Add(new TrackItem(p));
                var last = _tracks.FirstOrDefault(t =>
                    string.Equals(t.Path, _settings.LastFilePath, StringComparison.OrdinalIgnoreCase))
                    ?? _tracks[^1];
                FileListView.SelectedItem = last;
            }
            finally
            {
                _playlistSync = false;
                _playlistAutoPlay = true;
            }
            UpdatePlaylistUi();

            foreach (var t in _tracks.ToList())
                t.DurationText = await Task.Run(() => TryGetDurationText(t.Path));

            // Resume paused on the last track.
            if (FileListView.SelectedItem is TrackItem selected)
                await LoadFileAsync(selected.Path);
        }
        catch { /* startup restore is best-effort */ }
    }

    private static string TryGetDurationText(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using WaveStream reader = ext == ".wav"
                ? new WaveFileReader(path)
                : new MediaFoundationReader(path);
            return FormatTime(reader.TotalTime);
        }
        catch { return "--:--"; }
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            StatusLabel.Text = "Loading…";
            await Task.Run(() => _engine.Load(path));

            // Apply current UI settings to the fresh engine
            if (!_settings.RememberEffects && _settings.ApplyDefaultsOnFileLoad)
            {
                TempoSlider.Value = _settings.DefaultTempoPercent;
                PitchSlider.Value = _settings.DefaultPitchSemitones;
            }
            _engine.Tempo = TempoSlider.Value / 100.0;
            _engine.PitchSemitones = PitchSlider.Value;
            _engine.Volume = (float)(VolumeSlider.Value / 100.0);

            FileLabel.Text = _engine.FileName ?? Path.GetFileName(path);
            StatusLabel.Text = "";
            Waveform.Data = null;
            ClearLoopUi();

            UpdateTransportState();
            RefreshPosition();
            UpdateEffectiveLabel();
            UpdateReverseUi();
            await RefreshSmtcForTrackAsync();

            // Build waveform in background
            string copy = path;
            var data = await Task.Run(() => WaveformData.FromFile(copy, _settings.WaveformPeaks));
            // Ignore if user opened another file meanwhile
            if (_engine.FilePath == copy)
                Waveform.Data = data;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Could not open file:\n{ex.Message}");
            StatusLabel.Text = "Open failed";
        }
    }

    // ---------- Transport ----------

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded) return;
        if (_engine.IsPlaying)
        {
            _engine.Pause();
            SetPlayIcon(playing: false);
        }
        else
        {
            _engine.Play();
            SetPlayIcon(playing: true);
        }
        RefreshSmtcPlayback();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        SetPlayIcon(playing: false);
        RefreshPosition();
        RefreshSmtcPlayback();
    }

    /// <summary>Swap the play/pause Media Player glyph.</summary>
    private void SetPlayIcon(bool playing)
    {
        if (PlayIcon != null)
            PlayIcon.Glyph = playing ? "\uE769" : "\uE768";
    }

    private async void ReverseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded) return;
        ReverseButton.IsEnabled = false;
        try
        {
            bool target = !_engine.Reverse;
            if (target) StatusLabel.Text = "Preparing reverse…";
            await Task.Run(() => _engine.SetReverse(target));
            StatusLabel.Text = "";
            RefreshPosition();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Could not enable reverse:\n{ex.Message}");
            StatusLabel.Text = "Reverse failed";
        }
        finally
        {
            ReverseButton.IsEnabled = _engine.IsLoaded;
            UpdateReverseUi();
        }
    }

    private void UpdateReverseUi()
    {
        bool on = _engine.IsLoaded && _engine.Reverse;
        ReverseButton.Content = on ? "Forward" : "Reverse";
        ReverseButton.Background = on ? _reverseActiveBackground : _reverseIdleBackground;
        if (ReverseStateLabel != null)
            ReverseStateLabel.Text = on ? "On" : "Off";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        _engine.Volume = (float)(e.NewValue / 100.0);

    // ---------- Seek ----------

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || !_engine.IsLoaded) return;
        if (_seekDragging)
            _engine.Progress = e.NewValue / 1000.0;
        else if (e.NewValue != SeekSlider.Value)
        {
            // Keyboard / programmatic change outside the timer — follow it.
            _engine.Progress = e.NewValue / 1000.0;
        }
    }

    private void Waveform_SeekRequested(object? sender, double progress)
    {
        if (!_engine.IsLoaded) return;
        _engine.Progress = progress;
        RefreshPosition();
        RefreshSmtcTimeline(force: true);
    }

    /// <summary>Mobile-style magnifier: cycles waveform zoom levels 0..10.</summary>
    private void ZoomButton_Click(object sender, RoutedEventArgs e)
    {
        Waveform.ZoomLevel = (Waveform.ZoomLevel + 1) % 11;
        ZoomBadge.Text = Waveform.ZoomLevel.ToString();
        ToolTipService.SetToolTip(ZoomButton,
            $"Waveform zoom — level {Waveform.ZoomLevel} of 10 (click to zoom in)");
    }

    private void RefreshPosition()
    {
        if (!_engine.IsLoaded) return;
        _engine.Update();

        var pos = _engine.SourcePosition;
        var dur = _engine.SourceDuration;
        CurrentTimeLabel.Text = FormatTime(pos);
        TotalTimeLabel.Text = FormatTime(dur);

        double progress = dur.TotalSeconds > 0 ? pos.TotalSeconds / dur.TotalSeconds : 0;
        _updatingSeek = true;
        try
        {
            if (!_seekDragging)
                SeekSlider.Value = Math.Clamp(progress, 0, 1) * 1000.0;
            Waveform.Progress = Math.Clamp(progress, 0, 1);
        }
        finally { _updatingSeek = false; }
        RefreshSmtcTimeline();
    }

    // ---------- Tempo / pitch ----------

    private void TempoSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TempoLabel == null) return;
        TempoLabel.Text = $"{e.NewValue:0}%";
        _engine.Tempo = e.NewValue / 100.0;
        UpdateEffectiveLabel();
        RefreshSmtcTimeline(force: true);
    }

    private void TempoPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && double.TryParse(b.Tag?.ToString(), out double v))
            TempoSlider.Value = v;
    }

    private void ResetTempo_Click(object sender, RoutedEventArgs e) => TempoSlider.Value = 100;

    private void PitchSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PitchLabel == null) return;
        PitchLabel.Text = $"{(e.NewValue >= 0 ? "+" : "")}{e.NewValue:0.0} st";
        _engine.PitchSemitones = e.NewValue;
    }

    private void PitchPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && double.TryParse(b.Tag?.ToString(), out double v))
            PitchSlider.Value = v;
    }

    private void ResetPitch_Click(object sender, RoutedEventArgs e) => PitchSlider.Value = 0;

    private void UpdateEffectiveLabel()
    {
        if (EffectiveTimeLabel == null) return;
        if (!_engine.IsLoaded) { EffectiveTimeLabel.Text = ""; return; }
        EffectiveTimeLabel.Text = $"plays as {FormatTime(_engine.OutputDuration)} @ {TempoSlider.Value:0}%";
    }

    // ---------- Loop ----------

    private void SetAButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.LoopA = _engine.SourcePosition;
        if (_engine.LoopB.HasValue && _engine.LoopB <= _engine.LoopA)
            _engine.LoopB = null;
        UpdateLoopUi(autoEnable: true);
    }

    private void SetBButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.LoopB = _engine.SourcePosition;
        if (_engine.LoopA.HasValue && _engine.LoopB <= _engine.LoopA)
        {
            // Swap so A < B
            (_engine.LoopA, _engine.LoopB) = (_engine.LoopB, _engine.LoopA);
        }
        UpdateLoopUi(autoEnable: true);
    }

    private void ClearLoopButton_Click(object sender, RoutedEventArgs e) => ClearLoopUi();

    private void LoopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _engine.LoopEnabled = LoopCheckBox.IsChecked == true;
    }

    private void UpdateLoopUi(bool autoEnable = false)
    {
        var dur = _engine.SourceDuration.TotalSeconds;
        double? a = _engine.LoopA.HasValue ? _engine.LoopA.Value.TotalSeconds / Math.Max(1e-6, dur) : null;
        double? b = _engine.LoopB.HasValue ? _engine.LoopB.Value.TotalSeconds / Math.Max(1e-6, dur) : null;
        Waveform.LoopA = a;
        Waveform.LoopB = b;

        if (_engine.LoopA.HasValue && _engine.LoopB.HasValue)
        {
            LoopLabel.Text = $"{FormatTime(_engine.LoopA.Value)} → {FormatTime(_engine.LoopB.Value)}";
            if (autoEnable)
            {
                LoopCheckBox.IsChecked = true;
                _engine.LoopEnabled = true;
            }
        }
        else if (_engine.LoopA.HasValue)
            LoopLabel.Text = $"A = {FormatTime(_engine.LoopA.Value)} (set B…)";
        else if (_engine.LoopB.HasValue)
            LoopLabel.Text = $"B = {FormatTime(_engine.LoopB.Value)} (set A…)";
        else
            LoopLabel.Text = "No loop set";
    }

    private void ClearLoopUi()
    {
        _engine.LoopA = _engine.LoopB = null;
        _engine.LoopEnabled = false;
        LoopCheckBox.IsChecked = false;
        Waveform.LoopA = Waveform.LoopB = null;
        LoopLabel.Text = "No loop set";
    }

    // ---------- Navigation (Media Player style rail) ----------

    private void NavPlayerButton_Click(object sender, RoutedEventArgs e) => ShowView("player");
    private void NavEqButton_Click(object sender, RoutedEventArgs e) => ShowView("eq");
    private void NavSettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsButton_Click(sender, e);

    private void ShowView(string view)
    {
        bool eq = view == "eq";
        if (EqView == null || PlayerView == null) return;
        EqView.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;
        PlayerView.Visibility = eq ? Visibility.Collapsed : Visibility.Visible;
        if (NavPlayerButton != null)
            NavPlayerButton.Background = !eq ? _reverseActiveBackground : _navIdleBackground;
        if (NavEqButton != null)
            NavEqButton.Background = eq ? _reverseActiveBackground : _navIdleBackground;
    }

    // ---------- Effects chain (Equalizer APO style) ----------

    private void AddEqButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.AddEffect(EffectBlock.NewEq());
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void AddPreampButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.AddEffect(EffectBlock.NewPreamp());
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void RebuildEffectsPanel()
    {
        if (EffectsPanel == null || EffectsEmptyLabel == null) return;
        _rebuildingFx = true;
        try
        {
            EffectsPanel.Children.Clear();
            var blocks = _engine.SnapshotEffects();
            EffectsEmptyLabel.Visibility = blocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < blocks.Count; i++)
                EffectsPanel.Children.Add(BuildEffectCard(blocks[i], i, blocks.Count));
        }
        finally { _rebuildingFx = false; }
        UpdateCurve();
    }

    /// <summary>Refresh the combined response graph from the live chain.</summary>
    private void UpdateCurve()
    {
        if (FxCurve == null) return;
        var blocks = _engine.SnapshotEffects();
        var curve = new List<EqCurveControl.CurveBlock>(blocks.Count);
        foreach (var b in blocks)
        {
            if (b.IsPreamp)
            {
                curve.Add(new EqCurveControl.CurveBlock
                {
                    IsPreamp = true, Enabled = b.Enabled, PreampDb = b.PreampDb,
                });
            }
            else
            {
                curve.Add(new EqCurveControl.CurveBlock
                {
                    Freqs = GraphicEqualizer.CentersForBands(b.Bands),
                    Gains = b.Gains ?? Array.Empty<float>(),
                    Q = GraphicEqualizer.QForBands(b.Bands),
                    Enabled = b.Enabled,
                });
            }
        }
        FxCurve.Update(curve, _engine.SourceSampleRate);
    }

    private Border BuildEffectCard(EffectBlock block, int index, int count)
    {
        var secondary = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A));
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var root = new StackPanel { Spacing = 6 };
        card.Child = root;

        // Header: #N, type, band radios / spacer, power, up, down, delete.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = new TextBlock
        {
            Text = $"#{index + 1}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        header.Children.Add(num);

        var title = new TextBlock
        {
            Text = block.IsPreamp ? "Preamp" : $"Graphic EQ • {block.Bands}-band",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        int col = 2;
        if (!block.IsPreamp)
        {
            var bandsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var r15 = new RadioButton { Content = "15-band", Tag = (index, GraphicEqualizer.BandCount15), IsChecked = block.Bands == GraphicEqualizer.BandCount15, VerticalAlignment = VerticalAlignment.Center };
            var r31 = new RadioButton { Content = "31-band", Tag = (index, GraphicEqualizer.BandCount), IsChecked = block.Bands != GraphicEqualizer.BandCount15, VerticalAlignment = VerticalAlignment.Center };
            r15.Checked += FxBands_Checked;
            r31.Checked += FxBands_Checked;
            bandsPanel.Children.Add(r15);
            bandsPanel.Children.Add(r31);
            Grid.SetColumn(bandsPanel, col++);
            header.Children.Add(bandsPanel);
        }

        var power = new ToggleSwitch
        {
            IsOn = block.Enabled,
            Tag = (index, (StackPanel?)null), // body filled in below
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(power, block.Enabled ? "Bypass this effect" : "Enable this effect");
        power.Toggled += FxPower_Toggled;
        Grid.SetColumn(power, col++);
        header.Children.Add(power);

        header.Children.Add(IconButton("\uE70E", "Move up", FxMove_Click, (index, -1), enabled: index > 0, col: col++));
        header.Children.Add(IconButton("\uE70D", "Move down", FxMove_Click, (index, +1), enabled: index < count - 1, col: col++));
        header.Children.Add(IconButton("\uE74D", "Remove effect", FxDelete_Click, index, col: col++));
        root.Children.Add(header);

        // Body (dimmed when bypassed).
        var body = new StackPanel { Spacing = 6, Opacity = block.Enabled ? 1.0 : 0.45 };
        power.Tag = (index, body);
        if (block.IsPreamp)
            BuildPreampBody(body, block, index);
        else
            BuildEqBody(body, block, index);
        root.Children.Add(body);

        return card;
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler click, object tag, bool enabled = true, int col = 0)
    {
        var b = new Button { Padding = new Thickness(8, 2, 8, 2), Tag = tag, IsEnabled = enabled };
        ToolTipService.SetToolTip(b, tip);
        b.Content = new FontIcon { Glyph = glyph, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14 };
        Grid.SetColumn(b, col);
        b.Click += click;
        return b;
    }

    private void BuildEqBody(StackPanel body, EffectBlock block, int index)
    {
        // Preset row.
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var presetBox = new ComboBox { Width = 150, Tag = index };
        foreach (var name in EqPresets.Keys)
            presetBox.Items.Add(name);
        presetBox.SelectedItem = EqPresets.ContainsKey(block.Preset ?? "") ? block.Preset : null;
        presetBox.SelectionChanged += FxPresetBox_SelectionChanged;
        presetRow.Children.Add(presetBox);
        var flatBtn = IconButton("\uE7A7", "Reset bands to flat", FxResetButton_Click, index);
        Grid.SetColumn(flatBtn, 0);
        presetRow.Children.Add(flatBtn);
        var hint = new TextBlock
        {
            Text = "+/-15 dB per band, 1 dB steps",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A)),
        };
        presetRow.Children.Add(hint);
        body.Children.Add(presetRow);

        // Bands.
        var freqs = GraphicEqualizer.CentersForBands(block.Bands);
        var labels = GraphicEqualizer.LabelsForBands(block.Bands);
        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        var bandsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        scroll.Content = bandsPanel;
        for (int band = 0; band < block.Bands; band++)
        {
            float gain = block.Gains != null && band < block.Gains.Length ? block.Gains[band] : 0;
            var value = new TextBlock
            {
                Text = FmtDb(gain),
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                Width = 40,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7C, 0x9E, 0xFF)),
            };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = GraphicEqualizer.MinGainDb,
                Maximum = GraphicEqualizer.MaxGainDb,
                Value = gain,
                Height = 100,
                Width = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                TickFrequency = 1,
                StepFrequency = 1,
                Tag = (index, band, presetBox),
            };
            ToolTipService.SetToolTip(slider, EqTip(freqs[band], gain));
            slider.ValueChanged += FxBandSlider_ValueChanged;
            var freq = new TextBlock
            {
                Text = labels[band],
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A)),
                TextAlignment = TextAlignment.Center,
                Width = 40,
            };
            var c = new StackPanel { Width = 40, Margin = new Thickness(1, 0, 1, 0) };
            c.Children.Add(value);
            c.Children.Add(slider);
            c.Children.Add(freq);
            bandsPanel.Children.Add(c);
        }
        body.Children.Add(scroll);
    }

    private void BuildPreampBody(StackPanel body, EffectBlock block, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var number = new NumberBox
        {
            Header = "Gain (dB)",
            Minimum = -20,
            Maximum = 20,
            Value = block.PreampDb,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 0.5,
            LargeChange = 1,
            Width = 160,
            Tag = (index, (Slider?)null),
        };
        var slider = new Slider
        {
            Minimum = -20,
            Maximum = 20,
            StepFrequency = 0.5,
            Value = block.PreampDb,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = (index, (NumberBox?)null),
        };
        number.Tag = (index, slider);
        slider.Tag = (index, number);
        number.ValueChanged += FxPreampBox_ValueChanged;
        slider.ValueChanged += FxPreampSlider_ValueChanged;
        row.Children.Add(number);
        row.Children.Add(slider);
        body.Children.Add(row);
    }

    private void FxBandSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_rebuildingFx || sender is not Slider s || s.Tag is not (int bi, int band, ComboBox preset)) return;
        var blocks = _engine.SnapshotEffects();
        if (bi < 0 || bi >= blocks.Count) return;
        var blk = blocks[bi];
        if (blk.IsPreamp || band < 0 || band >= blk.Bands) return;
        _engine.SetEffectGain(bi, band, (float)e.NewValue);
        if (s.Parent is StackPanel c && c.Children.Count > 0 && c.Children[0] is TextBlock v)
            v.Text = FmtDb(e.NewValue);
        ToolTipService.SetToolTip(s, EqTip(GraphicEqualizer.CentersForBands(blk.Bands)[band], e.NewValue));
        // Manual tweak → no longer exactly a preset (null = early return in handler).
        preset.SelectedIndex = -1;
        UpdateCurve();
    }

    private void FxPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingFx || sender is not ComboBox box || box.Tag is not int index) return;
        if (box.SelectedItem is not string name) return;
        if (!EqPresets.TryGetValue(name, out var gains)) return;
        var blocks = _engine.SnapshotEffects();
        if (index < 0 || index >= blocks.Count) return;
        var b = blocks[index];
        float[] dst = gains.Length == b.Bands
            ? gains
            : GraphicEqualizer.ResampleGains(GraphicEqualizer.CentersForBands(gains.Length),
                gains, GraphicEqualizer.CentersForBands(b.Bands));
        _engine.SetEffectGains(index, dst, name);
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void FxResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int index) return;
        _engine.ResetEffect(index);
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void FxPower_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch t || t.Tag is not (int index, StackPanel body)) return;
        _engine.SetEffectEnabled(index, t.IsOn);
        body.Opacity = t.IsOn ? 1.0 : 0.45;
        ToolTipService.SetToolTip(t, t.IsOn ? "Bypass this effect" : "Enable this effect");
        SaveFxSettings();
        UpdateCurve();
    }

    private void FxBands_Checked(object sender, RoutedEventArgs e)
    {
        if (_rebuildingFx || sender is not RadioButton r || r.Tag is not (int index, int bands)) return;
        if (r.IsChecked != true) return;
        _engine.SetEffectBands(index, bands);
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void FxMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not (int from, int delta)) return;
        _engine.MoveEffect(from, from + delta);
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void FxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int index) return;
        _engine.RemoveEffectAt(index);
        SaveFxSettings();
        RebuildEffectsPanel();
    }

    private void FxPreampBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_rebuildingFx || sender.Tag is not (int index, Slider slider)) return;
        _engine.SetPreampDb(index, (float)sender.Value);
        if (Math.Abs(slider.Value - sender.Value) > 0.001)
            slider.Value = sender.Value;
        SaveFxSettings();
        UpdateCurve();
    }

    private void FxPreampSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_rebuildingFx || sender is not Slider s || s.Tag is not (int index, NumberBox number)) return;
        _engine.SetPreampDb(index, (float)e.NewValue);
        if (Math.Abs(number.Value - e.NewValue) > 0.001)
            number.Value = e.NewValue;
        SaveFxSettings();
        UpdateCurve();
    }

    private static string FmtDb(double gain) => $"{(gain >= 0 ? "+" : "")}{gain:0.0}";

    private static string EqTip(float freq, double gain) =>
        $"{(freq >= 1000 ? $"{freq / 1000:0.##} kHz" : $"{freq:0.#} Hz")}: {(gain >= 0 ? "+" : "")}{gain:0} dB";

    // ---------- Export ----------

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded || _exporting) return;

        bool hasLoop = _engine.LoopA.HasValue && _engine.LoopB.HasValue && _engine.LoopB > _engine.LoopA;
        string suggested = Path.GetFileNameWithoutExtension(_engine.FileName ?? "track")
                    + $"_{TempoSlider.Value:0}pct_{(PitchSlider.Value >= 0 ? "+" : "")}{PitchSlider.Value:0.0}st"
                    + (_engine.EffectsActive ? "_eq" : "");

        var saveDialog = new SaveDialog(SaveDialog.SanitizeFileName(suggested), hasLoop)
        {
            XamlRoot = Content.XamlRoot,
        };
        if (await saveDialog.ShowAsync() != ContentDialogResult.Primary) return;

        string chosenName = SaveDialog.SanitizeFileName(saveDialog.FileName);
        bool saveLoopOnly = hasLoop && saveDialog.SaveLoopOnly;
        int repeat = saveLoopOnly ? saveDialog.LoopRepeat : 1;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            SuggestedFileName = chosenName,
        };
        picker.FileTypeChoices.Add("WAV audio", new List<string> { ".wav" });
        InitializeWithWindow.Initialize(picker, WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        _exporting = true;
        ExportButton.IsEnabled = false;
        bool wasPlaying = _engine.IsPlaying;
        if (wasPlaying) _engine.Pause();

        try
        {
            // Save loop region when requested, else whole track.
            TimeSpan? from = null, to = null;
            if (saveLoopOnly)
            { from = _engine.LoopA; to = _engine.LoopB; }

            var progress = new Progress<double>(p => StatusLabel.Text = $"Saving… {p * 100:0}%");
            StatusLabel.Text = "Saving…";
            // StorageFile may be brokered — use cached path when available, else the picked path.
            string dest = file.Path;
            if (string.IsNullOrEmpty(dest))
                dest = chosenName + ".wav";
            await _engine.ExportWavAsync(dest, TempoSlider.Value / 100.0, PitchSlider.Value, from, to, progress, repeat);
            StatusLabel.Text = $"Saved ✓ {Path.GetFileName(dest)}";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Save failed:\n{ex.Message}");
            StatusLabel.Text = "Save failed";
        }
        finally
        {
            _exporting = false;
            ExportButton.IsEnabled = _engine.IsLoaded;
            if (wasPlaying) _engine.Play();
        }
    }

    // ---------- Helpers ----------

    private async Task ShowErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Music Speed Changer",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch { /* last resort: status label already shows the failure */ }
    }

    private void UpdateTransportState()
    {
        bool loaded = _engine.IsLoaded;
        PlayButton.IsEnabled = loaded;
        StopButton.IsEnabled = loaded;
        ReverseButton.IsEnabled = loaded;
        ExportButton.IsEnabled = loaded;
        SetAButton.IsEnabled = loaded;
        SetBButton.IsEnabled = loaded;
        ClearLoopButton.IsEnabled = loaded;
        LoopCheckBox.IsEnabled = loaded;
        UpdateReverseUi();
        UpdatePlaylistUi();
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    // ---------- Windows media controls (SMTC / Action Center) ----------

    private void InitSmtc()
    {
        try
        {
            _smtc.Initialize(WindowHandle);
            _smtc.PlayPressed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_engine.IsLoaded && !_engine.IsPlaying)
                {
                    _engine.Play();
                    SetPlayIcon(playing: true);
                    RefreshSmtcPlayback();
                }
            });
            _smtc.PausePressed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_engine.IsPlaying)
                {
                    _engine.Pause();
                    SetPlayIcon(playing: false);
                    RefreshSmtcPlayback();
                }
            });
            _smtc.StopPressed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                _engine.Stop();
                SetPlayIcon(playing: false);
                RefreshPosition();
                RefreshSmtcPlayback();
            });
            _smtc.NextPressed += (_, _) => DispatcherQueue.TryEnqueue(async () => await SmtcNextAsync());
            _smtc.PreviousPressed += (_, _) => DispatcherQueue.TryEnqueue(async () => await MoveSelection(-1, wrap: _repeatMode == RepeatAll));
            _smtc.SeekRequested += (_, pos) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_engine.IsLoaded) return;
                _engine.SourcePosition = pos;
                RefreshPosition();
                RefreshSmtcTimeline(force: true);
            });
            _smtc.RateRequested += (_, rate) => DispatcherQueue.TryEnqueue(() =>
            {
                if (rate >= 0.25 && rate <= 2.0)
                    TempoSlider.Value = rate * 100.0;
            });
            _smtc.ShuffleRequested += (_, on) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_shuffle == on) return;
                _shuffle = on;
                _settings.ShuffleEnabled = on;
                _settings.Save();
                UpdateShuffleRepeatUi();
                UpdateNextUp();
            });
            _smtc.RepeatRequested += (_, mode) => DispatcherQueue.TryEnqueue(() =>
            {
                mode = Math.Clamp(mode, RepeatOff, RepeatOne);
                if (_repeatMode == mode) return;
                _repeatMode = mode;
                _settings.RepeatMode = mode;
                _settings.Save();
                UpdateShuffleRepeatUi();
                UpdateNextUp();
            });
            _smtc.UpdateShuffleRepeat(_shuffle, _repeatMode);
            RefreshSmtcPlayback();
        }
        catch { /* media keys are best-effort */ }
    }

    private async Task SmtcNextAsync()
    {
        if (_tracks.Count == 0) return;
        int current = FileListView.SelectedIndex;
        int next = PickNextIndex(current, wrap: _repeatMode == RepeatAll);
        if (next < 0) return;
        if (next == current && _tracks[next] is TrackItem same)
            await LoadTrackAsync(same, autoplay: true);
        else
            FileListView.SelectedIndex = next; // SelectionChanged auto-plays
    }

    private async Task RefreshSmtcForTrackAsync()
    {
        try
        {
            if (!_engine.IsLoaded || _engine.FilePath == null)
            {
                _smtc.ClearTrack();
                return;
            }
            await _smtc.UpdateTrackAsync(_engine.FilePath);
            _smtc.UpdateShuffleRepeat(_shuffle, _repeatMode);
            RefreshSmtcPlayback();
        }
        catch { /* ignore */ }
    }

    private void RefreshSmtcPlayback()
    {
        try
        {
            _smtc.UpdatePlaybackStatus(_engine.IsLoaded, _engine.IsPlaying);
            _smtc.SetNextPreviousEnabled(CanGoNext(), CanGoPrevious());
            RefreshSmtcTimeline(force: true);
        }
        catch { /* ignore */ }
    }

    private void RefreshSmtcTimeline(bool force = false)
    {
        if (!_engine.IsLoaded) return;
        var now = DateTime.UtcNow;
        if (!force && (now - _lastSmtcTimeline).TotalMilliseconds < 800) return;
        _lastSmtcTimeline = now;
        _smtc.UpdateTimeline(_engine.SourcePosition, _engine.SourceDuration,
            Math.Clamp(TempoSlider.Value / 100.0, 0.25, 3.0));
    }

    private bool CanGoNext()
    {
        if (_tracks.Count == 0) return false;
        if (_shuffle && _tracks.Count > 1) return true;
        int current = -1;
        try { current = FileListView.SelectedIndex; } catch { /* ignore */ }
        return current + 1 < _tracks.Count || _repeatMode == RepeatAll;
    }

    private bool CanGoPrevious() => _tracks.Count > 0;
}
