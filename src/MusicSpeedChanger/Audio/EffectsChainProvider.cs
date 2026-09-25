using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// <see cref="ISampleProvider"/> that runs audio through an ordered chain of
/// effect blocks (graphic EQs + preamp gains), Equalizer APO style.
/// Sits after the SoundTouch stage: source → SoundTouch → effects → limiter → volume → output.
/// Snapshots block settings; call <see cref="Rebuild"/> after structural edits and
/// the Set* methods for live tweaks.
/// </summary>
public sealed class EffectsChainProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _lock = new();
    private List<Stage> _stages = new();

    private sealed class Stage
    {
        public GraphicEqualizer? Eq;
        public int BlockIndex;
        public bool Enabled;
        public bool IsPreamp;
        public float PreampLinear = 1f;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EffectsChainProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Rebuild stages from a snapshot (block order preserved).</summary>
    public void Rebuild(IReadOnlyList<EffectBlock> effects)
    {
        var fmt = _source.WaveFormat;
        var stages = new List<Stage>();
        for (int i = 0; i < (effects?.Count ?? 0); i++)
        {
            var b = effects![i];
            if (b == null) continue;
            if (b.IsPreamp)
            {
                stages.Add(new Stage
                {
                    BlockIndex = i,
                    Enabled = b.Enabled,
                    IsPreamp = true,
                    PreampLinear = DbToLinear(b.PreampDb),
                });
            }
            else
            {
                int bands = b.Bands == GraphicEqualizer.BandCount15
                    ? GraphicEqualizer.BandCount15 : GraphicEqualizer.BandCount;
                var eq = new GraphicEqualizer(fmt.SampleRate, fmt.Channels,
                    GraphicEqualizer.CentersForBands(bands), GraphicEqualizer.QForBands(bands));
                if (b.Gains != null && b.Gains.Length == bands)
                    eq.SetGains(b.Gains);
                stages.Add(new Stage { Eq = eq, BlockIndex = i, Enabled = b.Enabled });
            }
        }
        lock (_lock) _stages = stages;
    }

    /// <summary>Live single-band tweak (no rebuild).</summary>
    public void SetBlockGain(int blockIndex, int band, float gainDb)
    {
        lock (_lock)
        {
            foreach (var s in _stages)
            {
                if (s.BlockIndex == blockIndex && s.Eq != null)
                {
                    if (band >= 0 && band < s.Eq.Bands)
                        s.Eq.SetGain(band, gainDb);
                    return;
                }
            }
        }
    }

    public void SetPreampDb(int blockIndex, float gainDb)
    {
        float k = DbToLinear(gainDb);
        lock (_lock)
        {
            foreach (var s in _stages)
            {
                if (s.BlockIndex == blockIndex && s.IsPreamp)
                {
                    s.PreampLinear = k;
                    return;
                }
            }
        }
    }

    public void SetBlockEnabled(int blockIndex, bool enabled)
    {
        lock (_lock)
        {
            foreach (var s in _stages)
                if (s.BlockIndex == blockIndex) s.Enabled = enabled;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
            ProcessBuffer(buffer, offset, read);
        return read;
    }

    /// <summary>Buffer-only use (WAV export): apply enabled stages in order.</summary>
    public void ProcessBuffer(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            foreach (var s in _stages)
            {
                if (!s.Enabled) continue;
                if (s.Eq != null)
                    s.Eq.Process(buffer, offset, count);
                else if (s.IsPreamp && Math.Abs(s.PreampLinear - 1f) > 0.0001f)
                {
                    float k = s.PreampLinear;
                    for (int i = offset, n = offset + count; i < n; i++)
                        buffer[i] *= k;
                }
            }
        }
    }

    public static float DbToLinear(float db) =>
        (float)Math.Pow(10.0, Math.Clamp(db, -20f, 20f) / 20.0);
}
