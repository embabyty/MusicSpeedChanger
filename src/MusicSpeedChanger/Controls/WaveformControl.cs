using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicSpeedChanger.Audio;

namespace MusicSpeedChanger.Controls;

/// <summary>
/// Clickable waveform with playhead + AB loop region, WinUI 3 version.
/// Rendered into a WriteableBitmap (no WPF DrawingContext / no Win2D dependency).
/// </summary>
public sealed class WaveformControl : Grid
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformControl),
            new PropertyMetadata(0.0, (d, _) => ((WaveformControl)d).Render()));

    public static readonly DependencyProperty LoopAProperty =
        DependencyProperty.Register(nameof(LoopA), typeof(double?), typeof(WaveformControl),
            new PropertyMetadata(null, (d, _) => ((WaveformControl)d).Render()));

    public static readonly DependencyProperty LoopBProperty =
        DependencyProperty.Register(nameof(LoopB), typeof(double?), typeof(WaveformControl),
            new PropertyMetadata(null, (d, _) => ((WaveformControl)d).Render()));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public double? LoopA
    {
        get => (double?)GetValue(LoopAProperty);
        set => SetValue(LoopAProperty, value);
    }

    public double? LoopB
    {
        get => (double?)GetValue(LoopBProperty);
        set => SetValue(LoopBProperty, value);
    }

    public WaveformData? Data
    {
        get => _data;
        set { _data = value; Render(); }
    }
    private WaveformData? _data;

    /// <summary>When false, pointer input no longer seeks (Editor Controls setting).</summary>
    public bool IsSeekEnabled { get; set; } = true;

    private Windows.UI.Color _playedColor = Windows.UI.Color.FromArgb(255, 0x3A, 0x5F, 0xC9);

    /// <summary>Color of the already-played bars; follows the system accent by default.</summary>
    public Windows.UI.Color PlayedColor
    {
        get => _playedColor;
        set { _playedColor = value; Render(); }
    }

    public event EventHandler<double>? SeekRequested; // 0..1

    private readonly Image _image;
    private readonly TextBlock _hint;
    private WriteableBitmap? _bitmap;

    // Scrolling-window state (iOS-style fixed playhead): _windowStart is the
    // fractional bar index at the left edge, _windowBars how many bars are
    // visible, _barW render-pixels per bar. Refreshed on every Render().
    private double _windowStart;
    private double _windowBars = 1;
    private double _barW = 1;
    private int _barCount = 1;

    public WaveformControl()
    {
        MinHeight = 120;
        _image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
        _hint = new TextBlock
        {
            Text = "Open an audio file to begin",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Gray),
            FontSize = 14,
        };
        Children.Add(_image);
        Children.Add(_hint);

        SizeChanged += (_, _) => Render();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSeekEnabled || ActualWidth <= 0) return;
        var p = e.GetCurrentPoint(this).Position;
        SeekRequested?.Invoke(this, PositionToProgress(p.X));
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSeekEnabled) return;        if (e.Pointer.IsInContact && ActualWidth > 0)
        {
            var p = e.GetCurrentPoint(this).Position;
            SeekRequested?.Invoke(this, PositionToProgress(p.X));
        }
    }

    /// <summary>
    /// Map a pointer X (control coordinates) to 0..1 progress using the
    /// current scrolling window. Render uses a capped bitmap width with
    /// Stretch=Fill, so scale into render pixels first.
    /// </summary>
    private double PositionToProgress(double x)
    {
        double aw = ActualWidth;
        if (aw <= 0 || _barCount <= 0 || _barW <= 0) return 0;
        double renderW = Math.Min(aw, 1400);
        double xRender = x * (renderW / aw);
        double globalBar = _windowStart + xRender / _barW;
        return Math.Clamp(globalBar / _barCount, 0, 1);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => ReleasePointerCapture(e.Pointer);

    private void Render()
    {
        int w = Math.Max(1, (int)Math.Round(ActualWidth));
        int h = Math.Max(1, (int)Math.Round(ActualHeight));
        if (w < 10 || h < 10) return;
        // Cap render size for perf (bitmap scaled up by Stretch=Fill).
        const int maxW = 1400;
        if (w > maxW) w = maxW;

        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            _image.Source = _bitmap;
        }

        bool hasData = _data != null && _data.Peaks.Length > 0;
        _hint.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        _image.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        if (!hasData)
        {
            // Still paint a background so the empty area isn't transparent.
            FillBackgroundOnly(_bitmap);
            return;
        }

        var peaks = _data!.Peaks;
        int n = peaks.Length;
        double mid = h / 2.0;

        // Scrolling window (iOS-style): the playhead stays fixed at the
        // center while the waveform moves. ~3px per bar, clamped so short
        // files still fit (then it behaves like the old fit-all view).
        double windowBars = Math.Clamp(w / 3.0, 100, 600);
        if (windowBars > n) windowBars = n;
        if (windowBars < 1) windowBars = 1;
        double progressIdx = Math.Clamp(Progress, 0, 1) * n;
        double start = progressIdx - windowBars / 2.0;
        start = Math.Clamp(start, 0, Math.Max(0, n - windowBars));
        double barW = w / windowBars;
        double playheadX = (progressIdx - start) * barW;

        _windowStart = start;
        _windowBars = windowBars;
        _barW = barW;
        _barCount = n;

        double loopBar1 = LoopA.HasValue ? LoopA.Value * n : double.NaN;
        double loopBar2 = LoopB.HasValue ? LoopB.Value * n : double.NaN;
        bool hasLoop = !double.IsNaN(loopBar1) && !double.IsNaN(loopBar2) && loopBar2 > loopBar1;

        byte[] px = new byte[w * h * 4];
        // Background #1E1E28
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x28; px[i + 1] = 0x1E; px[i + 2] = 0x1E; px[i + 3] = 255;
        }

        int firstBar = Math.Max(0, (int)Math.Floor(start));
        int lastBar = Math.Min(n - 1, (int)Math.Ceiling(start + windowBars));

        // Loop region overlay (amber ~27% alpha over bg), only for visible bars.
        if (hasLoop)
        {
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (i + 1 < loopBar1 || i > loopBar2) continue;
                int x0 = Math.Clamp((int)((i - start) * barW), 0, w - 1);
                int x1 = Math.Clamp((int)Math.Ceiling((i + 1 - start) * barW), 0, w);
                for (int y = 0; y < h; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int o = (y * w + x) * 4;
                        // Blend amber (FF C1 07) at ~70/255 over bg #1E1E28.
                        px[o] = Blend(px[o], 0x07, 70);
                        px[o + 1] = Blend(px[o + 1], 0xC1, 70);
                        px[o + 2] = Blend(px[o + 2], 0xFF, 70);
                    }
            }
        }

        // Bars (only the visible window).
        for (int i = firstBar; i <= lastBar; i++)
        {
            int x0 = Math.Clamp((int)((i - start) * barW), 0, w - 1);
            int x1 = Math.Clamp(Math.Max(x0 + 1, (int)Math.Ceiling((i + 1 - start) * barW - 0.5)), 0, w);
            double amp = Math.Clamp(peaks[i], 0, 1) * (mid - 4);
            if (amp < 1) amp = 1;
            int y0 = Math.Clamp((int)(mid - amp), 0, h - 1);
            int y1 = Math.Clamp((int)(mid + amp), 0, h - 1);
            bool played = i <= progressIdx;
            // Played bars use the accent color, unplayed bars stay #7C9EFF.
            byte b = played ? _playedColor.B : (byte)0xFF;
            byte g = played ? _playedColor.G : (byte)0x9E;
            byte r = played ? _playedColor.R : (byte)0x7C;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
                }
        }

        // Loop edge markers #FFC107, 2px (only if on screen).
        if (hasLoop)
        {
            double lx1 = (loopBar1 - start) * barW;
            double lx2 = (loopBar2 - start) * barW;
            if (lx1 >= -2 && lx1 <= w) DrawVLine(px, w, h, (int)lx1, 0x07, 0xC1, 0xFF);
            if (lx2 >= -2 && lx2 <= w) DrawVLine(px, w, h, (int)lx2, 0x07, 0xC1, 0xFF);
        }

        // Playhead white, 2px — fixed (center except at the very ends).
        DrawVLine(px, w, h, (int)playheadX, 255, 255, 255);

        using var s = _bitmap.PixelBuffer.AsStream();
        s.Seek(0, SeekOrigin.Begin);
        s.Write(px, 0, px.Length);
        s.Flush();
        _bitmap.Invalidate();
    }

    private static void FillBackgroundOnly(WriteableBitmap bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x28; px[i + 1] = 0x1E; px[i + 2] = 0x1E; px[i + 3] = 255;
        }
        using var s = bmp.PixelBuffer.AsStream();
        s.Seek(0, SeekOrigin.Begin);
        s.Write(px, 0, px.Length);
        s.Flush();
        bmp.Invalidate();
    }

    private static byte Blend(byte dst, byte src, int alpha) =>
        (byte)((src * alpha + dst * (255 - alpha)) / 255);

    private static void DrawVLine(byte[] px, int w, int h, int x, byte b, byte g, byte r)
    {
        for (int dx = 0; dx < 2; dx++)
        {
            int xx = x + dx;
            if (xx < 0 || xx >= w) continue;
            for (int y = 0; y < h; y++)
            {
                int o = (y * w + xx) * 4;
                px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
            }
        }
    }
}
