using System;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Transparent peak limiter, last stage before output:
/// source → SoundTouch → EQ → <b>limiter</b> → volume → output.
/// Time-stretch overlap-add and EQ boosts can push peaks past 0 dBFS, which the
/// DAC turns into harsh clipping (most audible as muddy/distorted bass, worse
/// when pitched). This stage keeps peaks at ceiling with a fast-attack /
/// slow-release envelope so normal-level audio passes through bit-identical.
/// Also reused by the WAV exporter via <see cref="Process"/>.
/// </summary>
public sealed class LimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider? _source;
    private readonly WaveFormat _format;

    /// <summary>Ceiling in linear amplitude (0.98 ≈ −0.2 dBFS).</summary>
    public float Ceiling { get; set; } = 0.98f;

    private double _envelope;

    public WaveFormat WaveFormat => _source?.WaveFormat ?? _format;

    public LimiterSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _format = source.WaveFormat;
    }

    /// <summary>Buffer-only use (WAV export): no source stream attached.</summary>
    public LimiterSampleProvider(WaveFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_source == null) throw new InvalidOperationException("No source stream attached.");
        int read = _source.Read(buffer, offset, count);
        if (read > 0) Process(buffer, offset, read);
        return read;
    }

    /// <summary>Applies gain-reduction envelope + safety clamp in place.</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        double sampleRate = Math.Max(8000, WaveFormat.SampleRate);
        // ~0.5 ms attack, ~60 ms release — fast enough to catch overshoot
        // transients, slow enough to avoid bass pumping.
        double attack = Math.Exp(-1.0 / (sampleRate * 0.0005));
        double release = Math.Exp(-1.0 / (sampleRate * 0.060));
        double ceiling = Math.Clamp(Ceiling, 0.1, 1.0);
        double env = _envelope;

        for (int i = 0; i < count; i++)
        {
            double peak = Math.Abs(buffer[offset + i]);
            env = peak > env
                ? attack * env + (1.0 - attack) * peak
                : release * env + (1.0 - release) * peak;

            double y = buffer[offset + i] * (env > ceiling ? ceiling / env : 1.0);

            // Absolute safety net: hard-clamp any residual over (only reachable
            // on extreme inter-sample jumps the envelope hasn't caught yet).
            if (y > 1.0) y = 1.0;
            else if (y < -1.0) y = -1.0;
            buffer[offset + i] = (float)y;
        }

        _envelope = env;
    }
}
