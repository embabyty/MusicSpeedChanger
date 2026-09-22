using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// <see cref="ISampleProvider"/> that runs audio through a <see cref="GraphicEqualizer"/>.
/// Sits after the SoundTouch stage: source → SoundTouch → EQ → volume → output.
/// </summary>
public sealed class EqualizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public GraphicEqualizer Equalizer { get; }

    /// <summary>When false, samples pass through untouched.</summary>
    public bool Enabled { get; set; } = true;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source;
        Equalizer = new GraphicEqualizer(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0 && Enabled)
            Equalizer.Process(buffer, offset, read);
        return read;
    }
}
