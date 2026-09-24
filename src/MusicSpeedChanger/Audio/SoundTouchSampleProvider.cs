using System;
using NAudio.Wave;
using SoundTouch;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Streams audio through SoundTouch for independent tempo/pitch control.
/// Wraps an <see cref="ISampleProvider"/> source (32-bit float) and exposes processed output.
/// </summary>
public sealed class SoundTouchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _st;
    private readonly object _lock = new();
    private readonly float[] _sourceBuffer = new float[8192 * 4];
    private bool _sourceExhausted;
    private bool _flushed;

    private double _tempo = 1.0;      // 1.0 = original speed
    private double _pitchSemitones;   // -12..+12

    // Slow-tempo stretch profile (see UpdateStretchProfile). Library defaults
    // are captured at construction so normal speed is bit-identical to before.
    private bool _slowMode;
    private int _defaultSequenceMs;
    private int _defaultSeekMs;
    private int _defaultOverlapMs;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double Tempo
    {
        get => _tempo;
        set
        {
            value = Math.Clamp(value, 0.25, 3.0);
            lock (_lock)
            {
                _tempo = value;
                _st.Tempo = value;
                UpdateStretchProfile(value);
            }
        }
    }

    public double PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            value = Math.Clamp(value, -12.0, 12.0);
            lock (_lock)
            {
                _pitchSemitones = value;
                _st.PitchSemiTones = value;
            }
        }
    }

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Source must be 32-bit float.", nameof(source));

        _st = new SoundTouchProcessor
        {
            Channels = source.WaveFormat.Channels,
            SampleRate = source.WaveFormat.SampleRate,
            Tempo = _tempo,
            PitchSemiTones = _pitchSemitones
        };
        ConfigureQuality(_st);
        _defaultSequenceMs = _st.GetSetting(SettingId.SequenceDurationMs);
        _defaultSeekMs = _st.GetSetting(SettingId.SeekWindowDurationMs);
        _defaultOverlapMs = _st.GetSetting(SettingId.OverlapDurationMs);
    }

    /// <summary>
    /// Quality settings shared by realtime playback and file export. The
    /// anti-alias filter is what keeps pitched bass clean: without a long
    /// enough AA kernel the rate transposer folds bass harmonics back into
    /// the low end as inharmonic mud, and quickseek trades accuracy for CPU.
    /// </summary>
    internal static void ConfigureQuality(SoundTouchProcessor st)
    {
        st.SetSetting(SettingId.UseAntiAliasFilter, 1);
        st.SetSetting(SettingId.AntiAliasFilterLength, 64);
        st.SetSetting(SettingId.UseQuickSeek, 0);
    }

    /// <summary>
    /// Long-window stretch profile for slow playback. At low tempo the WSOLA
    /// stretcher repeats short windows, and a short overlap crossfade plus a
    /// narrow seek window turn into warble/phasiness ("underwater" distortion)
    /// with energy buildup. Longer windows smooth the repeats out.
    /// Hysteresis (65% in, 75% out) stops the profile flapping when parked
    /// near the boundary; buffers are cleared on switch because frames
    /// analyzed under the old window geometry no longer line up.
    /// </summary>
    internal static void ConfigureSlowStretch(SoundTouchProcessor st)
    {
        st.SetSetting(SettingId.SequenceDurationMs, 120);
        st.SetSetting(SettingId.SeekWindowDurationMs, 40);
        st.SetSetting(SettingId.OverlapDurationMs, 24);
    }

    private void UpdateStretchProfile(double tempo)
    {
        bool slow = _slowMode ? tempo < 0.75 : tempo < 0.65;
        if (slow == _slowMode) return;
        _slowMode = slow;
        if (slow)
        {
            ConfigureSlowStretch(_st);
        }
        else
        {
            _st.SetSetting(SettingId.SequenceDurationMs, _defaultSequenceMs);
            _st.SetSetting(SettingId.SeekWindowDurationMs, _defaultSeekMs);
            _st.SetSetting(SettingId.OverlapDurationMs, _defaultOverlapMs);
        }
        _st.Clear();
    }

    /// <summary>Call after seeking the source to drop buffered audio.</summary>
    public void ClearBuffer()
    {
        lock (_lock)
        {
            _st.Clear();
            _sourceExhausted = false;
            _flushed = false;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int samplesWritten = 0;
            int channels = WaveFormat.Channels;
            Span<float> outSpan = new Span<float>(buffer, offset, count);

            while (samplesWritten < count)
            {
                var slice = outSpan.Slice(samplesWritten);
                int received = _st.ReceiveSamples(slice, (count - samplesWritten) / channels);
                if (received > 0)
                {
                    samplesWritten += received * channels;
                    continue;
                }

                if (_sourceExhausted)
                {
                    if (!_flushed)
                    {
                        _st.Flush();
                        _flushed = true;
                        continue; // drain remaining
                    }
                    break; // EOF
                }

                int read = _source.Read(_sourceBuffer, 0, _sourceBuffer.Length);
                if (read == 0)
                {
                    _sourceExhausted = true;
                    continue;
                }

                _st.PutSamples(new ReadOnlySpan<float>(_sourceBuffer, 0, read), read / channels);
            }

            return samplesWritten;
        }
    }
}
