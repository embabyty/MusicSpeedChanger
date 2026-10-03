using System;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Realtime filter and gain provider for DJ-style transitions (AutoMix).
/// Sits between limiter and output to apply dynamic equal-power gain,
/// bass swapping (low-end high-pass cut), and high-pass sweeps (rise).
/// </summary>
public sealed class AutoMixFilterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;

    private float _mixGain = 1f;
    private float _highPassCutoff = 20f;

    // Biquad filter state per channel: x1, x2, y1, y2
    private float[]? _x1;
    private float[]? _x2;
    private float[]? _y1;
    private float[]? _y2;

    // Filter coefficients
    private float _b0 = 1f, _b1 = 0f, _b2 = 0f;
    private float _a1 = 0f, _a2 = 0f;
    private bool _filterActive = false;
    private readonly object _lock = new();

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float MixGain
    {
        get => _mixGain;
        set => _mixGain = Math.Clamp(value, 0f, 1f);
    }

    public float HighPassCutoff
    {
        get => _highPassCutoff;
        set
        {
            float clamped = Math.Clamp(value, 20f, _sampleRate * 0.45f);
            if (Math.Abs(clamped - _highPassCutoff) > 0.5f)
            {
                _highPassCutoff = clamped;
                UpdateCoefficients();
            }
        }
    }

    public AutoMixFilterSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = Math.Max(8000, source.WaveFormat.SampleRate);
        _x1 = new float[_channels];
        _x2 = new float[_channels];
        _y1 = new float[_channels];
        _y2 = new float[_channels];
        UpdateCoefficients();
    }

    private void UpdateCoefficients()
    {
        lock (_lock)
        {
            if (_highPassCutoff <= 25f)
            {
                _filterActive = false;
                return;
            }

            _filterActive = true;
            // RBJ 2nd-order Butterworth High-Pass Filter (Q = 0.7071)
            double omega = 2.0 * Math.PI * _highPassCutoff / _sampleRate;
            double cos = Math.Cos(omega);
            double sin = Math.Sin(omega);
            double alpha = sin / (2.0 * 0.70710678);

            double b0 = (1.0 + cos) / 2.0;
            double b1 = -(1.0 + cos);
            double b2 = (1.0 + cos) / 2.0;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cos;
            double a2 = 1.0 - alpha;

            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
        }
    }

    public void ResetFilter()
    {
        lock (_lock)
        {
            if (_x1 != null) Array.Clear(_x1, 0, _x1.Length);
            if (_x2 != null) Array.Clear(_x2, 0, _x2.Length);
            if (_y1 != null) Array.Clear(_y1, 0, _y1.Length);
            if (_y2 != null) Array.Clear(_y2, 0, _y2.Length);
            _highPassCutoff = 20f;
            _mixGain = 1f;
            _filterActive = false;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        float gain = _mixGain;
        bool active = _filterActive;

        if (!active && gain >= 0.999f)
        {
            return read; // Transparent pass-through
        }

        lock (_lock)
        {
            if (!_filterActive)
            {
                // Pure gain scaling
                for (int i = 0; i < read; i++)
                {
                    buffer[offset + i] *= gain;
                }
                return read;
            }

            float b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
            int channels = _channels;
            var x1 = _x1!;
            var x2 = _x2!;
            var y1 = _y1!;
            var y2 = _y2!;

            for (int i = 0; i < read; i++)
            {
                int ch = (i % channels);
                float x = buffer[offset + i];
                float y = b0 * x + b1 * x1[ch] + b2 * x2[ch] - a1 * y1[ch] - a2 * y2[ch];

                // Anti-denormal clamp
                if (Math.Abs(y) < 1e-15f) y = 0f;

                x2[ch] = x1[ch];
                x1[ch] = x;
                y2[ch] = y1[ch];
                y1[ch] = y;

                buffer[offset + i] = y * gain;
            }
        }

        return read;
    }
}

