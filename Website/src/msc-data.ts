export interface Feature {
  title: string;
  body: string;
}

export const FEATURES: Feature[] = [
  {
    title: "Tempo 50–200%",
    body: "Presets 0.5×, 0.75×, 1×, 1.25×, 1.5×. Pitch preserved. Effective-duration readout like “(plays as 2:00 @ 150%)”.",
  },
  {
    title: "Pitch ±12 semitones",
    body: "0.1-st resolution, tempo preserved. Shift the key without rushing the band.",
  },
  {
    title: "AB loop",
    body: "Set A / B points badged on the waveform, loop forwards or in reverse while drilling a passage.",
  },
  {
    title: "Reverse playback",
    body: "Play backwards from the current position for ear-training and transcription.",
  },
  {
    title: "31-band graphic EQ",
    body: "ISO 20 Hz–20 kHz ±15 dB with presets, bypass, and draggable curve — applied live and in exports.",
  },
  {
    title: "Waveform + zoom 0–10",
    body: "Playhead, click/drag seek, loop-region highlight. Zoomed in, the playhead pins center and the wave scrolls.",
  },
  {
    title: "Export WAV / M4A",
    body: "Bakes in tempo + pitch + EQ. Exports the AB loop if set, with repeat count and _eq in the filename.",
  },
  {
    title: "Files panel + settings",
    body: "Trailing inspector playlist with drag-and-drop and restore. Apple-Music-style Settings (⌘,) for everything.",
  },
];

export const REPO = "embabyty/MusicSpeedChangerMacOS";
export const RELEASES_URL = `https://github.com/${REPO}/releases`;
export const API_LATEST = `https://api.github.com/repos/${REPO}/releases/latest`;
