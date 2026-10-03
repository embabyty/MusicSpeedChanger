using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicSpeedChanger.Audio;
using MusicSpeedChanger.Services;

namespace MusicSpeedChanger;

/// <summary>
/// Spotify-style AutoMix mixer popup: outgoing/incoming track headers with
/// detected BPM, a dual-waveform mix view with the crossfade zone, a preview
/// button, a bars picker, and the transition style customization (moved here
/// from Settings and the old right-click flyout). Changes apply live.
/// </summary>
public sealed partial class AutoMixDialog : ContentDialog
{
    private static readonly int[] BarOptions = { 1, 2, 4, 8 };

    private readonly MainWindow _owner;
    private readonly AppSettings _settings;

    private bool _loading = true;
    private bool _syncingBars;
    private bool _peaksReady;

    private string? _outPath;
    private string? _inPath;
    private WaveformData? _outPeaks;
    private WaveformData? _inPeaks;
    private WriteableBitmap? _bitmap;

    public AutoMixDialog(MainWindow owner, AppSettings settings)
    {
        InitializeComponent();
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        MixImage.SizeChanged += (_, _) => { if (_peaksReady) RenderMixView(); };
        Opened += AutoMixDialog_Opened;
    }

    private async void AutoMixDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            EnableToggle.IsOn = _settings.AutoMixEnabled;
            DurationSlider.Value = Math.Clamp(_settings.AutoMixSeconds, 1, 12);
            DurationValueLabel.Text = $"{_settings.AutoMixSeconds:0.#}s";
            CheckStyleRadio(AppSettings.NormalizeAutoMixStyle(_settings.AutoMixStyle));
            BeatSyncBox.IsChecked = _settings.AutoMixBeatSync;
            SkipSilenceBox.IsChecked = _settings.AutoMixSkipSilence;

            if (!_owner.TryGetAutoMixPair(out var current, out var next) ||
                current == null || next == null)
            {
                SetHeader(OutTitle, OutArtist, OutArtLetter, OutBpm, OutDuration, null);
                SetHeader(InTitle, InArtist, InArtLetter, InBpm, InDuration, null);
                PreviewButton.IsEnabled = false;
                BarsBox.IsEnabled = false;
                PreviewStatusLabel.Text = "Queue at least two tracks and play one to preview a transition.";
                return;
            }

            _outPath = current.Path;
            _inPath = next.Path;
            var (outTitle, outArtist) = SplitMeta(_outPath);
            var (inTitle, inArtist) = SplitMeta(_inPath);
            SetHeader(OutTitle, OutArtist, OutArtLetter, OutBpm, OutDuration,
                (outTitle, outArtist, current.DurationText));
            SetHeader(InTitle, InArtist, InArtLetter, InBpm, InDuration,
                (inTitle, inArtist, next.DurationText));
            PreviewStatusLabel.Text = "";

            string outP = _outPath, inP = _inPath;
            var loadTask = Task.Run(() =>
            {
                WaveformData? o = null, i = null;
                try { o = WaveformData.FromFile(outP, 320); } catch { }
                try { i = WaveformData.FromFile(inP, 320); } catch { }
                return (o, i);
            });
            var gridTask = Task.Run(() =>
            {
                BeatGrid? og = null, ig = null;
                try { og = BeatGridCache.GetOrAnalyze(outP, BeatGridWindow.Tail); } catch { }
                try { ig = BeatGridCache.GetOrAnalyze(inP, BeatGridWindow.Head); } catch { }
                return (og, ig);
            });

            var (oPeaks, iPeaks) = await loadTask;
            var (oGrid, iGrid) = await gridTask;

            _outPeaks = oPeaks;
            _inPeaks = iPeaks;
            _peaksReady = oPeaks != null || iPeaks != null;

            OutBpm.Text = oGrid != null && oGrid.IsReliable ? $"{oGrid.Bpm:0} BPM" : "— BPM";
            InBpm.Text = iGrid != null && iGrid.IsReliable ? $"{iGrid.Bpm:0} BPM" : "— BPM";
            if (oPeaks != null && (string.IsNullOrWhiteSpace(OutDuration.Text) || OutDuration.Text == "…"))
                OutDuration.Text = FormatDuration(oPeaks.Duration);
            if (iPeaks != null && (string.IsNullOrWhiteSpace(InDuration.Text) || InDuration.Text == "…"))
                InDuration.Text = FormatDuration(iPeaks.Duration);

