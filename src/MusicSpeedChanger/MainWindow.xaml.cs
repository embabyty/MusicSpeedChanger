using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicSpeedChanger.Audio;

namespace MusicSpeedChanger;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _timer;
    private readonly Brush _reverseActiveBackground = new SolidColorBrush(Color.FromRgb(0x6A, 0x3F, 0xB5));
    private Brush _reverseIdleBackground = Brushes.Transparent;
    private bool _seekDragging;
    private bool _updatingSeek;
    private bool _exporting;

    // ----- Equalizer UI state -----
    private readonly Slider[] _eqSliders = new Slider[GraphicEqualizer.BandCount];
    private bool _updatingEq;

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
        BuildEqUi();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => RefreshPosition();
        _timer.Start();

        _engine.PlaybackEnded += (_, _) => Dispatcher.Invoke(() =>
        {
            _engine.Stop();
            PlayButton.Content = "▶ Play";
            RefreshPosition();
        });

        Closed += (_, _) => _engine.Dispose();
        UpdateTransportState();
    }

    // ---------- File ----------

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.aiff;*.aif;*.flac|All files|*.*",
            Title = "Open audio file"
        };
        if (dlg.ShowDialog() != true) return;

        await LoadFileAsync(dlg.FileName);
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            StatusLabel.Text = "Loading…";
            await Task.Run(() => _engine.Load(path));

            // Apply current UI settings to the fresh engine
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
            var data = await Task.Run(() => WaveformData.FromFile(copy, 1400));
            // Ignore if user opened another file meanwhile
            if (_engine.FilePath == copy)
                Waveform.Data = data;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open file:\n{ex.Message}", "Music Speed Changer",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, $"Could not enable reverse:\n{ex.Message}", "Music Speed Changer",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _engine.Volume = (float)(e.NewValue / 100.0);

    // ---------- Seek ----------

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seekDragging = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _seekDragging = false;
        if (_engine.IsLoaded)
            _engine.Progress = SeekSlider.Value / 1000.0;
        RefreshPosition();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || !_engine.IsLoaded) return;
        if (_seekDragging)
            _engine.Progress = e.NewValue / 1000.0;
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

    private void TempoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent before TempoLabel exists.
        if (TempoLabel == null) return;
        TempoLabel.Text = $"{e.NewValue:0}%";
        _engine.Tempo = e.NewValue / 100.0;
        UpdateEffectiveLabel();
    }

    private void TempoPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && double.TryParse(b.Tag?.ToString(), out double v))
            TempoSlider.Value = v;
    }

    private void ResetTempo_Click(object sender, RoutedEventArgs e) => TempoSlider.Value = 100;

    private void PitchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent before PitchLabel exists.
        if (PitchLabel == null) return;
        PitchLabel.Text = $"{(e.NewValue >= 0 ? "+" : "")}{e.NewValue:0.0} st";
        _engine.PitchSemitones = e.NewValue;
    }

    private void PitchPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && double.TryParse(b.Tag?.ToString(), out double v))
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
                IsSnapToTickEnabled = true,
                Tag = band,
                ToolTip = EqTip(freq, 0),
            };
            slider.ValueChanged += EqBandSlider_ValueChanged;
            _eqSliders[band] = slider;

            var label = new TextBlock
            {
                Text = GraphicEqualizer.ShortLabels[band],
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x8A)),
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

    private void EqBandSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingEq || sender is not Slider s || s.Tag is not int band) return;
        _engine.SetEqGain(band, (float)e.NewValue);
        s.ToolTip = EqTip(GraphicEqualizer.CenterFrequencies[band], e.NewValue);
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
        // Fires during InitializeComponent before EqBandsPanel exists.
        if (EqBandsPanel != null)
            EqBandsPanel.IsEnabled = on;
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
                _eqSliders[i].ToolTip = EqTip(GraphicEqualizer.CenterFrequencies[i], gains[i]);
            }
        }
        finally { _updatingEq = false; }
    }

    // ---------- Export ----------

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsLoaded || _exporting) return;
        var dlg = new SaveFileDialog
        {
            Filter = "WAV audio|*.wav",
            FileName = Path.GetFileNameWithoutExtension(_engine.FileName ?? "track")
                        + $"_{TempoSlider.Value:0}pct_{(PitchSlider.Value >= 0 ? "+" : "")}{PitchSlider.Value:0.0}st"
                        + (_engine.EqEnabled && !_engine.EqIsFlat ? "_eq" : "") + ".wav",
            Title = "Export with current tempo / pitch / EQ"
        };
        if (dlg.ShowDialog() != true) return;

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
            await _engine.ExportWavAsync(dlg.FileName, TempoSlider.Value / 100.0, PitchSlider.Value, from, to, progress);
            StatusLabel.Text = $"Exported ✓ {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "Music Speed Changer",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
