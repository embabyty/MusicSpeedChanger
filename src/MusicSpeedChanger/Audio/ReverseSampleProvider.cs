using System;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Serves a fully-decoded float buffer back-to-front (multi-channel frames stay intact,
/// frame order is reversed). Position 0 = start of the song, TotalFrames = end of the song.
/// </summary>
public sealed class ReverseSampleProvider : ISampleProvider
{
    private readonly float[] _data;
    private readonly int _channels;
    private long _framePos; // next unread frame index (exclusive upper bound)

    public WaveFormat WaveFormat { get; }
    public long TotalFrames { get; }

    /// <summary>Current read position in frames. TotalFrames = end of song, 0 = fully played.</summary>
    public long PositionFrames
    {
        get => _framePos;
        set => _framePos = Math.Clamp(value, 0, TotalFrames);
    }

    public bool Exhausted => _framePos <= 0;

    public ReverseSampleProvider(float[] data, WaveFormat format)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        if (format.Encoding != WaveFormatEncoding.IeeeFloat)
            throw new ArgumentException("Data must be 32-bit float.", nameof(format));
        _channels = format.Channels;
        if (_channels <= 0) throw new ArgumentException("Channel count must be positive.", nameof(format));
        if (data.Length % _channels != 0)
            throw new ArgumentException("Data length must be a whole number of frames.", nameof(data));

        WaveFormat = format;
        TotalFrames = data.Length / _channels;
        _framePos = TotalFrames;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        int frames = count / _channels;
        long n = Math.Min(frames, _framePos);
        for (long f = 0; f < n; f++)
        {
            long srcFrame = _framePos - 1 - f;
            long src = srcFrame * _channels;
            int dst = offset + (int)f * _channels;
            for (int c = 0; c < _channels; c++)
                buffer[dst + c] = _data[src + c];
        }
        _framePos -= n;
        return (int)n * _channels;
    }
}
