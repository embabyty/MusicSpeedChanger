using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
    /// <summary>True after a verified Patreon login (unlocks the beta switch).</summary>
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

    // ----- Appearance -----
    public bool UseSystemAccent { get; set; } = true;
    public string CustomAccentHex { get; set; } = "#2E7D32";

    // ----- Last effect state (written on exit, restored on startup) -----
    public double LastTempoPercent { get; set; } = 100;
    public double LastPitchSemitones { get; set; } = 0;
    public double LastVolumePercent { get; set; } = 80;
    public float[]? LastEqGains { get; set; }
    public bool LastEqEnabled { get; set; } = true;

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
        return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl)) UpdateFeedUrl = DefaultFeedUrl;
        if (string.IsNullOrWhiteSpace(CustomAccentHex)) CustomAccentHex = "#2E7D32";
        if (string.IsNullOrWhiteSpace(PatreonRefreshToken)) PatreonRefreshToken = null;
        if (string.IsNullOrWhiteSpace(PatreonFullName)) PatreonFullName = null;
        if (LastEqGains != null && LastEqGains.Length != Audio.GraphicEqualizer.BandCount)
            LastEqGains = null;
    }
}
