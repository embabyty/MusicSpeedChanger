using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using MusicSpeedChanger.Audio;
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

    // ----- Equalizer UI state -----
    private readonly Slider[] _eqSliders = new Slider[GraphicEqualizer.BandCount];
    private bool _updatingEq;

    // ----- Settings / appearance / updates -----
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly UISettings _uiSettings = new();

    // 31 gains each, band order = GraphicEqualizer.CenterFrequencies (20 Hz … 20 kHz).
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

        // Mica backdrop: visible in the transparent title-bar strip (content keeps
        // its dark background below). Falls back to a solid color off Windows 11.
        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StyleCaptionButtons();

        // WinUI windows have no XAML Width/Height — size the AppWindow instead.
        TrySizeWindow(980, 860);
        TrySetIcon();

        ApplyStartupState();
        BuildEqUi();
        ApplyRememberedEq();
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
            }), true);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => RefreshPosition();
        _timer.Start();

        _engine.PlaybackEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _engine.Stop();
            PlayButton.Content = "▶ Play";
            RefreshPosition();
        });

        Closed += (_, _) =>
        {
            CaptureEffectState();
            _settings.Save();
            _engine.Dispose();
        };
        UpdateTransportState();

        if (_settings.AutoCheckUpdates)
            CheckForUpdatesOnStartupAsync();
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
        EqCard.Visibility = _settings.ShowEqPanel ? Visibility.Visible : Visibility.Collapsed;
        Waveform.IsSeekEnabled = _settings.ClickToSeek;
        ToolTipService.SetToolTip(Waveform, _settings.ClickToSeek
            ? "Click to seek"
            : "Seeking from the waveform is off (Settings → Editor controls)");
    }

    private void ApplyRememberedEq()
    {
        if (_settings.RememberEffects && _settings.LastEqGains != null)
        {
            _engine.SetEqGains(_settings.LastEqGains);
            ApplyEqToSliders(_settings.LastEqGains);
        }
        EqEnableCheckBox.IsChecked = !_settings.RememberEffects || _settings.LastEqEnabled;
    }

    private void CaptureEffectState()
    {
        _settings.LastTempoPercent = TempoSlider.Value;
        _settings.LastPitchSemitones = PitchSlider.Value;
        _settings.LastVolumePercent = VolumeSlider.Value;
        _settings.LastEqGains = _engine.GetEqGains();
        _settings.LastEqEnabled = _engine.EqEnabled;
    }

    private void ApplyAccent()
    {
        Windows.UI.Color accent = _settings.UseSystemAccent
            ? _uiSettings.GetColorValue(UIColorType.Accent)
            : ParseHex(_settings.CustomAccentHex, Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
        var brush = new SolidColorBrush(accent);
        PlayButton.Background = brush;
        PlayButton.BorderBrush = brush;
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
            var info = await UpdateService.CheckForUpdateAsync(_settings.UpdateFeedUrl);
            if (info == null) return;
            await PromptUpdateAsync(info);
        }
        catch { /* silent: updates are best-effort */ }
    }

    private async Task PromptUpdateAsync(UpdateInfo info)
    {
        var dlg = new ContentDialog
        {
            Title = $"Update available — {info.Version}",
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

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".aac");
        picker.FileTypeFilter.Add(".wma");
        picker.FileTypeFilter.Add(".aiff");
        picker.FileTypeFilter.Add(".aif");
        picker.FileTypeFilter.Add(".flac");
        InitializeWithWindow.Initialize(picker, WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        await LoadFileAsync(file.Path);
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
            PlayButton.Content = "▶ Play";
        }
        else
        {
            _engine.Play();
            PlayButton.Content = "⏸ Pause";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        PlayButton.Content = "▶ Play";
        RefreshPosition();
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
        ReverseButton.Content = on ? "➡ Forward" : "⏪ Reverse";
        ReverseButton.Background = on ? _reverseActiveBackground : _reverseIdleBackground;
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
    }

    // ---------- Tempo / pitch ----------

    private void TempoSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TempoLabel == null) return;
        TempoLabel.Text = $"{e.NewValue:0}%";
        _engine.Tempo = e.NewValue / 100.0;
        UpdateEffectiveLabel();
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

    // ---------- 31-band equalizer ----------

    private void BuildEqUi()
    {
        foreach (var name in EqPresets.Keys)
            EqPresetBox.Items.Add(name);

        for (int band = 0; band < GraphicEqualizer.BandCount; band++)
        {
            float freq = GraphicEqualizer.CenterFrequencies[band];
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = GraphicEqualizer.MinGainDb,
                Maximum = GraphicEqualizer.MaxGainDb,
                Value = 0,
                Height = 95,
                Width = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                TickFrequency = 1,
                StepFrequency = 1,
                Tag = band,
            };
            ToolTipService.SetToolTip(slider, EqTip(freq, 0));
            slider.ValueChanged += EqBandSlider_ValueChanged;
            _eqSliders[band] = slider;

            var label = new TextBlock
            {
                Text = GraphicEqualizer.ShortLabels[band],
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x8A)),
                TextAlignment = TextAlignment.Center,
                Width = 34,
            };

            var col = new StackPanel { Width = 34, Margin = new Thickness(1, 0, 1, 0) };
            col.Children.Add(slider);
            col.Children.Add(label);
            EqBandsPanel.Children.Add(col);
        }

        // Select Flat preset last: SelectionChanged applies gains to the sliders,
        // which must exist first.
        EqPresetBox.SelectedIndex = 0; // Flat
    }

    private static string EqTip(float freq, double gain) =>
        $"{(freq >= 1000 ? $"{freq / 1000:0.##} kHz" : $"{freq:0.#} Hz")}: {(gain >= 0 ? "+" : "")}{gain:0} dB";

    private void EqBandSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingEq || sender is not Slider s || s.Tag is not int band) return;
        _engine.SetEqGain(band, (float)e.NewValue);
        ToolTipService.SetToolTip(s, EqTip(GraphicEqualizer.CenterFrequencies[band], e.NewValue));
        // Manual tweak → no longer exactly a preset.
        _updatingEq = true;
        try { EqPresetBox.SelectedIndex = -1; }
        finally { _updatingEq = false; }
    }

    private void EqPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEq || EqPresetBox.SelectedItem is not string name) return;
        if (!EqPresets.TryGetValue(name, out var gains)) return;
        _engine.SetEqGains(gains);
        ApplyEqToSliders(gains);
    }

    private void EqFlatButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.ResetEq();
        ApplyEqToSliders(new float[GraphicEqualizer.BandCount]);
        _updatingEq = true;
        try { EqPresetBox.SelectedIndex = EqPresetBox.Items.IndexOf("Flat"); }
        finally { _updatingEq = false; }
    }

    private void EqEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool on = EqEnableCheckBox.IsChecked == true;
        _engine.EqEnabled = on;
        // StackPanel (Panel) has no IsEnabled in WinUI — disable each band slider.
        foreach (var s in _eqSliders)
        {
            if (s != null) s.IsEnabled = on;
        }
        if (EqBandsPanel != null)
            EqBandsPanel.Opacity = on ? 1.0 : 0.45;
    }

    private void ApplyEqToSliders(float[] gains)
    {
        _updatingEq = true;
        try
        {
            for (int i = 0; i < _eqSliders.Length; i++)
            {
                if (_eqSliders[i] == null) continue;
                _eqSliders[i].Value = gains[i];
                ToolTipService.SetToolTip(_eqSliders[i], EqTip(GraphicEqualizer.CenterFrequencies[i], gains[i]));
            }
        }
        finally { _updatingEq = false; }
    }

    // ---------- Export ----------

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded || _exporting) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(_engine.FileName ?? "track")
                        + $"_{TempoSlider.Value:0}pct_{(PitchSlider.Value >= 0 ? "+" : "")}{PitchSlider.Value:0.0}st"
                        + (_engine.EqEnabled && !_engine.EqIsFlat ? "_eq" : ""),
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
            // Export loop region if fully set, else whole track.
            TimeSpan? from = null, to = null;
            if (_engine.LoopA.HasValue && _engine.LoopB.HasValue && _engine.LoopB > _engine.LoopA)
            { from = _engine.LoopA; to = _engine.LoopB; }

            var progress = new Progress<double>(p => StatusLabel.Text = $"Exporting… {p * 100:0}%");
            StatusLabel.Text = "Exporting…";
            // StorageFile may be brokered — use cached path when available, else the picked path.
            string dest = file.Path;
            if (string.IsNullOrEmpty(dest))
                dest = picker.SuggestedFileName + ".wav";
            await _engine.ExportWavAsync(dest, TempoSlider.Value / 100.0, PitchSlider.Value, from, to, progress);
            StatusLabel.Text = $"Exported ✓ {Path.GetFileName(dest)}";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Export failed:\n{ex.Message}");
            StatusLabel.Text = "Export failed";
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
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
