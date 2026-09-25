# MusicSpeedChanger (Desktop)

<img src="src/MusicSpeedChanger/Assets/MusicSpeed.png" width="128" alt="MusicSpeedChanger logo" />

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
- Clean DSP chain: SoundTouch runs with the anti-alias filter (64 taps) and
  exact-seek mode, a long-window stretch profile kicks in below ~65% speed to
  avoid slow-tempo warble, and a transparent peak limiter (-0.2 dBFS) after the
  EQ stops stretch/EQ overshoot from clipping — playback and exports alike
- **Automatic updates** — checks GitHub Releases on startup, downloads and launches the installer
- **Settings** (⚙ in the header): update feed + manual check, default tempo/pitch and slider steps,
  editor panel visibility, waveform detail, click-to-seek, queue memory, save/restore of effects
  (tempo, pitch, volume, EQ) across sessions, and Windows accent-color matching
- **Queue sidebar** — keep a list of audio files: add via picker or drag-and-drop,
  click a track to load and play it, ⏮/⏭ step through the list, per-track
  durations, Delete-key removal, list restored on startup (toggle in Settings)

## Tech

- .NET 8 + WinUI 3 / Windows App SDK 1.7 (unpackaged, Windows only)
- [NAudio](https://github.com/naudio/NAudio) for decode/playback
- [SoundTouch.NET](https://github.com/owoudenberg/soundtouch.net) for independent tempo/pitch

## Run / build

```powershell
# Build (WinUI needs an explicit platform: x64, x86, or ARM64)
dotnet build MusicSpeedChanger.sln -c Release -p:Platform=x64

# Run from source
dotnet run --project src/MusicSpeedChanger -p:Platform=x64

# Publish for the installer (ships the whole folder: exe + WinAppSDK runtime)
dotnet publish src/MusicSpeedChanger/MusicSpeedChanger.csproj -c Release -p:Platform=x64 -o dist/publish
```

Requires the .NET 8 SDK and Windows 10 1809+ (Media Foundation ships with Windows).
Building from the command line also needs a Visual Studio 18 install — the project
falls back to its AppxPackage MSBuild tasks for PRI generation (see
`VsAppxPackageDir` in `src/MusicSpeedChanger/MusicSpeedChanger.csproj`).

Notes on the WinUI port: same C# audio engine (`src/MusicSpeedChanger/Audio/`,
unchanged) with the WPF UI replaced — `MainWindow` is now a WinUI 3 `Window`
(file pickers via `FileOpenPicker`/`FileSavePicker`, errors via `ContentDialog`),
and the waveform is rendered into a `WriteableBitmap` instead of WPF's
`DrawingContext`.

## Updates & releases

The in-app updater polls the GitHub Releases API
(`embabyty/MusicSpeedChanger`, configurable in Settings) and installs the
`Setup-*.exe` asset when its version exceeds the running build. To ship a release:

1. Bump `<Version>` in `src/MusicSpeedChanger/MusicSpeedChanger.csproj`.
2. Publish and rebuild the installer, then attach the resulting
   `Setup-MusicSpeedChanger-<version>.exe` to a GitHub release tagged
   `v<version>` (e.g. `v1.1.0`).

Settings live in `%LocalAppData%\MusicSpeedChanger\settings.json` and can be
edited by hand when the app is closed; effect state (tempo/pitch/volume/EQ) is
saved there on exit when "Save effects and restore them on startup" is on.
