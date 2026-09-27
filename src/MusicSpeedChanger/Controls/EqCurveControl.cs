using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MusicSpeedChanger.Controls;

/// <summary>
/// Combined EQ chain frequency-response graph (Equalizer APO style curve).
/// Log axis 20 Hz – 20 kHz, dB range auto-scales. Rendered into a WriteableBitmap.
/// </summary>
public sealed class EqCurveControl : Grid
{
    public sealed class CurveBlock
    {
        public float[] Freqs { get; set; } = Array.Empty<float>();
        public float[] Gains { get; set; } = Array.Empty<float>();
        public double Q { get; set; } = 4.318;
        public bool Enabled { get; set; } = true;
        public bool IsPreamp { get; set; }
        public float PreampDb { get; set; }
    }

    private const int Points = 140;
    private const float FMin = 20f;
    private const float FMax = 20000f;

    private readonly Image _image;
    private WriteableBitmap? _bitmap;
    private IReadOnlyList<CurveBlock>? _blocks;
    private int _sampleRate = 48000;

    public EqCurveControl()
    {
        MinHeight = 120;
        _image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
        Children.Add(_image);
        SizeChanged += (_, _) => Render();
    }

    public void Update(IReadOnlyList<CurveBlock> blocks, int sampleRate)
    {
        _blocks = blocks;
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        Render();
    }

    private void Render()
    {
        int w = Math.Max(1, (int)Math.Round(ActualWidth));
        int h = Math.Max(1, (int)Math.Round(ActualHeight));
        if (w < 10 || h < 10) return;
        const int maxW = 1400;
        if (w > maxW) w = maxW;

        if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
        {
            _bitmap = new WriteableBitmap(w, h);
            _image.Source = _bitmap;
        }

        // Combined response in dB.
        double[] db = new double[Points];
        if (_blocks != null)
        {
            foreach (var b in _blocks)
            {
                if (!b.Enabled) continue;
                if (b.IsPreamp)
                {
                    for (int i = 0; i < Points; i++) db[i] += b.PreampDb;
                    continue;
                }
                if (b.Freqs == null || b.Gains == null) continue;
                int n = Math.Min(b.Freqs.Length, b.Gains.Length);
                for (int k = 0; k < n; k++)
                {
                    float gain = b.Gains[k];
                    float f0 = b.Freqs[k];
                    if (Math.Abs(gain) <= 0.001f || f0 >= _sampleRate * 0.45f) continue;
                    AddPeaking(db, f0, gain, b.Q);
                }
            }
        }

        double maxAbs = 6;
        for (int i = 0; i < Points; i++)
            maxAbs = Math.Max(maxAbs, Math.Abs(db[i]));
        maxAbs = Math.Min(24, maxAbs);
        double mid = h / 2.0;
        double yScale = (h / 2.0 - 6) / maxAbs;

        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0x28; px[i + 1] = 0x1E; px[i + 2] = 0x1E; px[i + 3] = 255;
        }

        // Vertical gridlines at 50/100/200/500/1k/2k/5k/10k.
        foreach (float f in new float[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            int x = (int)(Math.Log(f / FMin) / Math.Log(FMax / FMin) * w);
            VLine(px, w, h, x, 0x3A, 0x3A, 0x4A);
        }
        // Horizontal gridlines at ±6/±12/±18 when in range + solid 0 dB line.
        foreach (double g in new double[] { -18, -12, -6, 6, 12, 18 })
        {
            if (Math.Abs(g) >= maxAbs) continue;
            int y = (int)(mid - g * yScale);
            HLine(px, w, h, y, 0x3A, 0x3A, 0x4A);
        }
        HLine(px, w, h, (int)mid, 0x55, 0x55, 0x66);

        // Fill under curve (translucent accent) then the curve itself.
        int prevY = -1;
        for (int i = 0; i < Points; i++)
        {
            int x0 = (int)((double)i / (Points - 1) * (w - 1));
            int x1 = (int)((double)(i + 1) / (Points - 1) * (w - 1));
            if (x1 <= x0) x1 = x0 + 1;
            int y = (int)Math.Clamp(mid - db[i] * yScale, 0, h - 1);
            if (prevY >= 0)
            {
                // Simple segment fill between this point and mid.
                for (int x = x0; x <= Math.Min(x1, w - 1); x++)
                {
                    int ya = Math.Min(y, (int)mid), yb = Math.Max(y, (int)mid);
                    for (int yy = ya; yy <= yb; yy++)
                    {
                        int o = (yy * w + x) * 4;
                        px[o] = Blend(px[o], 0xFF, 28);
                        px[o + 1] = Blend(px[o + 1], 0x9E, 28);
                        px[o + 2] = Blend(px[o + 2], 0x7C, 28);
                    }
                }
            }
            prevY = y;
        }
        for (int i = 0; i < Points; i++)
        {
            int x = (int)((double)i / (Points - 1) * (w - 1));
            int y = (int)Math.Clamp(mid - db[i] * yScale, 0, h - 1);
            for (int dx = 0; dx < 2 && x + dx < w; dx++)
                for (int dy = -1; dy <= 0; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    int o = (yy * w + x + dx) * 4;
                    px[o] = 0xFF; px[o + 1] = 0x9E; px[o + 2] = 0x7C; px[o + 3] = 255;
                }
        }

        using var s = _bitmap.PixelBuffer.AsStream();
        s.Seek(0, SeekOrigin.Begin);
        s.Write(px, 0, px.Length);
        s.Flush();
        _bitmap.Invalidate();
    }

    private void AddPeaking(double[] db, float f0, float gainDb, double q)
    {
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * f0 / _sampleRate;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * q);
        double b0 = 1.0 + alpha * a, b1 = -2.0 * cosW0, b2 = 1.0 - alpha * a;
        double a0 = 1.0 + alpha / a, a1 = -2.0 * cosW0, a2 = 1.0 - alpha / a;
        for (int i = 0; i < Points; i++)
        {
            double f = FMin * Math.Pow(FMax / FMin, (double)i / (Points - 1));
            double w = 2.0 * Math.PI * f / _sampleRate;
            double cosW = Math.Cos(w), sinW = Math.Sin(w);
            double cos2 = cosW * cosW - sinW * sinW, sin2 = 2 * cosW * sinW;
            double nRe = b0 + b1 * cosW + b2 * cos2;
            double nIm = -(b1 * sinW + b2 * sin2);
            double dRe = a0 + a1 * cosW + a2 * cos2;
            double dIm = -(a1 * sinW + a2 * sin2);
            double mag2 = (nRe * nRe + nIm * nIm) / Math.Max(1e-12, dRe * dRe + dIm * dIm);
            db[i] += 10.0 * Math.Log10(Math.Max(1e-9, mag2));
        }
    }

    private static void VLine(byte[] px, int w, int h, int x, byte b, byte g, byte r)
    {
        if (x < 0 || x >= w) return;
        for (int y = 0; y < h; y++)
        {
            int o = (y * w + x) * 4;
            px[o] = b; px[o + 1] = g; px[o + 2] = r;
        }
    }

    private static void HLine(byte[] px, int w, int h, int y, byte b, byte g, byte r)
    {
        if (y < 0 || y >= h) return;
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = b; px[o + 1] = g; px[o + 2] = r;
        }
    }

    private static byte Blend(byte dst, byte src, int alpha) =>
        (byte)((src * alpha + dst * (255 - alpha)) / 255);
}
