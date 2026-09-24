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
        SeekRequested?.Invoke(this, Math.Clamp(p.X / ActualWidth, 0, 1));
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSeekEnabled) return;        if (e.Pointer.IsInContact && ActualWidth > 0)
        {
            var p = e.GetCurrentPoint(this).Position;
            SeekRequested?.Invoke(this, Math.Clamp(p.X / ActualWidth, 0, 1));
        }
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
        double progressX = Math.Clamp(Progress, 0, 1) * w;

        double? loopX1 = LoopA.HasValue ? LoopA.Value * w : null;
        double? loopX2 = LoopB.HasValue ? LoopB.Value * w : null;
        bool hasLoop = loopX1.HasValue && loopX2.HasValue && loopX2 > loopX1;

        byte[] px = new byte[w * h * 4];
        // Background #1E1E28
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x28; px[i + 1] = 0x1E; px[i + 2] = 0x1E; px[i + 3] = 255;
        }

        // Loop region overlay (amber ~27% alpha over bg) + will draw markers later.
        if (hasLoop)
        {
            int lx1 = Math.Clamp((int)loopX1!.Value, 0, w);
            int lx2 = Math.Clamp((int)Math.Ceiling(loopX2!.Value), 0, w);
            for (int y = 0; y < h; y++)
                for (int x = lx1; x < lx2; x++)
                {
                    int o = (y * w + x) * 4;
                    // Blend amber (FF C1 07) at ~70/255 over bg #1E1E28.
                    px[o] = Blend(px[o], 0x07, 70);
                    px[o + 1] = Blend(px[o + 1], 0xC1, 70);
                    px[o + 2] = Blend(px[o + 2], 0xFF, 70);
                }
        }

        // Bars.
        for (int i = 0; i < n; i++)
        {
            double x0d = (double)i / n * w;
            double x1d = (double)(i + 1) / n * w;
            int x0 = (int)x0d;
            int x1 = Math.Max(x0 + 1, (int)Math.Ceiling(x1d - 0.5));
            if (x1 > w) x1 = w;
            double amp = Math.Clamp(peaks[i], 0, 1) * (mid - 4);
            if (amp < 1) amp = 1;
            int y0 = Math.Clamp((int)(mid - amp), 0, h - 1);
            int y1 = Math.Clamp((int)(mid + amp), 0, h - 1);
            bool played = x0d <= progressX;
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

        // Loop edge markers #FFC107, 2px.
        if (hasLoop)
        {
            DrawVLine(px, w, h, (int)loopX1!.Value, 0x07, 0xC1, 0xFF);
            DrawVLine(px, w, h, (int)loopX2!.Value, 0x07, 0xC1, 0xFF);
        }

        // Playhead white, 2px.
        DrawVLine(px, w, h, (int)progressX, 255, 255, 255);

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
