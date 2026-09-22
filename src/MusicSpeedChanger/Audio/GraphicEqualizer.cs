using System;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// 31-band ISO 1/3-octave graphic equalizer (20 Hz – 20 kHz).
/// Pure DSP: cascaded RBJ peaking biquads, one chain per channel.
/// Thread-safe for one reader + one writer via lock; same pattern as the rest of the engine.
/// </summary>
public sealed class GraphicEqualizer
{
    public const int BandCount = 31;

    /// <summary>ISO 1/3-octave center frequencies, Hz.</summary>
    public static readonly float[] CenterFrequencies =
    {
        20, 25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500,
        630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
        10000, 12500, 16000, 20000
    };

    /// <summary>Short axis labels for the UI.</summary>
    public static readonly string[] ShortLabels =
    {
        "20", "25", "31", "40", "50", "63", "80", "100", "125", "160", "200",
        "250", "315", "400", "500", "630", "800", "1k", "1.25k", "1.6k", "2k",
        "2.5k", "3.15k", "4k", "5k", "6.3k", "8k", "10k", "12.5k", "16k", "20k"
    };

    public const float MinGainDb = -15f;
    public const float MaxGainDb = +15f;

    /// <summary>Q for 1/3-octave proportional filters.</summary>
    private const double Q = 4.318;

    private readonly object _lock = new();
    private readonly float[] _gains = new float[BandCount];
    private readonly bool[] _bandActive = new bool[BandCount];
    private Biquad[][] _chains = Array.Empty<Biquad[]>();

    private int _sampleRate;
    private int _channels;

    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    public GraphicEqualizer(int sampleRate, int channels)
    {
        Configure(sampleRate, channels);
    }

    public void Configure(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        lock (_lock)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _chains = new Biquad[channels][];
            for (int c = 0; c < channels; c++)
                _chains[c] = new Biquad[BandCount];
            RecomputeAll();
        }
    }

    public float GetGain(int band)
    {
        CheckBand(band);
        lock (_lock) return _gains[band];
    }

    public void SetGain(int band, float gainDb)
    {
        CheckBand(band);
        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        lock (_lock)
        {
            _gains[band] = gainDb;
            UpdateBand(band);
        }
    }

    public float[] GetGains()
    {
        lock (_lock)
        {
            var copy = new float[BandCount];
            Array.Copy(_gains, copy, BandCount);
            return copy;
        }
    }

    public void SetGains(float[] gains)
    {
        if (gains == null) throw new ArgumentNullException(nameof(gains));
        if (gains.Length != BandCount) throw new ArgumentException($"Need {BandCount} gains.", nameof(gains));
        lock (_lock)
        {
            for (int i = 0; i < BandCount; i++)
                _gains[i] = Math.Clamp(gains[i], MinGainDb, MaxGainDb);
            RecomputeAll();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_gains, 0, BandCount);
            RecomputeAll();
        }
    }

    /// <summary>True when every band is at (near) 0 dB — processing is a no-op.</summary>
    public bool IsFlat
    {
        get
        {
            lock (_lock)
            {
                for (int i = 0; i < BandCount; i++)
                    if (Math.Abs(_gains[i]) > 0.001f) return false;
                return true;
            }
        }
    }

    /// <summary>Process interleaved float samples in place.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        lock (_lock)
        {
            if (_chains.Length == 0) return;
            bool anyActive = false;
            for (int i = 0; i < BandCount; i++)
                if (_bandActive[i]) { anyActive = true; break; }
            if (!anyActive) return;

            int channels = _channels;
            int frames = count / channels;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float s = buffer[offset + f * channels + c];
                    var chain = _chains[c];
                    for (int b = 0; b < BandCount; b++)
                    {
                        if (_bandActive[b])
                            s = chain[b].Step(s);
                    }
                    buffer[offset + f * channels + c] = s;
                }
            }
        }
    }

    private void RecomputeAll()
    {
        for (int b = 0; b < BandCount; b++)
            UpdateBand(b);
    }

    private void UpdateBand(int band)
    {
        float gain = _gains[band];
        float freq = CenterFrequencies[band];
        // Bands at/above Nyquist can't be represented — leave them pass-through.
        bool usable = Math.Abs(gain) > 0.001f && freq < _sampleRate * 0.45f;
        _bandActive[band] = usable;
        for (int c = 0; c < _channels; c++)
        {
            if (usable)
                _chains[c][band].SetPeaking(_sampleRate, freq, Q, gain);
            else
                _chains[c][band].Reset();
        }
    }

    private static void CheckBand(int band)
    {
        if (band < 0 || band >= BandCount)
            throw new ArgumentOutOfRangeException(nameof(band), $"Band must be 0..{BandCount - 1}.");
    }

    /// <summary>RBJ peaking-EQ biquad (Direct Form I).</summary>
    private struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void SetPeaking(int sampleRate, float freq, double q, float gainDb)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sampleRate;
            double cosW = Math.Cos(w0);
            double sinW = Math.Sin(w0);
            double alpha = sinW / (2.0 * q);

            double b0 = 1.0 + alpha * a;
            double b1 = -2.0 * cosW;
            double b2 = 1.0 - alpha * a;
            double a0 = 1.0 + alpha / a;
            double a1 = -2.0 * cosW;
            double a2 = 1.0 - alpha / a;

            _b0 = (float)(b0 / a0);
            _b1 = (float)(b1 / a0);
            _b2 = (float)(b2 / a0);
            _a1 = (float)(a1 / a0);
            _a2 = (float)(a2 / a0);
            _x1 = _x2 = _y1 = _y2 = 0f;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0f;

        public float Step(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