            ReselectBars();
            RenderMixView();
        }
        finally { _loading = false; }
    }

    private static void SetHeader(TextBlock title, TextBlock artist, TextBlock art,
        TextBlock bpm, TextBlock duration, (string Title, string Artist, string Duration)? data)
    {
        if (data == null)
        {
            title.Text = "—";
            artist.Text = "";
            art.Text = "♪";
            bpm.Text = "— BPM";
            duration.Text = "";
            return;
        }
        title.Text = data.Value.Title;
        artist.Text = data.Value.Artist;
        art.Text = data.Value.Title.Length > 0
            ? data.Value.Title[..1].ToUpperInvariant()
            : "♪";
        bpm.Text = "… BPM";
        duration.Text = data.Value.Duration == "…" ? "…" : data.Value.Duration;
    }

    private static (string Title, string Artist) SplitMeta(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int sep = name.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
            return (name[(sep + 3)..].Trim(), name[..sep].Trim());
        string? parent = Path.GetDirectoryName(path);
        parent = string.IsNullOrEmpty(parent) ? null : Path.GetFileName(parent);
        return (name, string.IsNullOrWhiteSpace(parent) ? "Unknown artist" : parent!);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    // ---------- Mix view rendering ----------

    private void RenderMixView()
    {
        int w = Math.Max(1, (int)Math.Round(MixImage.ActualWidth));
        const int h = 220;
        if (w < 10) return;
        const int maxW = 880;
        if (w > maxW) w = maxW;

        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            MixImage.Source = _bitmap;
        }

        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x16; px[i + 1] = 0x10; px[i + 2] = 0x10; px[i + 3] = 255;
        }

        int mid = h / 2;
        // Divider between the halves.
        for (int x = 0; x < w; x++)
        {
            int o = (mid * w + x) * 4;
            px[o] = 0x35; px[o + 1] = 0x2A; px[o + 2] = 0x2A; px[o + 3] = 255;
        }

        if (_outPeaks != null)
            DrawHalf(px, w, h, 2, mid - 3, _outPeaks.Peaks, 0xE1, 0x7D, 0x2F);
        if (_inPeaks != null)
            DrawHalf(px, w, h, mid + 3, h - 2, _inPeaks.Peaks, 0x4B, 0x8A, 0xC9);

        // Crossfade zone: last mixSec of outgoing, first mixSec of incoming.
        if (_outPeaks != null && _inPeaks != null)
        {
            double mixSec = Math.Clamp(_settings.AutoMixSeconds, 1, 12);
            double outDur = Math.Max(1, _outPeaks.Duration.TotalSeconds);
            double inDur = Math.Max(1, _inPeaks.Duration.TotalSeconds);
            int outX0 = (int)(w * Math.Clamp(1 - mixSec / outDur, 0, 1));
            int inX1 = (int)(w * Math.Clamp(mixSec / inDur, 0, 1));
            // Amber frame around each zone half.
            DrawRectBorder(px, w, h, outX0, 1, w - 1, mid - 2, 0x07, 0xC1, 0xFF);
            DrawRectBorder(px, w, h, 0, mid + 2, inX1, h - 2, 0x07, 0xC1, 0xFF);
            // Green mix-point line at the outgoing zone start.
            for (int y = 2; y < mid - 1; y++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int x = outX0 + dx;
                    if (x < 0 || x >= w) continue;
                    int o = (y * w + x) * 4;
                    px[o] = 0x30; px[o + 1] = 0xDD; px[o + 2] = 0x30; px[o + 3] = 255;
                }
        }

        using var s = _bitmap.PixelBuffer.AsStream();
        s.Seek(0, SeekOrigin.Begin);
        s.Write(px, 0, px.Length);
        s.Flush();
        _bitmap.Invalidate();
    }

    private static void DrawHalf(byte[] px, int w, int h, int yTop, int yBottom,
        float[] peaks, byte b, byte g, byte r)
    {
        int n = peaks.Length;
        if (n == 0) return;
        double mid = (yTop + yBottom) / 2.0;
        double ampMax = Math.Max(4, (yBottom - yTop) / 2.0 - 3);
        for (int x = 0; x < w; x++)
        {
            int i = Math.Clamp((int)((double)x / w * n), 0, n - 1);
            double amp = Math.Clamp(peaks[i], 0, 1) * ampMax;
            if (amp < 1) amp = 1;
            int y0 = Math.Clamp((int)(mid - amp), yTop, yBottom);
            int y1 = Math.Clamp((int)(mid + amp), yTop, yBottom);
            for (int y = y0; y <= y1; y++)
            {
                int o = (y * w + x) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
        }
    }

    private static void DrawRectBorder(byte[] px, int w, int h,
        int x0, int y0, int x1, int y1, byte b, byte g, byte r)
    {
        x0 = Math.Clamp(x0, 0, w - 1); x1 = Math.Clamp(x1, 0, w - 1);
        y0 = Math.Clamp(y0, 0, h - 1); y1 = Math.Clamp(y1, 0, h - 1);
        if (x1 - x0 < 3 || y1 - y0 < 3) return;
        for (int x = x0; x <= x1; x++)
            for (int t = 0; t < 2; t++)
            {
                SetPx(px, w, h, x, y0 + t, b, g, r);
                SetPx(px, w, h, x, y1 - t, b, g, r);
            }
        for (int y = y0; y <= y1; y++)
            for (int t = 0; t < 2; t++)
            {
                SetPx(px, w, h, x0 + t, y, b, g, r);
                SetPx(px, w, h, x1 - t, y, b, g, r);
            }
    }

    private static void SetPx(byte[] px, int w, int h, int x, int y, byte b, byte g, byte r)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int o = (y * w + x) * 4;
        px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
    }

    // ---------- Controls ----------

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        PreviewButton.IsEnabled = false;
        PreviewStatusLabel.Text = "Starting…";
        try
        {
            var (started, message) = await _owner.PreviewAutoMixAsync();
            if (started)
            {
                Hide();
                return;
            }
            PreviewStatusLabel.Text = message;
        }
        finally { PreviewButton.IsEnabled = true; }
    }

    private void BarsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingBars) return;
        if (BarsBox.SelectedIndex < 0 || BarsBox.SelectedIndex >= BarOptions.Length) return;
        double? seconds = _owner.BarsToSeconds(BarOptions[BarsBox.SelectedIndex]);
        if (seconds == null)
        {
            PreviewStatusLabel.Text = "Bars need a detected BPM on the current track.";
            return;
        }
        _settings.AutoMixSeconds = Math.Clamp(seconds.Value, 1, 12);
        _settings.Save();
        _syncingBars = true;
        try
        {
            DurationSlider.Value = _settings.AutoMixSeconds;
            DurationValueLabel.Text = $"{_settings.AutoMixSeconds:0.#}s";
        }
        finally { _syncingBars = false; }
        _owner.RefreshAutoMixUi();
        RenderMixView();
    }

    private void ReselectBars()
    {
        _syncingBars = true;
        try
        {
            int? bars = _owner.SecondsToBars(_settings.AutoMixSeconds);
            int idx = bars.HasValue ? Array.IndexOf(BarOptions, bars.Value) : -1;
            BarsBox.SelectedIndex = idx;
            BarsBox.IsEnabled = idx >= 0;
        }
        finally { _syncingBars = false; }
    }

    private void DurationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoMixSeconds = Math.Clamp(e.NewValue, 1, 12);
        _settings.Save();
        DurationValueLabel.Text = $"{_settings.AutoMixSeconds:0.#}s";
        _owner.RefreshAutoMixUi();
        if (!_syncingBars) ReselectBars();
        RenderMixView();
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoMixEnabled = EnableToggle.IsOn;
        _settings.Save();
        _owner.RefreshAutoMixUi();
    }

    private void StyleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _settings.AutoMixStyle = AppSettings.NormalizeAutoMixStyle(tag);
            _settings.Save();
            _owner.RefreshAutoMixUi();
        }
    }

    private void CheckStyleRadio(string style)
    {
        StyleBassSwap.IsChecked = style == "BassSwap";
        StyleBlend.IsChecked = style == "Blend";
        StyleRise.IsChecked = style == "Rise";
        StyleBeatDrop.IsChecked = style == "BeatDrop";
        StyleLinear.IsChecked = style == "Linear";
    }

    private void OptionBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoMixBeatSync = BeatSyncBox.IsChecked == true;
        _settings.AutoMixSkipSilence = SkipSilenceBox.IsChecked == true;
        _settings.Save();
        _owner.RefreshAutoMixUi();
    }
}
