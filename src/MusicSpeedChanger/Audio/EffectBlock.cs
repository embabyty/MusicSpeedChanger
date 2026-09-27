using System;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// One block in the effects chain (Equalizer APO style).
/// Kind is "eq" (graphic EQ, 15 or 31 bands) or "preamp" (gain in dB).
/// Plain data: cloned across the engine/settings/UI boundary.
/// </summary>
public sealed class EffectBlock
{
    public string Kind { get; set; } = "eq";
    public bool Enabled { get; set; } = true;

    /// <summary>EQ only: 15 or 31.</summary>
    public int Bands { get; set; } = GraphicEqualizer.BandCount;

    /// <summary>EQ only: per-band gains in dB, length == <see cref="Bands"/>.</summary>
    public float[] Gains { get; set; } = new float[GraphicEqualizer.BandCount];

    /// <summary>EQ only: preset name ("Flat", "Rock", …) or "" when hand-tweaked.</summary>
    public string Preset { get; set; } = "Flat";

    /// <summary>Preamp only: gain in dB (-20..+20).</summary>
    public float PreampDb { get; set; } = 0f;

    public static EffectBlock NewEq(int bands = GraphicEqualizer.BandCount)
    {
        bands = bands == GraphicEqualizer.BandCount15 ? bands : GraphicEqualizer.BandCount;
        return new EffectBlock
        {
            Kind = "eq",
            Enabled = true,
            Bands = bands,
            Gains = new float[bands],
            Preset = "Flat",
        };
    }

    public static EffectBlock NewPreamp(float gainDb = 0f) => new()
    {
        Kind = "preamp",
        Enabled = true,
        PreampDb = Math.Clamp(gainDb, -20f, 20f),
    };

    public bool IsPreamp => string.Equals(Kind, "preamp", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the block audibly does something.</summary>
    public bool IsActiveContent
    {
        get
        {
            if (!Enabled) return false;
            if (IsPreamp) return Math.Abs(PreampDb) > 0.001f;
            if (Gains == null || Gains.Length != Bands) return false;
            for (int i = 0; i < Gains.Length; i++)
                if (Math.Abs(Gains[i]) > 0.001f) return true;
            return false;
        }
    }

    public EffectBlock Clone() => new()
    {
        Kind = Kind,
        Enabled = Enabled,
        Bands = Bands,
        Gains = Gains == null ? Array.Empty<float>() : (float[])Gains.Clone(),
        Preset = Preset,
        PreampDb = PreampDb,
    };
}
