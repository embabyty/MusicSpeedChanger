# MusicSpeedChanger (Desktop)

A Windows desktop version of the Music Speed Changer mobile app — slow down / speed up
music **without changing pitch**, and shift key **without changing tempo**. Built for
musicians practicing tricky sections.

## Features

- **Tempo control** 25%–200% (presets: 0.5x, 0.75x, 1x, 1.25x, 1.5x), pitch preserved
- **Pitch shift** −12…+12 semitones (0.1-st resolution), tempo preserved
- **AB loop** — set A / B points, loop the section while practicing
- **31-band graphic EQ** (ISO 20 Hz–20 kHz, ±15 dB) with presets, bypass switch, applied live and in exports
- **Import** MP3, WAV, M4A/AAC, WMA, AIFF, FLAC (via Media Foundation / NAudio)
- **Waveform display** with playhead, click/drag to seek, loop-region highlight
- **Export to WAV** with current tempo + pitch + EQ (exports the AB loop if one is set, `_eq` in filename when EQ is active)
- Volume control, effective-duration readout ("plays as 2:00 @ 150%")

## Tech

- .NET 8 + WPF (Windows only)
- [NAudio](https://github.com/naudio/NAudio) for decode/playback
- [SoundTouch.NET](https://github.com/owoudenberg/soundtouch.net) for independent tempo/pitch

## Run / build

```powershell
dotnet build MusicSpeedChanger.sln -c Release
dotnet run --project src/MusicSpeedChanger
```

Requires the .NET 8 SDK and Windows 10+ (Media Foundation ships with Windows).
