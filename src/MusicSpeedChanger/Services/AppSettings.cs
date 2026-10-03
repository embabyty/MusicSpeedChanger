using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicSpeedChanger.Services;

/// <summary>
/// Persisted user settings + last-used effect state, stored as JSON in
/// %LocalAppData%\MusicSpeedChanger\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultFeedUrl =
        "https://api.github.com/repos/embabyty/MusicSpeedChanger/releases/latest";

    // ----- Updates -----
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateFeedUrl { get; set; } = DefaultFeedUrl;
    /// <summary>When true, the updater also looks at beta/pre-release builds.</summary>
    public bool IncludeBetaUpdates { get; set; } = false;
    /// <summary>Insider Hub channel for Patreon supporters: "Beta" or "Canary" (weekly).</summary>
    public string InsiderChannel { get; set; } = "Beta";
    /// <summary>True after a verified Patreon login (active membership).</summary>
    public bool BetaAccessUnlocked { get; set; } = false;
    /// <summary>Patreon OAuth refresh token (plaintext, like a session cookie).</summary>
    public string? PatreonRefreshToken { get; set; }
    public string? PatreonFullName { get; set; }

    // ----- Speed & pitch -----
    public double DefaultTempoPercent { get; set; } = 100;
    public double DefaultPitchSemitones { get; set; } = 0;
    public bool ApplyDefaultsOnFileLoad { get; set; } = true;
    public double TempoSliderStep { get; set; } = 1;
    public double PitchSliderStep { get; set; } = 0.1;

    // ----- Editor controls -----
    public bool ShowTempoPanel { get; set; } = true;
    public bool ShowPitchPanel { get; set; } = true;
    public bool ShowLoopPanel { get; set; } = true;
    public bool ShowEqPanel { get; set; } = true;
    public int WaveformPeaks { get; set; } = 1400;
    public bool ClickToSeek { get; set; } = true;

    // ----- Effects -----
    /// <summary>When true, tempo/pitch/volume/EQ are restored on startup and kept across files.</summary>
    public bool RememberEffects { get; set; } = true;

    // ----- Queue -----
    public bool RememberFileList { get; set; } = true;
    public List<string> FileListPaths { get; set; } = new();
    public string? LastFilePath { get; set; }

    // ----- Playback modes -----
    public bool ShuffleEnabled { get; set; } = false;
    /// <summary>0 = Off, 1 = Repeat All, 2 = Repeat One.</summary>
    public int RepeatMode { get; set; } = 0;

    // ----- Layout (Cider-style) -----
    /// <summary>Sidebar dock: "Mojave" (left 230), "Mavericks" (right 230),
    /// "Calico" (left 340), "Montara" (right 340).</summary>
    public string LayoutType { get; set; } = "Mojave";
    /// <summary>Transport density: "Comfy" (full two-row), "Compact" (slim),
    /// or "CompactInline" (seek merged into one row).</summary>
    public string PlayerType { get; set; } = "Comfy";

    // ----- AutoMix (Spotify-style transitions) -----
    /// <summary>
    /// DJ-style beat-synced crossfade into the next queue track.
    /// </summary>
    public bool AutoMixEnabled { get; set; } = false;
    /// <summary>Crossfade length in seconds (1…12). Effective delay before track end.</summary>
    public double AutoMixSeconds { get; set; } = 5;
    /// <summary>Transition style: "BassSwap" (DJ EQ Swap), "Blend" (Equal Power), "Rise" (High-pass sweep), "BeatDrop", "Linear".</summary>
    public string AutoMixStyle { get; set; } = "BassSwap";
    /// <summary>Whether to beat-match and phase-align tracks when beat grids are detected.</summary>
    public bool AutoMixBeatSync { get; set; } = true;
    /// <summary>Whether to trim leading silence on incoming tracks so music enters promptly (Smart Cue).</summary>
    public bool AutoMixSkipSilence { get; set; } = true;

    // ----- Appearance -----
    public bool UseSystemAccent { get; set; } = true;
    public string CustomAccentHex { get; set; } = "#2E7D32";

    // ----- Effects chain (Equalizer APO style; engine is source of truth) -----
    public List<Audio.EffectBlock> EffectChain { get; set; } = new();

    // ----- Last effect state (written on exit, restored on startup) -----
    public double LastTempoPercent { get; set; } = 100;
    public double LastPitchSemitones { get; set; } = 0;
    public double LastVolumePercent { get; set; } = 80;
    public float[]? LastEqGains { get; set; }
    public bool LastEqEnabled { get; set; } = true;

    /// <summary>
    /// Round-trips keys this build doesn't know (e.g. fields written by a newer
    /// channel), so running an older build never wipes settings from a newer one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicSpeedChanger", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch { /* corrupt file → defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* settings are best-effort */ }
    }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this);
        var copy = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        copy.Clamp();
        return copy;
    }

    public void CopyFrom(AppSettings other)
    {
        var json = JsonSerializer.Serialize(other);
        var copy = JsonSerializer.Deserialize<AppSettings>(json);
        if (copy == null) return;
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanWrite || prop.Name == nameof(FilePath)) continue;
            if (prop.Name.StartsWith("Last", StringComparison.Ordinal)) continue; // effect state is runtime-owned
            if (prop.Name == nameof(EffectChain)) continue; // owned by the engine, not the dialog draft
            prop.SetValue(this, prop.GetValue(copy));
        }
        Clamp();
    }

    private void Clamp()
    {
        DefaultTempoPercent = Math.Clamp(DefaultTempoPercent, 25, 300);
        DefaultPitchSemitones = Math.Clamp(DefaultPitchSemitones, -12, 12);
        TempoSliderStep = Math.Clamp(TempoSliderStep, 0.5, 10);
        PitchSliderStep = Math.Clamp(PitchSliderStep, 0.1, 1);
        WaveformPeaks = Math.Clamp(WaveformPeaks, 100, 8000);
        LastTempoPercent = Math.Clamp(LastTempoPercent, 25, 300);
        LastPitchSemitones = Math.Clamp(LastPitchSemitones, -12, 12);
        LastVolumePercent = Math.Clamp(LastVolumePercent, 0, 100);
        RepeatMode = Math.Clamp(RepeatMode, 0, 2);
        AutoMixSeconds = Math.Clamp(AutoMixSeconds, 1, 12);
        AutoMixStyle = NormalizeAutoMixStyle(AutoMixStyle);
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl)) UpdateFeedUrl = DefaultFeedUrl;
        if (string.IsNullOrWhiteSpace(CustomAccentHex)) CustomAccentHex = "#2E7D32";
        if (!string.Equals(InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase))
            InsiderChannel = "Beta";
        LayoutType = NormalizeLayoutType(LayoutType);
        PlayerType = NormalizePlayerType(PlayerType);
        if (string.IsNullOrWhiteSpace(PatreonRefreshToken)) PatreonRefreshToken = null;
        if (string.IsNullOrWhiteSpace(PatreonFullName)) PatreonFullName = null;
        if (LastEqGains != null && LastEqGains.Length != Audio.GraphicEqualizer.BandCount)
            LastEqGains = null;
    }

    public static string NormalizeAutoMixStyle(string? value) =>
        string.Equals(value, "Blend", StringComparison.OrdinalIgnoreCase) ? "Blend"
        : string.Equals(value, "Rise", StringComparison.OrdinalIgnoreCase) ? "Rise"
        : string.Equals(value, "BeatDrop", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(value, "Beat Drop", StringComparison.OrdinalIgnoreCase) ? "BeatDrop"
        : string.Equals(value, "Linear", StringComparison.OrdinalIgnoreCase) ? "Linear"
        : "BassSwap";

    private static string NormalizeLayoutType(string? value) =>
        string.Equals(value, "Mavericks", StringComparison.OrdinalIgnoreCase) ? "Mavericks"
        : string.Equals(value, "Calico", StringComparison.OrdinalIgnoreCase) ? "Calico"
        : string.Equals(value, "Montara", StringComparison.OrdinalIgnoreCase) ? "Montara"
        : "Mojave";

    private static string NormalizePlayerType(string? value) =>
        string.Equals(value, "Compact", StringComparison.OrdinalIgnoreCase) ? "Compact"
        : string.Equals(value, "CompactInline", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(value, "Compact Inline", StringComparison.OrdinalIgnoreCase) ? "CompactInline"
        : "Comfy";
}
