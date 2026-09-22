using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicSpeedChanger.Audio;

namespace MusicSpeedChanger.Controls;

/// <summary>Clickable waveform with playhead + AB loop region.</summary>
public sealed class WaveformControl : FrameworkElement
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x28));
    private static readonly Brush Wave = new SolidColorBrush(Color.FromRgb(0x7C, 0x9E, 0xFF));
    private static readonly Brush Played = new SolidColorBrush(Color.FromRgb(0x3A, 0x5F, 0xC9));
    private static readonly Brush LoopBrush = new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xC1, 0x07));
    private static readonly Pen PlayheadPen = new Pen(new SolidColorBrush(Colors.White), 1.5);
    private static readonly Pen MarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), 1.5);

    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LoopAProperty =
        DependencyProperty.Register(nameof(LoopA), typeof(double?), typeof(WaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LoopBProperty =
        DependencyProperty.Register(nameof(LoopB), typeof(double?), typeof(WaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

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
        set => SetValueLoop(LoopBProperty, value);
    }

    private void SetValueLoop(DependencyProperty dp, double? value) => SetValue(dp, value);

    public WaveformData? Data
    {
        get => _data;
        set { _data = value; InvalidateVisual(); }
    }
    private WaveformData? _data;

    public event EventHandler<double>? SeekRequested; // 0..1

    public WaveformControl()
    {
        MinHeight = 120;
        Cursor = Cursors.Hand;
        ToolTip = "Click to seek";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth <= 0) return;
        var p = e.GetPosition(this);
        SeekRequested?.Invoke(this, Math.Clamp(p.X / ActualWidth, 0, 1));
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured && ActualWidth > 0)
        {
            var p = e.GetPosition(this);
            SeekRequested?.Invoke(this, Math.Clamp(p.X / ActualWidth, 0, 1));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (w <= 0 || h <= 0) return;

        // Loop region
        if (LoopA.HasValue && LoopB.HasValue && LoopB > LoopA)
        {
            double x1 = LoopA.Value * w, x2 = LoopB.Value * w;
            dc.DrawRectangle(LoopBrush, null, new Rect(x1, 0, Math.Max(1, x2 - x1), h));
            dc.DrawLine(MarkerPen, new Point(x1, 0), new Point(x1, h));
            dc.DrawLine(MarkerPen, new Point(x2, 0), new Point(x2, h));
        }

        if (_data == null || _data.Peaks.Length == 0)
        {
            var hint = new FormattedText("Open an audio file to begin",
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }

        var peaks = _data.Peaks;
        int n = peaks.Length;
        double colW = w / n;
        double mid = h / 2;
        double progressX = Math.Clamp(Progress, 0, 1) * w;

        for (int i = 0; i < n; i++)
        {
            double x = i * colW;
            double amp = Math.Clamp(peaks[i], 0, 1) * (mid - 4);
            if (amp < 1) amp = 1;
            var brush = x <= progressX ? Played : Wave;
            dc.DrawRectangle(brush, null, new Rect(x, mid - amp, Math.Max(1, colW - 0.5), amp * 2));
        }

        dc.DrawLine(PlayheadPen, new Point(progressX, 0), new Point(progressX, h));
    }
}
