using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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
    private AudioEngine _engine = new();
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

    // ----- AutoMix (Spotify-style customizable transitions) -----
    // Dual-engine beat-synced and styled crossfade: the outgoing track keeps playing on
    // _engine while the incoming track fades in on _mixNext. Supports Bass Swap, Equal Power
    // Blend, Rise / High-Pass sweeps, Beat Drop, and Linear Crossfade.
    private AudioEngine? _mixNext;
    private int _mixNextIndex = -1;
    private bool _mixStarting;
    private readonly System.Diagnostics.Stopwatch _mixStopwatch = new();
    private double _mixDuration = 5;
    private double _mixTempoRatio = 1;
    private string? _mixBeatInfo;
    private string? _mixPrewarmedKey;
    private DateTime _lastMixFinishedTime = DateTime.MinValue;
    private int _mixSessionId = 0;
    private bool _isFinishingMix = false;
    private Brush? _autoMixIdleBackground;

    // ----- Windows media controls (Action Center / flyout / lock screen) -----
    private readonly SmtcService _smtc = new();
    private DateTime _lastSmtcTimeline = DateTime.MinValue;

    // ----- Taskbar progress (Cider-style song progress on the taskbar button) -----
    private readonly TaskbarProgressService _taskbar = new();

    // ----- Discord Rich Presence (Cider-style track status on the Discord profile) -----
    private readonly DiscordPresenceService _discord = new();
    private DateTime _lastDiscordPush = DateTime.MinValue;

    // ----- Beta gate (Patreon login required to run beta builds) -----
    private bool _betaGateActive;

    // ----- MSC Insider Hub (Beta + weekly Canary for Patreon supporters) -----
    private UpdateInfo? _pendingInsiderBeta;
    private UpdateInfo? _pendingInsiderCanary;
    private bool _refreshingInsider;
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
        _autoMixIdleBackground = ShuffleButton.Background;

        // Mica backdrop: visible in the transparent title-bar strip (content keeps
        // its dark background below). Falls back to a solid color off Windows 11.
        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarFull);
        StyleCaptionButtons();

        // WinUI windows have no XAML Width/Height — size the AppWindow instead.
        TrySizeWindow(1120, 950);
        TrySetIcon();

        ApplyStartupState();
        FileListView.ItemsSource = _tracks;
        RestoreEffects();
        RebuildEffectsPanel();
        _navIdleBackground = NavPlayerButton.Background;
        InitSettingsView();
        ShowView("player");
        UpdateInsiderVisibility();
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
                RefreshDiscordPresence(force: true);
            }), true);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => RefreshPosition();
        _timer.Start();

        _shuffle = _settings.ShuffleEnabled;
        _repeatMode = Math.Clamp(_settings.RepeatMode, RepeatOff, RepeatOne);

        _engine.PlaybackEnded += OnEnginePlaybackEnded;

        Closed += (_, _) =>
        {
            CaptureEffectState();
            CapturePlaylistState();
            _settings.ShuffleEnabled = _shuffle;
            _settings.RepeatMode = _repeatMode;
            _settings.Save();
            _smtc.Dispose();
            try { _taskbar.Clear(WindowHandle); } catch { /* ignore */ }
            _taskbar.Dispose();
            try { _discord.Clear(); } catch { /* ignore */ }
            _discord.Dispose();
            try { _mixNext?.Dispose(); } catch { /* ignore */ }
            _engine.Dispose();
        };
        UpdateTransportState();
        UpdateShuffleRepeatUi();
        UpdateAutoMixUi();
        ApplyLayoutSettings();
        UpdateNextUp();
        InitSmtc();
        InitTaskbarThumb();
        InitDiscord();
        InitZoomMenu();
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

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);
    private const int IDC_SIZEWE = 32644;
    private const int IDC_ARROW = 32512;

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

    // ---------- Settings View (Full Page) ----------

    public sealed record SettingsNavItem(string Name, string Glyph);

    private static readonly SettingsNavItem[] SettingsNavItems =
    {
        new("About", "\uE946"),
        new("Audio", "\uE8D6"),
        new("Appearance", "\uE771"),
        new("Discord", "\uE8BD"),
    };

    private UpdateInfo? _settingsPendingUpdate;
    private bool _settingsChecking;
    private bool _settingsSwitching;
    private bool _syncingSettingsView;

    private void InitSettingsView()
    {
        if (SettingsNavList != null)
        {
            SettingsNavList.ItemsSource = SettingsNavItems;
            SettingsNavList.SelectedIndex = 0;
        }
        ShowSettingsCategory("About");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowView("settings");
    }

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromView();
        ShowView("player");
    }

    private bool _settingsSidebarExpanded = true;
    private double _settingsSidebarExpandedWidth = 220;
    private bool _isDraggingSettingsSplitter;
    private double _splitterStartX;
    private double _splitterStartWidth;

    private void SettingsSidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSettingsSidebarExpanded(!_settingsSidebarExpanded);
    }

    private void SetSettingsSidebarExpanded(bool expanded)
    {
        _settingsSidebarExpanded = expanded;
        if (SettingsSidebarColumn == null) return;

        if (expanded)
        {
            double w = _settingsSidebarExpandedWidth >= 120 ? _settingsSidebarExpandedWidth : 220;
            SettingsSidebarColumn.Width = new GridLength(w);
            if (SettingsHeaderExpanded != null) SettingsHeaderExpanded.Visibility = Visibility.Visible;
            if (SettingsHeaderCollapsed != null) SettingsHeaderCollapsed.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (SettingsSidebarColumn.ActualWidth >= 120)
                _settingsSidebarExpandedWidth = SettingsSidebarColumn.ActualWidth;
            SettingsSidebarColumn.Width = new GridLength(56);
            if (SettingsHeaderExpanded != null) SettingsHeaderExpanded.Visibility = Visibility.Collapsed;
            if (SettingsHeaderCollapsed != null) SettingsHeaderCollapsed.Visibility = Visibility.Visible;
        }
    }

    private void SettingsSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZEWE)); } catch { }
    }

    private void SettingsSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSettingsSplitter)
        {
            try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }
        }
    }

    private void SettingsSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(SettingsView);
        if (!pt.Properties.IsLeftButtonPressed) return;

        _isDraggingSettingsSplitter = true;
        _splitterStartX = pt.Position.X;
        _splitterStartWidth = SettingsSidebarColumn?.ActualWidth ?? 220;
        if (sender is UIElement el)
            el.CapturePointer(e.Pointer);
    }

    private void SettingsSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZEWE)); } catch { }

        if (!_isDraggingSettingsSplitter || SettingsSidebarColumn == null) return;
        var pt = e.GetCurrentPoint(SettingsView);
        double delta = pt.Position.X - _splitterStartX;
        double newWidth = Math.Clamp(_splitterStartWidth + delta, 56, 480);

        SettingsSidebarColumn.Width = new GridLength(newWidth);
        if (newWidth < 120)
        {
            if (_settingsSidebarExpanded)
            {
                _settingsSidebarExpanded = false;
                if (SettingsHeaderExpanded != null) SettingsHeaderExpanded.Visibility = Visibility.Collapsed;
                if (SettingsHeaderCollapsed != null) SettingsHeaderCollapsed.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _settingsSidebarExpandedWidth = newWidth;
            if (!_settingsSidebarExpanded)
            {
                _settingsSidebarExpanded = true;
                if (SettingsHeaderExpanded != null) SettingsHeaderExpanded.Visibility = Visibility.Visible;
                if (SettingsHeaderCollapsed != null) SettingsHeaderCollapsed.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SettingsSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSettingsSplitter)
        {
            _isDraggingSettingsSplitter = false;
            if (sender is UIElement el)
                el.ReleasePointerCapture(e.Pointer);
            try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }

            if (SettingsSidebarColumn != null && SettingsSidebarColumn.ActualWidth < 120)
            {
                SetSettingsSidebarExpanded(false);
            }
        }
    }

    private void SettingsSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _settingsSidebarExpandedWidth = 220;
        SetSettingsSidebarExpanded(true);
    }

    private void SettingsNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNavList?.SelectedItem is SettingsNavItem item)
        {
            if (_isSearchingSettings)
            {
                SettingsSearchBox.Text = "";
                RefreshSettingsSearchResults("");
            }
            ShowSettingsCategory(item.Name);
        }
    }

    private string _currentSettingsCategory = "About";

    /// <summary>One searchable setting: picking it jumps to its page and focuses the control.</summary>
    public sealed record SettingsSearchResult(
        string Title, string Category, string CategoryGlyph, string TargetName, string Keywords);

    // Windows-style search index over every setting (titles + keywords, not just page names).
    private static readonly SettingsSearchResult[] AllSettingsSearchResults =
    {
        new("Automatically check for updates", "About", "", nameof(SettingsAutoCheckSwitch), "update auto startup check"),
        new("Check for updates now", "About", "", nameof(SettingsCheckNowButton), "check now update download"),
        new("Update feed", "About", "", nameof(SettingsFeedUrlBox), "github releases json url feed"),
        new("Include beta updates", "About", "", nameof(SettingsBetaUpdatesBox), "beta prerelease preview"),
        new("Insider Hub channel", "About", "", nameof(SettingsInsiderChannelBox), "canary beta channel insider hub"),
        new("Login with Patreon", "About", "", nameof(SettingsPatreonLoginButton), "patreon login supporter link membership"),
        new("Switch to Beta", "About", "", nameof(SettingsSwitchToBetaButton), "beta channel switch install"),
        new("Email the owner", "About", "", nameof(SettingsEmailOwnerButton), "contact email support owner"),

        new("Default tempo", "Audio", "", nameof(SettingsDefaultTempoBox), "tempo speed default percent"),
        new("Default pitch", "Audio", "", nameof(SettingsDefaultPitchBox), "pitch key semitones default"),
        new("Reset to defaults when a file is opened", "Audio", "", nameof(SettingsApplyDefaultsBox), "defaults reset file open load"),
        new("Tempo slider step", "Audio", "", nameof(SettingsTempoStepBox), "tempo step slider increment"),
        new("Pitch slider step", "Audio", "", nameof(SettingsPitchStepBox), "pitch step slider increment semitone"),
        new("Save effects and restore them on startup", "Audio", "", nameof(SettingsRememberEffectsBox), "remember save restore effects eq tempo pitch volume startup"),
        new("Enable AutoMix transitions", "Audio", "", nameof(SettingsAutoMixEnabledBox), "automix crossfade transitions mix dj blend"),
        new("Crossfade length", "Audio", "", nameof(SettingsAutoMixSecondsBox), "automix crossfade seconds duration fade"),

        new("Show Tempo panel", "Appearance", "", nameof(SettingsShowTempoBox), "show hide tempo panel editor"),
        new("Show Pitch panel", "Appearance", "", nameof(SettingsShowPitchBox), "show hide pitch panel"),
        new("Show AB Loop panel", "Appearance", "", nameof(SettingsShowLoopBox), "show hide loop ab panel"),
        new("Show Equalizer panel", "Appearance", "", nameof(SettingsShowEqBox), "show hide equalizer eq effects panel"),
        new("Waveform detail", "Appearance", "", nameof(SettingsWaveformPeaksBox), "waveform detail bars peaks quality"),
        new("Click / drag the waveform to seek", "Appearance", "", nameof(SettingsClickToSeekBox), "click seek waveform drag"),
        new("Remember the files between sessions", "Appearance", "", nameof(SettingsRememberListBox), "remember files queue playlist restore sessions"),
        new("Layout type", "Appearance", "", nameof(SettingsLayoutTypeBox), "layout mojave mavericks calico montara sidebar dock"),
        new("Player type", "Appearance", "", nameof(SettingsPlayerTypeBox), "player comfy compact transport density"),
        new("Match the Windows accent color", "Appearance", "", nameof(SettingsUseAccentSwitch), "accent color theme windows system"),
        new("Custom accent", "Appearance", "", nameof(SettingsCustomAccentPicker), "accent custom color picker"),

        new("Show Discord Rich Presence", "Discord", "", nameof(SettingsDiscordEnabledSwitch), "discord rich presence status profile"),
        new("Discord Application ID", "Discord", "", nameof(SettingsDiscordClientIdBox), "discord application client id app portal"),
        new("Show tempo & pitch in Discord status", "Discord", "", nameof(SettingsDiscordTempoPitchBox), "discord tempo pitch show"),
        new("Reconnect Discord", "Discord", "", nameof(SettingsDiscordReconnectButton), "discord reconnect retry"),
    };

    private static List<SettingsSearchResult> SearchSettings(string query)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrEmpty(query)) return new List<SettingsSearchResult>();
        return AllSettingsSearchResults
            .Select(r => (result: r, score: ScoreSetting(r, query)))
            .Where(t => t.score >= 0)
            .OrderBy(t => t.score)
            .ThenBy(t => t.result.Title, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.result)
            .ToList();
    }

    private static int ScoreSetting(SettingsSearchResult r, string q)
    {
        if (r.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 0;
        if (r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) return 1;
        if (r.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)) return 2;
        if (r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        RefreshSettingsSearchResults(sender.Text);
    }

    private bool _isSearchingSettings;

    private void RefreshSettingsSearchResults(string query)
    {
        query = query.Trim();
        _isSearchingSettings = query.Length > 0;
        var results = SearchSettings(query);
        SettingsSearchResultsList.ItemsSource = results;
        SettingsSearchResultsSummary.Text = results.Count == 1
            ? $"1 result for \"{query}\""
            : $"{results.Count} results for \"{query}\"";
        SettingsSearchNoResultsText.Visibility = results.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ShowSettingsCategory(_currentSettingsCategory);
    }

    private void SettingsSearchResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SettingsSearchResult result)
            NavigateToSetting(result);
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var pick = args.ChosenSuggestion as SettingsSearchResult
            ?? SearchSettings(args.QueryText).FirstOrDefault();
        if (pick != null)
            NavigateToSetting(pick);
    }

    /// <summary>Jumps to the page holding a search result and focuses its control.</summary>
    private void NavigateToSetting(SettingsSearchResult result)
    {
        SettingsSearchBox.Text = "";
        RefreshSettingsSearchResults("");
        if (SettingsView == null || SettingsView.Visibility != Visibility.Visible)
            ShowView("settings");
        if (SettingsNavList != null)
        {
            var page = SettingsNavItems.FirstOrDefault(n =>
                string.Equals(n.Name, result.Category, StringComparison.OrdinalIgnoreCase));
            if (page != null)
                SettingsNavList.SelectedItem = page;
        }
        ShowSettingsCategory(result.Category);
        try
        {
            if (SettingsView?.FindName(result.TargetName) is UIElement el)
            {
                el.StartBringIntoView();
                _ = el.Focus(FocusState.Programmatic);
            }
        }
        catch { /* navigation already landed on the right page */ }
    }

    private void ShowSettingsCategory(string name)
    {
        _currentSettingsCategory = name;
        if (SettingsSearchResultsPanel != null)
            SettingsSearchResultsPanel.Visibility = _isSearchingSettings
                ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsAboutPanel != null)
            SettingsAboutPanel.Visibility = !_isSearchingSettings && string.Equals(name, "About", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsAudioPanel != null)
            SettingsAudioPanel.Visibility = !_isSearchingSettings && string.Equals(name, "Audio", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsAppearancePanel != null)
            SettingsAppearancePanel.Visibility = !_isSearchingSettings && string.Equals(name, "Appearance", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsDiscordPanel != null)
            SettingsDiscordPanel.Visibility = !_isSearchingSettings && string.Equals(name, "Discord", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncSettingsViewFromModel()
    {
        _syncingSettingsView = true;
        try
        {
            if (SettingsVersionLabel != null)
                SettingsVersionLabel.Text = $"Music Speed Changer {UpdateService.DisplayVersion}";
            if (SettingsOwnerEmailLabel != null)
                SettingsOwnerEmailLabel.Text = $"{UpdateService.OwnerName} — {UpdateService.OwnerEmail}";

            if (SettingsAutoCheckSwitch != null)
                SettingsAutoCheckSwitch.IsOn = _settings.AutoCheckUpdates;
            if (SettingsFeedUrlBox != null)
                SettingsFeedUrlBox.Text = _settings.UpdateFeedUrl;
            if (SettingsBetaUpdatesBox != null)
                SettingsBetaUpdatesBox.IsChecked = _settings.IncludeBetaUpdates;
            if (SettingsInsiderChannelBox != null)
                SettingsInsiderChannelBox.SelectedItem = string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase)
                    ? "Canary" : "Beta";
            UpdateSettingsPatreonUi();

            if (SettingsDefaultTempoBox != null)
                SettingsDefaultTempoBox.Value = _settings.DefaultTempoPercent;
            if (SettingsDefaultPitchBox != null)
                SettingsDefaultPitchBox.Value = _settings.DefaultPitchSemitones;
            if (SettingsApplyDefaultsBox != null)
                SettingsApplyDefaultsBox.IsChecked = _settings.ApplyDefaultsOnFileLoad;
            if (SettingsTempoStepBox != null)
                SettingsTempoStepBox.Value = _settings.TempoSliderStep;
            if (SettingsPitchStepBox != null)
                SettingsPitchStepBox.Value = _settings.PitchSliderStep;

            if (SettingsShowTempoBox != null)
                SettingsShowTempoBox.IsChecked = _settings.ShowTempoPanel;
            if (SettingsShowPitchBox != null)
                SettingsShowPitchBox.IsChecked = _settings.ShowPitchPanel;
            if (SettingsShowLoopBox != null)
                SettingsShowLoopBox.IsChecked = _settings.ShowLoopPanel;
            if (SettingsShowEqBox != null)
                SettingsShowEqBox.IsChecked = _settings.ShowEqPanel;
            if (SettingsWaveformPeaksBox != null)
                SettingsWaveformPeaksBox.Value = _settings.WaveformPeaks;
            if (SettingsClickToSeekBox != null)
                SettingsClickToSeekBox.IsChecked = _settings.ClickToSeek;
            if (SettingsRememberListBox != null)
                SettingsRememberListBox.IsChecked = _settings.RememberFileList;
            if (SettingsRememberEffectsBox != null)
                SettingsRememberEffectsBox.IsChecked = _settings.RememberEffects;

            if (SettingsLayoutTypeBox != null)
                SettingsLayoutTypeBox.SelectedItem = _settings.LayoutType switch
                {
                    "Mavericks" => "Mavericks",
                    "Calico" => "Calico",
                    "Montara" => "Montara",
                    _ => "Mojave",
                };
            if (SettingsPlayerTypeBox != null)
                SettingsPlayerTypeBox.SelectedItem = _settings.PlayerType switch
                {
                    "Compact" => "Compact",
                    "CompactInline" => "Compact Inline",
                    _ => "Comfy",
                };

            if (SettingsAutoMixEnabledBox != null)
                SettingsAutoMixEnabledBox.IsChecked = _settings.AutoMixEnabled;
            if (SettingsAutoMixSecondsBox != null)
                SettingsAutoMixSecondsBox.Value = Math.Clamp(_settings.AutoMixSeconds, 1, 12);

            if (SettingsUseAccentSwitch != null)
                SettingsUseAccentSwitch.IsOn = _settings.UseSystemAccent;
            if (SettingsCustomAccentPicker != null)
            {
                SettingsCustomAccentPicker.Color = ParseHex(_settings.CustomAccentHex, Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
                SettingsCustomAccentPicker.IsEnabled = !_settings.UseSystemAccent;
            }

            if (SettingsDiscordEnabledSwitch != null)
                SettingsDiscordEnabledSwitch.IsOn = _settings.DiscordEnabled;
            if (SettingsDiscordClientIdBox != null)
                SettingsDiscordClientIdBox.Text = _settings.DiscordClientId;
            if (SettingsDiscordTempoPitchBox != null)
                SettingsDiscordTempoPitchBox.IsChecked = _settings.DiscordShowTempoPitch;
            UpdateDiscordStatusUi();
        }
        finally
        {
            _syncingSettingsView = false;
        }
    }

    private void SaveSettingsFromView()
    {
        if (_syncingSettingsView) return;

        bool autoMixWasOn = IsAutoMixOn();

        if (SettingsAutoCheckSwitch != null)
            _settings.AutoCheckUpdates = SettingsAutoCheckSwitch.IsOn;
        if (SettingsFeedUrlBox != null)
            _settings.UpdateFeedUrl = SettingsFeedUrlBox.Text?.Trim() ?? "";
        if (SettingsBetaUpdatesBox != null)
            _settings.IncludeBetaUpdates = SettingsBetaUpdatesBox.IsChecked == true;
        if (SettingsInsiderChannelBox != null)
            _settings.InsiderChannel = string.Equals(SettingsInsiderChannelBox.SelectedItem as string, "Canary", StringComparison.OrdinalIgnoreCase)
                ? "Canary" : "Beta";

        if (SettingsDefaultTempoBox != null)
            _settings.DefaultTempoPercent = SettingsDefaultTempoBox.Value;
        if (SettingsDefaultPitchBox != null)
            _settings.DefaultPitchSemitones = SettingsDefaultPitchBox.Value;
        if (SettingsApplyDefaultsBox != null)
            _settings.ApplyDefaultsOnFileLoad = SettingsApplyDefaultsBox.IsChecked == true;
        if (SettingsTempoStepBox != null)
            _settings.TempoSliderStep = SettingsTempoStepBox.Value;
        if (SettingsPitchStepBox != null)
            _settings.PitchSliderStep = SettingsPitchStepBox.Value;

        if (SettingsShowTempoBox != null)
            _settings.ShowTempoPanel = SettingsShowTempoBox.IsChecked == true;
        if (SettingsShowPitchBox != null)
            _settings.ShowPitchPanel = SettingsShowPitchBox.IsChecked == true;
        if (SettingsShowLoopBox != null)
            _settings.ShowLoopPanel = SettingsShowLoopBox.IsChecked == true;
        if (SettingsShowEqBox != null)
            _settings.ShowEqPanel = SettingsShowEqBox.IsChecked == true;
        if (SettingsWaveformPeaksBox != null)
            _settings.WaveformPeaks = (int)SettingsWaveformPeaksBox.Value;
        if (SettingsClickToSeekBox != null)
            _settings.ClickToSeek = SettingsClickToSeekBox.IsChecked == true;
        if (SettingsRememberListBox != null)
            _settings.RememberFileList = SettingsRememberListBox.IsChecked == true;
        if (SettingsRememberEffectsBox != null)
            _settings.RememberEffects = SettingsRememberEffectsBox.IsChecked == true;

        if (SettingsLayoutTypeBox != null)
            _settings.LayoutType = SettingsLayoutTypeBox.SelectedItem as string ?? "Mojave";
        if (SettingsPlayerTypeBox != null)
            _settings.PlayerType = string.Equals(SettingsPlayerTypeBox.SelectedItem as string, "Compact Inline", StringComparison.OrdinalIgnoreCase)
                ? "CompactInline"
                : string.Equals(SettingsPlayerTypeBox.SelectedItem as string, "Compact", StringComparison.OrdinalIgnoreCase)
                    ? "Compact" : "Comfy";

        if (SettingsAutoMixEnabledBox != null)
            _settings.AutoMixEnabled = SettingsAutoMixEnabledBox.IsChecked == true;
        if (SettingsAutoMixSecondsBox != null)
            _settings.AutoMixSeconds = Math.Clamp(SettingsAutoMixSecondsBox.Value, 1, 12);

        if (SettingsUseAccentSwitch != null)
            _settings.UseSystemAccent = SettingsUseAccentSwitch.IsOn;
        if (SettingsCustomAccentPicker != null)
        {
            var c = SettingsCustomAccentPicker.Color;
            _settings.CustomAccentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        if (SettingsDiscordEnabledSwitch != null)
            _settings.DiscordEnabled = SettingsDiscordEnabledSwitch.IsOn;
        if (SettingsDiscordClientIdBox != null)
            _settings.DiscordClientId = (SettingsDiscordClientIdBox.Text ?? "").Trim();
        if (SettingsDiscordTempoPitchBox != null)
            _settings.DiscordShowTempoPitch = SettingsDiscordTempoPitchBox.IsChecked == true;

        _settings.Save();
        _discord.Configure(_settings.DiscordEnabled, _settings.DiscordClientId);
        UpdateDiscordStatusUi();
        RefreshDiscordPresence(force: true);
        ApplyLiveSettings();
        ApplyAccent();
        ApplyLayoutSettings();
        if (!IsAutoMixOn() && autoMixWasOn)
            CancelAutoMix();
        UpdateInsiderVisibility();
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    private void UpdateSettingsPatreonUi()
    {
        bool linked = _settings.BetaAccessUnlocked && !string.IsNullOrEmpty(_settings.PatreonRefreshToken);
        if (SettingsPatreonLoginButton != null)
            SettingsPatreonLoginButton.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsPatreonUnlinkButton != null)
            SettingsPatreonUnlinkButton.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsPatreonStatusLabel == null) return;
        if (linked)
        {
            string who = string.IsNullOrWhiteSpace(_settings.PatreonFullName) ? "" : $" as {_settings.PatreonFullName}";
            SettingsPatreonStatusLabel.Text = $"Linked{who} ✓ — beta downloads unlocked.";
        }
        else if (string.IsNullOrWhiteSpace(SettingsPatreonStatusLabel.Text) ||
                 SettingsPatreonStatusLabel.Text.StartsWith("Linked", StringComparison.Ordinal))
        {
            SettingsPatreonStatusLabel.Text = "Not linked — log in with Patreon to unlock betas.";
        }
    }

    private void SettingsAutoCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.AutoCheckUpdates = SettingsAutoCheckSwitch.IsOn;
        _settings.Save();
    }

    private void SettingsFeedUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.UpdateFeedUrl = SettingsFeedUrlBox.Text?.Trim() ?? "";
        _settings.Save();
    }

    private void SettingsBetaUpdatesBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.IncludeBetaUpdates = SettingsBetaUpdatesBox.IsChecked == true;
        _settings.Save();
    }

    private void SettingsInsiderChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.InsiderChannel = string.Equals(SettingsInsiderChannelBox.SelectedItem as string, "Canary", StringComparison.OrdinalIgnoreCase)
            ? "Canary" : "Beta";
        _settings.Save();
    }

    private async void SettingsPatreonButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateService.PatreonPageUrl));
        }
        catch { /* best effort */ }
    }

    private async void SettingsEmailOwnerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"mailto:{UpdateService.OwnerEmail}"));
        }
        catch { /* best effort */ }
    }

    private async void SettingsPatreonLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPatreonLoginButton == null) return;
        SettingsPatreonLoginButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => { if (SettingsPatreonStatusLabel != null) SettingsPatreonStatusLabel.Text = s; });
            var (account, error) = await PatreonAuthService.LoginAsync(progress);
            if (account == null)
            {
                if (SettingsPatreonStatusLabel != null) SettingsPatreonStatusLabel.Text = error ?? "Login didn't complete.";
                return;
            }
            _settings.BetaAccessUnlocked = true;
            _settings.PatreonRefreshToken = account.RefreshToken;
            _settings.PatreonFullName = account.FullName;
            _settings.IncludeBetaUpdates = true;
            _settings.Save();
            if (SettingsBetaUpdatesBox != null) SettingsBetaUpdatesBox.IsChecked = true;
            UpdateSettingsPatreonUi();
            UpdateInsiderVisibility();
        }
        finally { SettingsPatreonLoginButton.IsEnabled = true; }
    }

    private void SettingsPatreonUnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.BetaAccessUnlocked = false;
        _settings.PatreonRefreshToken = null;
        _settings.PatreonFullName = null;
        _settings.Save();
        if (SettingsPatreonStatusLabel != null)
            SettingsPatreonStatusLabel.Text = "Not linked — log in with Patreon to unlock betas.";
        UpdateSettingsPatreonUi();
        UpdateInsiderVisibility();
    }

    private async void SettingsCheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsChecking) return;
        _settingsChecking = true;
        _settingsPendingUpdate = null;
        if (SettingsInstallUpdateButton != null) SettingsInstallUpdateButton.Visibility = Visibility.Collapsed;
        if (SettingsUpdateStatusLabel != null) SettingsUpdateStatusLabel.Text = "Checking…";
        try
        {
            bool wantBeta = SettingsBetaUpdatesBox?.IsChecked == true;
            bool wantCanary = wantBeta && _settings.BetaAccessUnlocked &&
                string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
            var info = await UpdateService.CheckForUpdateAsync(SettingsFeedUrlBox?.Text?.Trim() ?? "", wantBeta, wantCanary);
            if (info == null)
            {
                if (SettingsUpdateStatusLabel != null)
                {
                    SettingsUpdateStatusLabel.Text = wantBeta
                        ? $"You're up to date ({UpdateService.DisplayVersion})."
                        : $"You're up to date ({UpdateService.DisplayVersion}).\nBeta builds are gated for Patreon supporters — tick “Include beta updates” to look for them.";
                }
            }
            else if (info.IsBeta && !_settings.BetaAccessUnlocked)
            {
                if (!string.IsNullOrEmpty(_settings.PatreonRefreshToken))
                {
                    if (SettingsUpdateStatusLabel != null) SettingsUpdateStatusLabel.Text = "Re-verifying Patreon membership…";
                    var account = await PatreonAuthService.RefreshAndVerifyAsync(_settings.PatreonRefreshToken);
                    if (account != null)
                    {
                        _settings.BetaAccessUnlocked = true;
                        _settings.PatreonRefreshToken = account.RefreshToken;
                        _settings.PatreonFullName = account.FullName;
                        _settings.Save();
                        UpdateSettingsPatreonUi();
                    }
                }
            }

            if (info != null && info.IsBeta && !_settings.BetaAccessUnlocked)
            {
                if (SettingsUpdateStatusLabel != null)
                {
                    SettingsUpdateStatusLabel.Text = $"Version {info.Version} is a beta for Patreon supporters.\n" +
                        "Use “Login with Patreon” above, then check again to install it.";
                }
            }
            else if (info != null)
            {
                _settingsPendingUpdate = info;
                if (SettingsUpdateStatusLabel != null)
                {
                    SettingsUpdateStatusLabel.Text = info.IsBeta
                        ? $"Beta {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}"
                        : $"Version {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}";
                }
                if (SettingsInstallUpdateButton != null) SettingsInstallUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (SettingsUpdateStatusLabel != null) SettingsUpdateStatusLabel.Text = $"Check failed: {ex.Message}";
        }
        finally { _settingsChecking = false; }
    }

    private async void SettingsInstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsPendingUpdate != null)
        {
            await DownloadAndInstallAsync(_settingsPendingUpdate);
        }
    }

    private async void SettingsSwitchToBetaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsSwitching) return;
        _settingsSwitching = true;
        if (SettingsSwitchToBetaButton != null) SettingsSwitchToBetaButton.IsEnabled = false;
        if (SettingsInstallUpdateButton != null) SettingsInstallUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            if (!_settings.BetaAccessUnlocked && !string.IsNullOrEmpty(_settings.PatreonRefreshToken))
            {
                if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = "Re-verifying Patreon membership…";
                var silent = await PatreonAuthService.RefreshAndVerifyAsync(_settings.PatreonRefreshToken);
                if (silent != null)
                {
                    _settings.BetaAccessUnlocked = true;
                    _settings.PatreonRefreshToken = silent.RefreshToken;
                    _settings.PatreonFullName = silent.FullName;
                    _settings.Save();
                    UpdateSettingsPatreonUi();
                }
                else
                {
                    _settings.BetaAccessUnlocked = false;
                    _settings.PatreonRefreshToken = null;
                    _settings.PatreonFullName = null;
                    _settings.Save();
                    UpdateSettingsPatreonUi();
                }
            }
            if (!_settings.BetaAccessUnlocked)
            {
                var progress = new Progress<string>(s => { if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = s; });
                var (account, error) = await PatreonAuthService.LoginAsync(progress);
                if (account == null)
                {
                    if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = error ?? "Login didn't complete.";
                    return;
                }
                _settings.BetaAccessUnlocked = true;
                _settings.PatreonRefreshToken = account.RefreshToken;
                _settings.PatreonFullName = account.FullName;
                _settings.Save();
                UpdateSettingsPatreonUi();
            }
            if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = "Checking for beta builds…";
            bool wantCanary = _settings.BetaAccessUnlocked &&
                string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
            var info = await UpdateService.CheckForUpdateAsync(SettingsFeedUrlBox?.Text?.Trim() ?? "", includeBeta: true, includeCanary: wantCanary);
            if (info == null || !info.IsBeta)
            {
                if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = "No beta build available right now — you're up to date.";
                return;
            }
            _settingsPendingUpdate = info;
            if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = $"Beta {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}";
            if (SettingsInstallUpdateButton != null) SettingsInstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (SettingsBetaStatusLabel != null) SettingsBetaStatusLabel.Text = $"Beta switch failed: {ex.Message}";
        }
        finally
        {
            _settingsSwitching = false;
            if (SettingsSwitchToBetaButton != null) SettingsSwitchToBetaButton.IsEnabled = true;
        }
    }

    private void SettingsDefaultTempoBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.DefaultTempoPercent = args.NewValue;
        _settings.Save();
    }

    private void SettingsDefaultPitchBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.DefaultPitchSemitones = args.NewValue;
        _settings.Save();
    }

    private void SettingsApplyDefaultsBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ApplyDefaultsOnFileLoad = SettingsApplyDefaultsBox.IsChecked == true;
        _settings.Save();
    }

    private void SettingsTempoStepBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.TempoSliderStep = args.NewValue;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsPitchStepBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.PitchSliderStep = args.NewValue;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsRememberEffectsBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.RememberEffects = SettingsRememberEffectsBox.IsChecked == true;
        _settings.Save();
    }

    private void SettingsAutoMixEnabledBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        bool autoMixWasOn = IsAutoMixOn();
        _settings.AutoMixEnabled = SettingsAutoMixEnabledBox.IsChecked == true;
        _settings.Save();
        if (!IsAutoMixOn() && autoMixWasOn)
            CancelAutoMix();
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    private void SettingsAutoMixSecondsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.AutoMixSeconds = Math.Clamp(args.NewValue, 1, 12);
        _settings.Save();
        UpdateAutoMixUi();
    }

    private void SettingsShowTempoBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ShowTempoPanel = SettingsShowTempoBox.IsChecked == true;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsShowPitchBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ShowPitchPanel = SettingsShowPitchBox.IsChecked == true;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsShowLoopBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ShowLoopPanel = SettingsShowLoopBox.IsChecked == true;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsShowEqBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ShowEqPanel = SettingsShowEqBox.IsChecked == true;
        _settings.Save();
        ApplyLiveSettings();
    }

    private void SettingsWaveformPeaksBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettingsView || double.IsNaN(args.NewValue)) return;
        _settings.WaveformPeaks = (int)args.NewValue;
        _settings.Save();
    }

    private void SettingsClickToSeekBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.ClickToSeek = SettingsClickToSeekBox.IsChecked == true;
        _settings.Save();
    }

    private void SettingsRememberListBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.RememberFileList = SettingsRememberListBox.IsChecked == true;
        _settings.Save();
    }

    private void SettingsLayoutTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.LayoutType = SettingsLayoutTypeBox.SelectedItem as string ?? "Mojave";
        _settings.Save();
        ApplyLayoutSettings();
    }

    private void SettingsPlayerTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.PlayerType = string.Equals(SettingsPlayerTypeBox.SelectedItem as string, "Compact Inline", StringComparison.OrdinalIgnoreCase)
            ? "CompactInline"
            : string.Equals(SettingsPlayerTypeBox.SelectedItem as string, "Compact", StringComparison.OrdinalIgnoreCase)
                ? "Compact" : "Comfy";
        _settings.Save();
        ApplyLayoutSettings();
    }

    private void SettingsUseAccentSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.UseSystemAccent = SettingsUseAccentSwitch.IsOn;
        if (SettingsCustomAccentPicker != null)
            SettingsCustomAccentPicker.IsEnabled = !SettingsUseAccentSwitch.IsOn;
        _settings.Save();
        ApplyAccent();
    }

    private void SettingsCustomAccentPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingSettingsView) return;
        var c = args.NewColor;
        _settings.CustomAccentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _settings.Save();
        if (!_settings.UseSystemAccent)
            ApplyAccent();
    }

    // ---------- Automatic updates ----------

    private async void CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (!_settings.AutoCheckUpdates) return;
            if (_betaGateActive) return; // gated: user verifies (or exits) first

            if (_settings.BetaAccessUnlocked)
            {
                bool isCanaryChannel = string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
                var (beta, canary) = await UpdateService.GetInsiderUpdatesAsync(_settings.UpdateFeedUrl);

                if (isCanaryChannel)
                {
                    // Canary channel: prefer the weekly build, fall back to Beta/stable if not running Canary.
                    if (canary != null && UpdateService.IsNewerThanCurrent(canary))
                    {
                        await PromptUpdateAsync(canary);
                        return;
                    }
                    if (UpdateService.CurrentChannel != "Canary" && beta != null && UpdateService.IsNewerThanCurrent(beta))
                    {
                        await PromptUpdateAsync(beta);
                        return;
                    }
                }
                else
                {
                    // Beta channel: prefer the beta build if newer.
                    if (beta != null && UpdateService.IsNewerThanCurrent(beta))
                    {
                        await PromptUpdateAsync(beta);
                        return;
                    }
                }
            }

            var info = await UpdateService.CheckForUpdateAsync(
                _settings.UpdateFeedUrl,
                includeBeta: _settings.IncludeBetaUpdates && _settings.BetaAccessUnlocked,
                includeCanary: _settings.BetaAccessUnlocked && string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase));

            if (info != null && UpdateService.IsNewerThanCurrent(info))
            {
                await PromptUpdateAsync(info);
            }
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
        string displayTag = !string.IsNullOrEmpty(info.Tag) ? info.Tag : info.Version.ToString();
        var dlg = new ContentDialog
        {
            Title = info.IsCanary ? $"Canary update available — {displayTag}"
                : info.IsBeta ? $"Beta update available — {displayTag}"
                : $"Update available — {displayTag}",
            Content = string.IsNullOrWhiteSpace(info.Notes)
                ? $"Version {displayTag} is ready to install."
                : $"Version {displayTag} is ready to install.\n\n{UpdateService.CleanNotes(info.Notes)}",
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
                : $"Beta {info.Version} is gated for Patreon supporters.\n\n{UpdateService.CleanNotes(info.Notes)}\n\nLog in to verify your membership, or wait for the stable release.",
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
        UpdateInsiderVisibility();
    }

    private void ClearPatreonLink()
    {
        _settings.BetaAccessUnlocked = false;
        _settings.PatreonRefreshToken = null;
        _settings.PatreonFullName = null;
        _settings.Save();
        UpdateInsiderVisibility();
    }

    // ---------- Beta gate (startup Patreon requirement for beta builds) ----------

    private static bool IsBetaBuild() =>
        UpdateService.DisplayVersion.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsCanaryBuild() =>
        UpdateService.DisplayVersion.IndexOf("canary", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsInsiderBuild() => IsBetaBuild() || IsCanaryBuild();

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

    // ---------- MSC Insider Hub (Beta + weekly Canary, Patreon supporters) ----------

    /// <summary>
    /// The Hub is a beta-build perk: visible only on Beta/Canary builds for
    /// Patreon-linked supporters. Stable builds and unlinked users never see it.
    /// </summary>
    private void UpdateInsiderVisibility()
    {
        if (NavInsiderButton == null) return;
        bool visible = IsInsiderBuild() && _settings.BetaAccessUnlocked;
        NavInsiderButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && InsiderView != null && InsiderView.Visibility == Visibility.Visible)
            ShowView("player");
        SyncInsiderHeader();
    }

    private void SyncInsiderHeader()
    {
        if (InsiderPatreonLabel == null) return;
        string who = string.IsNullOrWhiteSpace(_settings.PatreonFullName) ? "" : $" as {_settings.PatreonFullName}";
        InsiderPatreonLabel.Text = _settings.BetaAccessUnlocked
            ? $"Linked{who} ✓ — insider builds unlocked."
            : "Not linked — log in with Patreon to unlock insider builds.";
        if (InsiderCurrentLabel != null)
            InsiderCurrentLabel.Text =
                $"Running {UpdateService.DisplayVersion} ({UpdateService.CurrentChannel} channel, {_settings.InsiderChannel} preferred).";
        if (InsiderBetaRadio != null)
            InsiderBetaRadio.IsChecked = !string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
        if (InsiderCanaryRadio != null)
            InsiderCanaryRadio.IsChecked = string.Equals(_settings.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
    }

    private void InsiderChannel_Checked(object sender, RoutedEventArgs e)
    {
        if (InsiderCanaryRadio == null) return;
        _settings.InsiderChannel = InsiderCanaryRadio.IsChecked == true ? "Canary" : "Beta";
        _settings.Save();
        SyncInsiderHeader();
    }

    private void InsiderCheckButton_Click(object sender, RoutedEventArgs e) => RefreshInsiderHubAsync();

    private void InsiderPatreonButton_Click(object sender, RoutedEventArgs e)
    {
        try { _ = Launcher.LaunchUriAsync(new Uri(UpdateService.PatreonPageUrl)); }
        catch { /* ignore */ }
    }

    private async void RefreshInsiderHubAsync()
    {
        if (InsiderStatusLabel == null || _refreshingInsider) return;
        if (!IsInsiderBuild() || !_settings.BetaAccessUnlocked) return;
        _refreshingInsider = true;
        if (InsiderCheckButton != null) InsiderCheckButton.IsEnabled = false;
        try
        {
            SyncInsiderHeader();
            InsiderStatusLabel.Text = "Checking for insider builds…";
            var (beta, canary) = await UpdateService.GetInsiderUpdatesAsync(_settings.UpdateFeedUrl);
            _pendingInsiderBeta = beta;
            _pendingInsiderCanary = canary;
            FillInsiderCard(beta, InsiderBetaStatus, InsiderBetaNotes, InsiderBetaInstallButton, "Beta");
            FillInsiderCard(canary, InsiderCanaryStatus, InsiderCanaryNotes, InsiderCanaryInstallButton, "Canary");
            InsiderStatusLabel.Text = beta == null && canary == null
                ? $"No insider builds found ({UpdateService.DisplayVersion} is the newest)."
                : "Insider builds refreshed.";
        }
        catch (Exception ex)
        {
            InsiderStatusLabel.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            _refreshingInsider = false;
            if (InsiderCheckButton != null) InsiderCheckButton.IsEnabled = true;
        }
    }

    private void FillInsiderCard(UpdateInfo? info, TextBlock status, TextBlock notes,
        Button installButton, string channel)
    {
        if (info == null)
        {
            status.Text = $"No {channel} build published yet.";
            notes.Text = "";
            installButton.Visibility = Visibility.Collapsed;
            return;
        }
        string date = info.PublishedAt == default ? "" : $" — published {info.PublishedAt:yyyy-MM-dd}";
        string tag = string.IsNullOrEmpty(info.Tag) ? info.Version.ToString() : info.Tag;
        bool isNewer = UpdateService.IsNewerThanCurrent(info);
        if (!isNewer && (string.Equals(UpdateService.CurrentChannel, channel, StringComparison.OrdinalIgnoreCase) ||
                         UpdateService.CurrentReleaseVersion.CompareTo(UpdateService.ParseReleaseVersion(info)) >= 0))
        {
            status.Text = $"{channel} {tag}{date} — up to date";
            installButton.Content = $"Reinstall {channel}";
        }
        else
        {
            status.Text = $"{channel} {tag}{date}";
            installButton.Content = $"Download & install {channel}";
        }
        notes.Text = UpdateService.CleanNotes(info.Notes);
        installButton.Visibility = Visibility.Visible;
    }

    private async void InsiderBetaInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingInsiderBeta != null)
            await DownloadAndInstallAsync(_pendingInsiderBeta);
    }

    private async void InsiderCanaryInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingInsiderCanary != null)
            await DownloadAndInstallAsync(_pendingInsiderCanary);
    }

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

    // ---------- Layout (Cider-style Layout Type + Player Type) ----------

    /// <summary>Applies the Layout Type (queue dock) + Player Type (transport density).</summary>
    private void ApplyLayoutSettings()
    {
        ApplyLayoutType();
        ApplyPlayerType();
    }

    /// <summary>
    /// Mojave = queue left 230, Mavericks = queue right 230,
    /// Calico = queue left 340, Montara = queue right 340.
    /// </summary>
    private void ApplyLayoutType()
    {
        if (FilesPanel == null || PlayerSideColumn == null || PlayerContentColumn == null ||
            PlayerContentGrid == null) return;
        bool right = string.Equals(_settings.LayoutType, "Mavericks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.LayoutType, "Montara", StringComparison.OrdinalIgnoreCase);
        bool wide = string.Equals(_settings.LayoutType, "Calico", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_settings.LayoutType, "Montara", StringComparison.OrdinalIgnoreCase);
        double sideWidth = wide ? 340 : 230;

        if (right)
        {
            PlayerSideColumn.Width = new GridLength(1, GridUnitType.Star);
            PlayerContentColumn.Width = new GridLength(sideWidth);
        }
        else
        {
            PlayerSideColumn.Width = new GridLength(sideWidth);
            PlayerContentColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        Grid.SetColumn(FilesPanel, right ? 1 : 0);
        Grid.SetColumn(PlayerContentGrid, right ? 0 : 1);
        // Keep the gutter on the inner edge of the queue panel.
        FilesPanel.Margin = new Thickness(right ? 6 : 0, 0, right ? 0 : 6, 6);
    }

    private bool _seekInline;

    /// <summary>
    /// Comfy = full two-row transport; Compact = slim (no artwork, smaller
    /// buttons); CompactInline = one row with the seek slider merged in.
    /// </summary>
    private void ApplyPlayerType()
    {
        if (SeekRow == null || TransportButtonsPanel == null || TransportInfoPanel == null ||
            TransportArtwork == null || TransportVolumePanel == null || SeekSlider == null) return;
        string mode = _settings.PlayerType; // normalized: Comfy, Compact, CompactInline

        // Restore the seek slider to its home row first (idempotent).
        if (_seekInline)
        {
            TransportButtonsPanel.Children.Remove(SeekSlider);
            SeekRow.Children.Add(SeekSlider);
            Grid.SetColumn(SeekSlider, 1);
            SeekSlider.Width = double.NaN;
            _seekInline = false;
        }

        bool slim = !string.Equals(mode, "Comfy", StringComparison.OrdinalIgnoreCase);
        bool inlineSeek = string.Equals(mode, "CompactInline", StringComparison.OrdinalIgnoreCase);

        TransportArtwork.Visibility = slim ? Visibility.Collapsed : Visibility.Visible;
        TransportInfoPanel.Visibility = inlineSeek ? Visibility.Collapsed : Visibility.Visible;
        SeekRow.Visibility = inlineSeek ? Visibility.Collapsed : Visibility.Visible;

        double small = slim ? 36 : 40;
        SetRoundButton(ShuffleButton, small);
        SetRoundButton(PrevButton, small);
        SetRoundButton(StopButton, small);
        SetRoundButton(NextButton, small);
        SetRoundButton(RepeatButton, small);
        if (AutoMixButton != null) SetRoundButton(AutoMixButton, small);
        PlayButton.Width = slim ? 44 : 52;
        PlayButton.Height = slim ? 44 : 52;
        PlayButton.CornerRadius = new CornerRadius(slim ? 22 : 26);
        VolumeSlider.Width = slim ? 80 : 110;

        if (inlineSeek)
        {
            SeekRow.Children.Remove(SeekSlider);
            SeekSlider.Width = 170;
            // Park it after the main transport buttons (before the AutoMix cluster).
            int at = Math.Min(6, TransportButtonsPanel.Children.Count);
            TransportButtonsPanel.Children.Insert(at, SeekSlider);
            _seekInline = true;
        }
    }

    private static void SetRoundButton(Button button, double size)
    {
        if (button == null) return;
        button.Width = size;
        button.Height = size;
        button.CornerRadius = new CornerRadius(size / 2);
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
        CancelAutoMix();
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
        CancelAutoMix();
        if (FileListView.SelectedItem is TrackItem item)
            _tracks.Remove(item);
        UpdatePlaylistUi();
    }

    private void ClearFilesButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoMix();
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

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoMix();
        await MoveSelection(-1, wrap: _repeatMode == RepeatAll);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAutoMix();
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
        CancelAutoMix();
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
        if (AutoMixButton != null)
            AutoMixButton.IsEnabled = _tracks.Count > 1;
        _smtc.SetNextPreviousEnabled(CanGoNext(), CanGoPrevious());
        RefreshTaskbarButtons();
        UpdateNextUp();
    }

    // ---------- Shuffle / repeat ----------

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        _settings.ShuffleEnabled = _shuffle;
        _settings.Save();
        UpdateShuffleRepeatUi();
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = (_repeatMode + 1) % 3;
        _settings.RepeatMode = _repeatMode;
        _settings.Save();
        if (_repeatMode == RepeatOne)
            CancelAutoMix();
        UpdateShuffleRepeatUi();
        UpdateAutoMixUi();
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

    private void OnEnginePlaybackEnded(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(async () => await OnTrackEndedAsync(sender));

    private async Task OnTrackEndedAsync(object? sender = null)
    {
        // Discard stale events from old/disposed engines or recent transitions
        if (sender is AudioEngine eng && eng != _engine && eng != _mixNext) return;
        if ((DateTime.UtcNow - _lastMixFinishedTime).TotalSeconds < 0.8) return;

        // AutoMix crossfade in progress: the outgoing engine ended — finish the swap.
        if (_mixNext != null)
        {
            await FinishAutoMixAsync();
            return;
        }
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

    // ---------- AutoMix (Spotify-style customizable transitions) ----------

    /// <summary>AutoMix is available to customize across all tracks.</summary>
    private bool IsAutoMixAvailable() => true;

    private bool IsAutoMixOn() =>
        IsAutoMixAvailable() && _settings.AutoMixEnabled;

    private double AutoMixRequestedSeconds() =>
        Math.Clamp(_settings.AutoMixSeconds, 1, 12);

    /// <summary>Whether a crossfade may start right now (eligible state).</summary>
    private bool CanAutoMixNow()
    {
        if (!IsAutoMixOn()) return false;
        if (_mixNext != null || _mixStarting || _isFinishingMix) return false;
        if (!_engine.IsLoaded || !_engine.IsPlaying || _engine.Reverse) return false;
        if (_engine.LoopEnabled) return false;
        if (_repeatMode == RepeatOne) return false;
        if (_tracks.Count < 2) return false;
        int current;
        try { current = FileListView.SelectedIndex; } catch { return false; }
        int next = PickNextIndex(current, wrap: _repeatMode == RepeatAll);
        if (next < 0 || next >= _tracks.Count) return false;
        if (!File.Exists(_tracks[next].Path)) return false;
        double remaining = _engine.EffectiveRemainingSeconds;
        if (remaining <= 0.15 || remaining > AutoMixRequestedSeconds() + 0.1) return false;
        // Outgoing track must be longer than a blip, incoming must exist.
        if (_engine.SourceDuration.TotalSeconds < 3) return false;
        return true;
    }

    private void MaybeStartAutoMix()
    {
        if (!CanAutoMixNow()) return;
        int current;
        try { current = FileListView.SelectedIndex; } catch { return; }
        int next = PickNextIndex(current, wrap: _repeatMode == RepeatAll);
        if (next < 0) return;
        _ = StartAutoMixAsync(next);
    }

    /// <summary>
    /// Warms the incoming track's head beat grid ahead of the crossfade so
    /// grid analysis doesn't eat into the fade: once per track pair, while
    /// the trigger is still up to 20 s away.
    /// </summary>
    private void MaybePrewarmAutoMix()
    {
        if (!IsAutoMixOn()) return;
        if (_mixNext != null || _mixStarting || _isFinishingMix) return;
        if (!_engine.IsLoaded || !_engine.IsPlaying || _engine.Reverse) return;
        if (_engine.LoopEnabled) return;
        if (_repeatMode == RepeatOne) return;
        if (_tracks.Count < 2) return;
        double requested = AutoMixRequestedSeconds();
        double remaining = _engine.EffectiveRemainingSeconds;
        if (remaining <= requested || remaining > requested + 20) return;
        int current;
        try { current = FileListView.SelectedIndex; } catch { return; }
        int next = PickNextIndex(current, wrap: _repeatMode == RepeatAll);
        if (next < 0 || next >= _tracks.Count) return;
        string key = (_engine.FilePath ?? "") + "->" + _tracks[next].Path;
        if (key == _mixPrewarmedKey) return;
        _mixPrewarmedKey = key;
        string warmNext = _tracks[next].Path;
        string? warmOut = _engine.FilePath;
        _ = Task.Run(() =>
        {
            if (_settings.AutoMixBeatSync)
            {
                if (!string.IsNullOrEmpty(warmOut))
                    BeatGridCache.Precompute(warmOut, BeatGridWindow.Tail);
                BeatGridCache.Precompute(warmNext, BeatGridWindow.Head);
            }
            else if (_settings.AutoMixSkipSilence)
            {
                BeatGridCache.Precompute(warmNext, BeatGridWindow.Head);
            }
        });
    }

    /// <summary>
    /// Starts the Spotify-style crossfade: preloads the next track on a second
    /// engine and transitions according to the selected style (Bass Swap, Equal Power
    /// Blend, Rise, Beat Drop, or Linear). When Beat-Sync is enabled and both tracks
    /// have a reliable beat grid, tempo and downbeats are matched during the blend.
    /// </summary>
    private async Task StartAutoMixAsync(int nextIndex)
    {
        if (_mixNext != null || _mixStarting || _isFinishingMix) return;
        if (nextIndex < 0 || nextIndex >= _tracks.Count) return;
        var item = _tracks[nextIndex];
        if (!File.Exists(item.Path)) return;

        int sessionId = ++_mixSessionId;
        _mixStarting = true;
        try
        {
            double userTempo = Math.Clamp(TempoSlider.Value / 100.0, 0.25, 3.0);
            double pitch = Math.Clamp(PitchSlider.Value, -12.0, 12.0);
            float volume = (float)Math.Clamp(VolumeSlider.Value / 100.0, 0, 1);
            var fx = _engine.SnapshotEffects();
            string? outPath = _engine.FilePath;
            string inPath = item.Path;
            var incoming = new AudioEngine();

            var loadTask = Task.Run(() =>
            {
                try
                {
                    incoming.ReplaceEffects(fx);
                    incoming.Load(inPath);
                    incoming.Tempo = userTempo;
                    incoming.PitchSemitones = pitch;
                    incoming.Volume = volume;
                    incoming.MixGain = 0f;
                    incoming.MixHighPassCutoff = 20f;
                    return true;
                }
                catch { return false; }
            });

            Task<BeatGrid?> outGridTask = _settings.AutoMixBeatSync
                ? Task.Run(() => (BeatGrid?)BeatGridCache.GetOrAnalyze(outPath, BeatGridWindow.Tail))
                : Task.FromResult<BeatGrid?>(null);

            Task<BeatGrid?> inGridTask = (_settings.AutoMixBeatSync || _settings.AutoMixSkipSilence)
                ? Task.Run(() => (BeatGrid?)BeatGridCache.GetOrAnalyze(inPath, BeatGridWindow.Head))
                : Task.FromResult<BeatGrid?>(null);

            bool loaded = await loadTask;
            if (!loaded || sessionId != _mixSessionId || !_engine.IsPlaying || !_engine.IsLoaded)
            {
                try { incoming.Dispose(); } catch { }
                return;
            }

            BeatGrid? outGrid = null;
            BeatGrid? inGrid = null;
            try
            {
                outGrid = await outGridTask;
                inGrid = await inGridTask;
            }
            catch { }

            if (sessionId != _mixSessionId || !_engine.IsPlaying || !_engine.IsLoaded)
            {
                try { incoming.Dispose(); } catch { }
                return;
            }

            double remaining = _engine.EffectiveRemainingSeconds;
            double requested = AutoMixRequestedSeconds();
            BeatSyncPlan? plan = null;

            if (_settings.AutoMixBeatSync && outGrid != null && inGrid != null)
            {
                try
                {
                    plan = BeatSyncPlan.TryPlan(
                        outGrid, inGrid,
                        _engine.SourcePosition.TotalSeconds, userTempo,
                        requested, remaining,
                        incoming.SourceDuration.TotalSeconds);
                }
                catch { }
            }

            if (plan != null)
            {
                incoming.Tempo = Math.Clamp(userTempo * plan.TempoRatio, 0.25, 3.0);
                try { incoming.SourcePosition = TimeSpan.FromSeconds(plan.IncomingStartSeconds); }
                catch { }
                _mixDuration = Math.Clamp(plan.DurationSeconds, 0.8, Math.Max(0.8, remaining));
                _mixTempoRatio = plan.TempoRatio;
                _mixBeatInfo = plan.Info;
            }
            else
            {
                _mixDuration = Math.Clamp(Math.Min(requested, Math.Max(0.8, remaining)), 0.8, 12.0);
                _mixTempoRatio = 1.0;
                _mixBeatInfo = null;

                if (_settings.AutoMixSkipSilence && inGrid != null && inGrid.FirstSoundSeconds > 0.05)
                {
                    double startSec = Math.Min(inGrid.FirstSoundSeconds, Math.Max(0, incoming.SourceDuration.TotalSeconds - 1.0));
                    try { incoming.SourcePosition = TimeSpan.FromSeconds(startSec); } catch { }
                }
            }

            ApplyMixGains(0.0, _settings.AutoMixStyle, _engine, incoming);

            _mixNextIndex = nextIndex;
            incoming.PlaybackEnded += OnEnginePlaybackEnded;
            incoming.Play();
            _mixStopwatch.Restart();
            _mixNext = incoming;

            string styleName = FormatStyleName(_settings.AutoMixStyle);
            StatusLabel.Text = _mixBeatInfo != null
                ? $"AutoMix ({styleName}) → {item.Name} · {_mixBeatInfo}"
                : $"AutoMix ({styleName}) → {item.Name}";
            UpdateNextUp();
        }
        catch { }
        finally { _mixStarting = false; }
    }

    /// <summary>Ramps gain and EQ filters along the selected transition style curve.</summary>
    private void UpdateAutoMixFade()
    {
        var incoming = _mixNext;
        if (incoming == null || _isFinishingMix) return;

        double elapsed = _mixStopwatch.Elapsed.TotalSeconds;
        double dur = Math.Max(0.4, _mixDuration);
        double t = Math.Clamp(elapsed / dur, 0.0, 1.0);

        ApplyMixGains(t, _settings.AutoMixStyle, _engine, incoming);

        // Smooth tempo restoration during the second half of the transition:
        if (Math.Abs(_mixTempoRatio - 1.0) > 0.001 && t > 0.5)
        {
            double blend = (t - 0.5) / 0.5; // 0 to 1
            double userTempo = TempoSlider.Value / 100.0;
            double currentRatio = _mixTempoRatio * (1.0 - blend) + 1.0 * blend;
            try { incoming.Tempo = Math.Clamp(userTempo * currentRatio, 0.25, 3.0); } catch { }
        }

        try
        {
            incoming.Update();
        }
        catch { }

        if (t >= 1.0 || _engine.EffectiveRemainingSeconds <= 0.04)
        {
            _ = FinishAutoMixAsync();
        }
    }

    private static void ApplyMixGains(double t, string? style, AudioEngine outgoing, AudioEngine incoming)
    {
        style = AppSettings.NormalizeAutoMixStyle(style);
        try
        {
            switch (style)
            {
                case "Blend":
                    outgoing.MixGain = (float)Math.Cos(t * Math.PI / 2.0);
                    incoming.MixGain = (float)Math.Sin(t * Math.PI / 2.0);
                    outgoing.MixHighPassCutoff = 20f;
                    incoming.MixHighPassCutoff = 20f;
                    break;

                case "BassSwap":
                    outgoing.MixGain = (float)Math.Cos(t * Math.PI / 2.0);
                    incoming.MixGain = (float)Math.Sin(t * Math.PI / 2.0);
                    if (t < 0.5)
                    {
                        outgoing.MixHighPassCutoff = 20f;
                        incoming.MixHighPassCutoff = 260f; // Bass cut on incoming
                    }
                    else
                    {
                        outgoing.MixHighPassCutoff = 260f; // Bass cut on outgoing
                        incoming.MixHighPassCutoff = 20f;  // Bass full on incoming
                    }
                    break;

                case "Rise":
                    outgoing.MixGain = (float)Math.Cos(t * Math.PI / 2.0);
                    incoming.MixGain = (float)Math.Sin(t * Math.PI / 2.0);
                    float sweep = (float)(20.0 * Math.Pow(1800.0 / 20.0, t));
                    outgoing.MixHighPassCutoff = Math.Clamp(sweep, 20f, 2000f);
                    incoming.MixHighPassCutoff = 20f;
                    break;

                case "BeatDrop":
                    if (t < 0.88)
                    {
                        outgoing.MixGain = 1.0f;
                        incoming.MixGain = 0.0f;
                    }
                    else
                    {
                        double subT = (t - 0.88) / 0.12;
                        outgoing.MixGain = (float)(1.0 - subT);
                        incoming.MixGain = (float)subT;
                    }
                    outgoing.MixHighPassCutoff = 20f;
                    incoming.MixHighPassCutoff = 20f;
                    break;

                case "Linear":
                default:
                    outgoing.MixGain = (float)(1.0 - t);
                    incoming.MixGain = (float)t;
                    outgoing.MixHighPassCutoff = 20f;
                    incoming.MixHighPassCutoff = 20f;
                    break;
            }
        }
        catch { }
    }

    /// <summary>
    /// Promotes the incoming engine to primary and disposes the outgoing one,
    /// then moves the queue selection + waveform + SMTC to the new track.
    /// </summary>
    private async Task FinishAutoMixAsync()
    {
        if (_isFinishingMix) return;
        _isFinishingMix = true;

        var incoming = _mixNext;
        if (incoming == null)
        {
            _isFinishingMix = false;
            return;
        }

        _mixNext = null;
        _mixStopwatch.Stop();
        _mixTempoRatio = 1.0;
        _mixBeatInfo = null;
        int finishedIndex = _mixNextIndex;
        _mixNextIndex = -1;

        AudioEngine old = _engine;
        try { old.PlaybackEnded -= OnEnginePlaybackEnded; } catch { }
        _engine = incoming;
        try { _engine.PlaybackEnded -= OnEnginePlaybackEnded; } catch { }
        _engine.PlaybackEnded += OnEnginePlaybackEnded;

        _engine.MixGain = 1f;
        _engine.MixHighPassCutoff = 20f;
        // Restore user's selected tempo slider on the engine
        try { _engine.Tempo = TempoSlider.Value / 100.0; } catch { }

        try { old.Stop(); } catch { }
        try { old.Dispose(); } catch { }

        _lastMixFinishedTime = DateTime.UtcNow;

        // Move the queue selection without re-loading (engine already playing).
        _playlistSync = true;
        try
        {
            if (finishedIndex >= 0 && finishedIndex < _tracks.Count)
                FileListView.SelectedIndex = finishedIndex;
        }
        finally { _playlistSync = false; }

        var item = (finishedIndex >= 0 && finishedIndex < _tracks.Count) ? _tracks[finishedIndex] : null;
        _settings.LastFilePath = item?.Path ?? _engine.FilePath;
        FileLabel.Text = _engine.FileName ?? item?.Name ?? "Unknown";
        StatusLabel.Text = "";
        ClearLoopUi();
        UpdateTransportState();
        RefreshPosition();
        UpdateEffectiveLabel();
        UpdateReverseUi();
        UpdatePlaylistUi();
        UpdateNextUp();
        UpdateAutoMixUi();
        await RefreshSmtcForTrackAsync();
        RefreshSmtcPlayback();

        // Warm the beat-grid cache for the newly promoted track.
        string? promotedPath = _engine.FilePath;
        if (!string.IsNullOrEmpty(promotedPath))
        {
            string warmPromoted = promotedPath;
            _ = Task.Run(() => BeatGridCache.Precompute(warmPromoted, BeatGridWindow.Tail));
        }

        // Waveform for the new track (best-effort, ignore if user moved on).
        try
        {
            string? path = _engine.FilePath;
            Waveform.Data = null;
            if (!string.IsNullOrEmpty(path))
            {
                string copy = path;
                var data = await Task.Run(() => WaveformData.FromFile(copy, _settings.WaveformPeaks));
                if (_engine.FilePath == copy)
                    Waveform.Data = data;
            }
        }
        catch { }

        _isFinishingMix = false;
    }

    /// <summary>Aborts an in-progress crossfade (user seek/pause/nav/loop/reverse).</summary>
    private void CancelAutoMix()
    {
        _mixSessionId++;
        _mixStarting = false;
        _mixStopwatch.Stop();
        var incoming = _mixNext;
        _mixNext = null;
        _mixNextIndex = -1;
        _mixTempoRatio = 1.0;
        _mixBeatInfo = null;
        if (incoming != null)
        {
            try { incoming.PlaybackEnded -= OnEnginePlaybackEnded; } catch { }
            try { incoming.Stop(); } catch { }
            try { incoming.Dispose(); } catch { }
        }
        try
        {
            _engine.MixGain = 1f;
            _engine.MixHighPassCutoff = 20f;
        }
        catch { }
    }

    private void AutoMixButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAutoMixAvailable()) return;
        _settings.AutoMixEnabled = !_settings.AutoMixEnabled;
        _settings.Save();
        if (!_settings.AutoMixEnabled)
            CancelAutoMix();
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    private void AutoMixButton_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
        ShowAutoMixDialog();

    private void AutoMixSettingsButton_Click(object sender, RoutedEventArgs e) =>
        ShowAutoMixDialog();

    /// <summary>Opens the Spotify-style AutoMix mixer popup.</summary>
    private async void ShowAutoMixDialog()
    {
        if (!IsAutoMixAvailable()) return;
        var dlg = new AutoMixDialog(this, _settings) { XamlRoot = Content.XamlRoot };
        await dlg.ShowAsync();
    }

    /// <summary>Current + next queue tracks for the mixer popup (null when no pair).</summary>
    internal bool TryGetAutoMixPair(out TrackItem? current, out TrackItem? next)
    {
        current = null;
        next = null;
        try
        {
            if (_tracks.Count < 2) return false;
            int cur = FileListView.SelectedIndex;
            if (cur < 0 || cur >= _tracks.Count) return false;
            int nxt = PickNextIndex(cur, wrap: _repeatMode == RepeatAll);
            if (nxt < 0 || nxt >= _tracks.Count) return false;
            current = _tracks[cur];
            next = _tracks[nxt];
            return true;
        }
        catch { return false; }
    }

    /// <summary>Starts the transition immediately (mixer preview button).</summary>
    internal async Task<(bool Started, string Message)> PreviewAutoMixAsync()
    {
        if (!IsAutoMixAvailable()) return (false, "AutoMix isn't available on this build.");
        if (!IsAutoMixOn()) return (false, "Turn AutoMix on first.");
        if (_mixNext != null || _mixStarting) return (false, "A transition is already running.");
        if (!_engine.IsLoaded || !_engine.IsPlaying) return (false, "Play a track first.");
        if (_engine.LoopEnabled || _engine.Reverse || _repeatMode == RepeatOne)
            return (false, "Turn off loop / reverse / repeat-one first.");
        int nextIndex = -1;
        try
        {
            int cur = FileListView.SelectedIndex;
            if (cur < 0 || cur >= _tracks.Count) return (false, "No next track in the queue.");
            nextIndex = PickNextIndex(cur, wrap: _repeatMode == RepeatAll);
        }
        catch { return (false, "No next track in the queue."); }
        if (nextIndex < 0 || nextIndex >= _tracks.Count) return (false, "No next track in the queue.");
        await StartAutoMixAsync(nextIndex);
        return (true, "Mixing…");
    }

    /// <summary>Refreshes transport labels after the mixer edits settings.</summary>
    internal void RefreshAutoMixUi()
    {
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    /// <summary>Wall-clock seconds for a bar-count fade on the current track (null when BPM unknown).</summary>
    internal double? BarsToSeconds(int bars)
    {
        if (bars < 1 || bars > 16) return null;
        try
        {
            string? path = _engine.FilePath;
            if (string.IsNullOrEmpty(path) || !_engine.IsLoaded) return null;
            double tempo = TempoSlider.Value / 100.0;
            if (tempo <= 0) return null;
            var grid = BeatGridCache.GetOrAnalyze(path, BeatGridWindow.Tail);
            if (grid == null || !grid.IsReliable) return null;
            double barWall = grid.BeatsPerBar * grid.BeatIntervalSeconds / tempo;
            if (barWall <= 0) return null;
            return Math.Clamp(bars * barWall, 1, 12);
        }
        catch { return null; }
    }

    /// <summary>Nearest offered bar count (1/2/4/8) for a seconds value (null when BPM unknown).</summary>
    internal int? SecondsToBars(double seconds)
    {
        try
        {
            string? path = _engine.FilePath;
            if (string.IsNullOrEmpty(path) || !_engine.IsLoaded) return null;
            double tempo = TempoSlider.Value / 100.0;
            if (tempo <= 0) return null;
            var grid = BeatGridCache.GetOrAnalyze(path, BeatGridWindow.Tail);
            if (grid == null || !grid.IsReliable) return null;
            double barWall = grid.BeatsPerBar * grid.BeatIntervalSeconds / tempo;
            if (barWall <= 0) return null;
            double exact = seconds / barWall;
            int best = 1;
            double bestErr = double.MaxValue;
            foreach (int b in new[] { 1, 2, 4, 8 })
            {
                double err = Math.Abs(exact - b);
                if (err < bestErr) { bestErr = err; best = b; }
            }
            return best;
        }
        catch { return null; }
    }

    private void AutoMixSeconds_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!IsAutoMixAvailable()) return;
        _settings.AutoMixSeconds = Math.Clamp(e.NewValue, 1, 12);
        _settings.Save();
        UpdateAutoMixUi();
        UpdateNextUp();
    }

    private static string FormatStyleName(string? style) =>
        AppSettings.NormalizeAutoMixStyle(style) switch
        {
            "Blend" => "Equal Power Blend",
            "Rise" => "Rise / High-Pass",
            "BeatDrop" => "Beat Drop",
            "Linear" => "Linear",
            _ => "Bass Swap",
        };

    private void UpdateAutoMixUi()
    {
        bool available = IsAutoMixAvailable();
        string styleName = FormatStyleName(_settings.AutoMixStyle);
        if (AutoMixButton != null)
        {
            AutoMixButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            AutoMixButton.Background = IsAutoMixOn() ? _reverseActiveBackground : _autoMixIdleBackground;
            ToolTipService.SetToolTip(AutoMixButton, IsAutoMixOn()
                ? $"AutoMix on — {styleName} ({AutoMixRequestedSeconds():0.#}s) · Click to turn off, gear opens the mixer"
                : $"AutoMix off — {styleName} ({AutoMixRequestedSeconds():0.#}s) · Click to turn on, gear opens the mixer");
        }
        if (AutoMixIcon != null)
            AutoMixIcon.Foreground = IsAutoMixOn()
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A));
        if (AutoMixDurationSlider != null)
        {
            AutoMixDurationSlider.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            if (Math.Abs(AutoMixDurationSlider.Value - AutoMixRequestedSeconds()) > 0.05)
                AutoMixDurationSlider.Value = AutoMixRequestedSeconds();
            AutoMixDurationSlider.IsEnabled = IsAutoMixOn();
        }
        if (AutoMixDurationPanel != null)
            AutoMixDurationPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (AutoMixDurationLabel != null)
        {
            AutoMixDurationLabel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            AutoMixDurationLabel.Text = $"{AutoMixRequestedSeconds():0.#}s";
        }
        if (AutoMixBetaBadge != null)
            AutoMixBetaBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNextUp()
    {
        if (NextUpLabel == null) return;
        string styleName = FormatStyleName(_settings.AutoMixStyle);
        if (_mixNext != null && _mixNextIndex >= 0 && _mixNextIndex < _tracks.Count)
        {
            NextUpLabel.Text = _mixBeatInfo != null
                ? $"🎧 AutoMix ({styleName}) → {_tracks[_mixNextIndex].Name} · {_mixBeatInfo}"
                : $"🎧 AutoMix ({styleName}) → {_tracks[_mixNextIndex].Name}";
            return;
        }
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
            NextUpLabel.Text = IsAutoMixOn() && _engine.IsLoaded && _repeatMode != RepeatOne
                ? $"🎧 AutoMix in {AutoMixRequestedSeconds():0.#}s ({styleName}) → {_tracks[next].Name}"
                : $"Playing Next: {_tracks[next].Name}";
        else if (_repeatMode == RepeatAll)
            NextUpLabel.Text = IsAutoMixOn() && _engine.IsLoaded
                ? $"🎧 AutoMix in {AutoMixRequestedSeconds():0.#}s ({styleName}) → {_tracks[0].Name} (repeat all)"
                : $"Playing Next: {_tracks[0].Name} (repeat all)";
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
            CancelAutoMix();
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

            // Warm the beat-grid cache so the tail grid is ready if AutoMix fires.
            string warmPath = path;
            _ = Task.Run(() => BeatGridCache.Precompute(warmPath, BeatGridWindow.Tail));

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
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (!_engine.IsLoaded) return;
        if (_engine.IsPlaying)
        {
            CancelAutoMix();
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
        CancelAutoMix();
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
        RefreshTaskbarButtons();
    }

    private async void ReverseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded) return;
        CancelAutoMix();
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

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _engine.Volume = (float)(e.NewValue / 100.0);
        var incoming = _mixNext;
        if (incoming != null)
        {
            try { incoming.Volume = (float)(e.NewValue / 100.0); } catch { /* ignore */ }
        }
    }

    // ---------- Seek ----------

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || !_engine.IsLoaded) return;
        CancelAutoMix();
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
        CancelAutoMix();
        _engine.Progress = progress;
        RefreshPosition();
        RefreshSmtcTimeline(force: true);
        RefreshDiscordPresence(force: true);
    }

    /// <summary>Mobile-style magnifier: cycles waveform zoom levels 0..10.</summary>
    /// <summary>Populates the waveform zoom dropdown (mobile parity: Level 0..10).</summary>
    private void InitZoomMenu()
    {
        if (ZoomMenuFlyout == null) return;
        ZoomMenuFlyout.Items.Clear();
        for (int level = 0; level <= 10; level++)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = $"Level {level}",
                Tag = level,
                GroupName = "WaveformZoom",
                IsChecked = Waveform.ZoomLevel == level,
            };
            item.Click += ZoomLevelMenu_Click;
            ZoomMenuFlyout.Items.Add(item);
        }
    }

    private void ZoomMenuFlyout_Opening(object? sender, object e)
    {
        if (ZoomMenuFlyout == null) return;
        foreach (var item in ZoomMenuFlyout.Items)
        {
            if (item is RadioMenuFlyoutItem radio && radio.Tag is int level)
                radio.IsChecked = Waveform.ZoomLevel == level;
        }
    }

    private void ZoomLevelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is int level)
        {
            Waveform.ZoomLevel = level;
            ZoomBadge.Text = Waveform.ZoomLevel.ToString();
        }
    }

    private void RefreshPosition()
    {
        if (!_engine.IsLoaded)
        {
            try { _taskbar.Clear(WindowHandle); } catch { /* ignore */ }
            return;
        }
        _engine.Update();

        // AutoMix (Beta, Canary only): pre-warm the next grid, then start the
        // crossfade on schedule and ramp it.
        if (_mixNext != null)
            UpdateAutoMixFade();
        else
        {
            MaybePrewarmAutoMix();
            MaybeStartAutoMix();
        }

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
        RefreshTaskbarProgress();
        // Steady-state Discord refresh (drift correction + reconnect), throttled.
        if ((DateTime.UtcNow - _lastDiscordPush).TotalSeconds >= 15)
            RefreshDiscordPresence(force: false);
    }

    /// <summary>
    /// Taskbar thumbnail toolbar (hover preview): Prev / Play-Pause / Next.
    /// Clicks arrive via THBN_CLICKED on the window message loop.
    /// </summary>
    private void InitTaskbarThumb()
    {
        try
        {
            var hwnd = WindowHandle;
            if (hwnd == IntPtr.Zero) return;
            _taskbar.EnsureThumbBar(hwnd);
            _taskbar.PrevClicked += (_, _) => DispatcherQueue.TryEnqueue(async () =>
            {
                try { await MoveSelection(-1, wrap: _repeatMode == RepeatAll); } catch { /* ignore */ }
            });
            _taskbar.PlayPauseClicked += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                try { TogglePlayPause(); } catch { /* ignore */ }
            });
            _taskbar.NextClicked += (_, _) => DispatcherQueue.TryEnqueue(async () =>
            {
                try { await SmtcNextAsync(); } catch { /* ignore */ }
            });
            RefreshTaskbarButtons();
        }
        catch { /* taskbar is best-effort */ }
    }

    private void RefreshTaskbarButtons()
    {
        try
        {
            var hwnd = WindowHandle;
            if (hwnd == IntPtr.Zero) return;
            _taskbar.EnsureThumbBar(hwnd);
            _taskbar.UpdateThumbButtons(
                hwnd,
                isPlaying: _engine.IsLoaded && _engine.IsPlaying,
                hasTrack: _engine.IsLoaded,
                canPrev: CanGoPrevious(),
                canNext: CanGoNext());
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Mirrors song progress onto the taskbar button (Cider-style): green fill
    /// while playing, yellow when paused, hidden when stopped. During an
    /// AutoMix crossfade the incoming track is the one taking over, so it is
    /// shown. A stopped engine sits at position 0, which clears the bar.
    /// </summary>
    private void RefreshTaskbarProgress()
    {
        try
        {
            var hwnd = WindowHandle;
            if (hwnd == IntPtr.Zero || !_engine.IsLoaded)
            {
                _taskbar.Clear(hwnd);
                return;
            }
            var active = _mixNext ?? _engine;
            double progress = Math.Clamp(active.Progress, 0, 1);
            if (!active.IsPlaying && progress <= 0.001)
            {
                _taskbar.Clear(hwnd);
                return;
            }
            _taskbar.SetProgress(hwnd, progress, paused: !active.IsPlaying);
        }
        catch { /* taskbar is best-effort */ }
    }

    // ---------- Tempo / pitch ----------

    private void TempoSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TempoLabel == null) return;
        TempoLabel.Text = $"{e.NewValue:0}%";
        _engine.Tempo = e.NewValue / 100.0;
        var incoming = _mixNext;
        if (incoming != null)
        {
            // Preserve the beat-sync ratio so both tracks stay locked.
            try { incoming.Tempo = e.NewValue / 100.0 * _mixTempoRatio; } catch { /* ignore */ }
        }
        UpdateEffectiveLabel();
        RefreshSmtcTimeline(force: true);
        RefreshDiscordPresence(force: false);
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
        var incoming = _mixNext;
        if (incoming != null)
        {
            try { incoming.PitchSemitones = e.NewValue; } catch { /* ignore */ }
        }
        RefreshDiscordPresence(force: false);
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

    private async void SetBButton_Click(object sender, RoutedEventArgs e)
    {
        var newB = _engine.SourcePosition;
        if (_engine.LoopA.HasValue && newB <= _engine.LoopA.Value)
        {
            await ShowErrorAsync(
                $"Loop end (B) must be after loop start (A).\n\nA = {FormatTime(_engine.LoopA.Value)} — move past A and try again.");
            return;
        }
        _engine.LoopB = newB;
        UpdateLoopUi(autoEnable: true);
    }

    private void ClearLoopButton_Click(object sender, RoutedEventArgs e) => ClearLoopUi();

    private void LoopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _engine.LoopEnabled = LoopCheckBox.IsChecked == true;
        if (_engine.LoopEnabled)
            CancelAutoMix();
        UpdateNextUp();
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
    private void NavInsiderButton_Click(object sender, RoutedEventArgs e) => ShowView("insider");
    private void NavSettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsButton_Click(sender, e);

    private void ShowView(string view)
    {
        bool eq = view == "eq";
        bool insider = view == "insider";
        bool settings = view == "settings";
        if (EqView == null || PlayerView == null || InsiderView == null) return;

        // Hide the current sidebar (NavRail) and top toolbar when in Settings
        if (NavRail != null)
            NavRail.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        if (TopToolbar != null)
            TopToolbar.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;

        if (SettingsSearchBox != null)
            SettingsSearchBox.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;

        if (AppTitleBarLeft != null && AppTitleBarFull != null)
            SetTitleBar(settings ? AppTitleBarLeft : AppTitleBarFull);

        if (SettingsView != null)
            SettingsView.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        EqView.Visibility = eq ? Visibility.Visible : Visibility.Collapsed;
        InsiderView.Visibility = insider ? Visibility.Visible : Visibility.Collapsed;
        PlayerView.Visibility = (!eq && !insider && !settings) ? Visibility.Visible : Visibility.Collapsed;

        if (NavPlayerButton != null)
            NavPlayerButton.Background = (!eq && !insider && !settings) ? _reverseActiveBackground : _navIdleBackground;
        if (NavEqButton != null)
            NavEqButton.Background = eq ? _reverseActiveBackground : _navIdleBackground;
        if (NavInsiderButton != null)
            NavInsiderButton.Background = insider ? _reverseActiveBackground : _navIdleBackground;
        if (NavSettingsButton != null)
            NavSettingsButton.Background = settings ? _reverseActiveBackground : _navIdleBackground;

        if (insider)
            RefreshInsiderHubAsync();
        if (settings)
        {
            SetSettingsSidebarExpanded(_settingsSidebarExpanded);
            SyncSettingsViewFromModel();
        }
    }

    // ---------- Effects chain (Equalizer APO style) ----------

    private void AddEqButton_Click(object sender, RoutedEventArgs e)
    {
        // One EQ per chain is plenty — the button disables once one exists,
        // but guard here too since the chain can outlive the UI state.
        if (_engine.SnapshotEffects().Any(b => !b.IsPreamp)) return;
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
            // One EQ per chain: once an EQ exists (even from an older chain),
            // no more can be added. Preamps stay unlimited.
            bool hasEq = blocks.Any(b => !b.IsPreamp);
            if (AddEqButton != null)
            {
                AddEqButton.IsEnabled = !hasEq;
                ToolTipService.SetToolTip(AddEqButton,
                    hasEq ? "Only one Graphic EQ per chain" : "Add graphic EQ");
            }
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
            // Fixed height: breaks scroll-viewport measure feedback that froze
            // layout (value ~14px + slider 100px + freq ~12px + room to spare).
            Height = 150,
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
        RefreshTaskbarButtons();
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
                if (mode == RepeatOne)
                    CancelAutoMix();
                UpdateShuffleRepeatUi();
                UpdateAutoMixUi();
                UpdateNextUp();
            });
            _smtc.UpdateShuffleRepeat(_shuffle, _repeatMode);
            RefreshSmtcPlayback();
        }
        catch { /* media keys are best-effort */ }
    }

    private async Task SmtcNextAsync()
    {
        CancelAutoMix();
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
        RefreshDiscordPresence(force: true);
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

    // ---------- Discord Rich Presence (Cider-style) ----------

    private void InitDiscord()
    {
        try
        {
            _discord.StatusChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateDiscordStatusUi);
            _discord.Configure(_settings.DiscordEnabled, _settings.DiscordClientId);
            UpdateDiscordStatusUi();
            RefreshDiscordPresence(force: true);
        }
        catch { /* presence is best-effort */ }
    }

    /// <summary>
    /// Pushes playback state to Discord. Forced updates (track / play-pause /
    /// seek) go immediately; steady refreshes are throttled to stay within
    /// Discord's rate limits — the service dedupes the rest.
    /// </summary>
    private void RefreshDiscordPresence(bool force = false)
    {
        try
        {
            if (!force && (DateTime.UtcNow - _lastDiscordPush).TotalSeconds < 5) return;
            _lastDiscordPush = DateTime.UtcNow;
            if (!_engine.IsLoaded)
            {
                _discord.Clear();
                return;
            }
            _discord.Refresh(
                _engine.FilePath,
                isLoaded: true,
                isPlaying: _engine.IsPlaying,
                position: _engine.SourcePosition,
                duration: _engine.SourceDuration,
                tempoPercent: TempoSlider.Value,
                pitchSemitones: PitchSlider.Value,
                showTempoPitch: _settings.DiscordShowTempoPitch,
                force: force);
        }
        catch { /* never break playback for presence */ }
    }

    private void UpdateDiscordStatusUi()
    {
        try
        {
            if (SettingsDiscordStatusLabel != null)
                SettingsDiscordStatusLabel.Text = "Status: " + _discord.StatusText;
        }
        catch { /* settings view may not exist yet */ }
    }

    private void SettingsDiscordEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.DiscordEnabled = SettingsDiscordEnabledSwitch.IsOn;
        _settings.Save();
        _discord.Configure(_settings.DiscordEnabled, _settings.DiscordClientId);
        UpdateDiscordStatusUi();
        RefreshDiscordPresence(force: true);
    }

    private void SettingsDiscordClientIdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.DiscordClientId = (SettingsDiscordClientIdBox.Text ?? "").Trim();
        _settings.Save();
        _discord.Configure(_settings.DiscordEnabled, _settings.DiscordClientId);
        UpdateDiscordStatusUi();
        RefreshDiscordPresence(force: true);
    }

    private void SettingsDiscordTempoPitchBox_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingSettingsView) return;
        _settings.DiscordShowTempoPitch = SettingsDiscordTempoPitchBox.IsChecked == true;
        _settings.Save();
        RefreshDiscordPresence(force: true);
    }

    private void SettingsDiscordReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DiscordClientId = (SettingsDiscordClientIdBox?.Text ?? "").Trim();
        _settings.Save();
        _discord.Configure(_settings.DiscordEnabled, _settings.DiscordClientId);
        _discord.Reconnect();
        UpdateDiscordStatusUi();
        RefreshDiscordPresence(force: true);
    }

    private async void SettingsDiscordHelpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(
                new Uri(DiscordPresenceService.DeveloperPortalUrl));
        }
        catch { /* best effort */ }
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
