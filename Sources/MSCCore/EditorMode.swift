import Foundation

/// Individual player controls, mirroring the iOS Custom Editor Mode list.
/// Only a subset is implemented on macOS; the rest are shown as
/// "coming soon" placeholders so presets stay faithful to iOS.
public enum PlayerControl: String, CaseIterable, Codable, Identifiable, Sendable {
    case waveform
    case reverse
    case tempo
    case pitch
    case loop
    case equalizer
    case preamp
    case volume
    case art
    case bpm
    case key
    case a4Tuning
    case trackIsolation
    case markers
    case balance
    case reverb
    case delay
    case distortion
    case linkTempoPitch
    case mono
    case invertStereo

    public var id: Self { self }

    public var title: String {
        switch self {
        case .waveform: "Waveform"
        case .reverse: "Reverse"
        case .tempo: "Tempo"
        case .pitch: "Pitch"
        case .loop: "Loop"
        case .equalizer: "Equalizer"
        case .preamp: "Preamp"
        case .volume: "Volume"
        case .art: "Art"
        case .bpm: "BPM"
        case .key: "Key"
        case .a4Tuning: "A4 Tuning"
        case .trackIsolation: "Track Isolation"
        case .markers: "Markers"
        case .balance: "Balance"
        case .reverb: "Reverb"
        case .delay: "Delay"
        case .distortion: "Distortion"
        case .linkTempoPitch: "Link Tempo and Pitch"
        case .mono: "Mono"
        case .invertStereo: "Invert Stereo"
        }
    }

    public var icon: String {
        switch self {
        case .waveform: "waveform"
        case .reverse: "arrow.uturn.backward"
        case .tempo: "speedometer"
        case .pitch: "music.note"
        case .loop: "repeat"
        case .equalizer: "slider.horizontal.3"
        case .preamp: "speaker.wave.1"
        case .volume: "speaker.wave.2"
        case .art: "photo"
        case .bpm: "metronome"
        case .key: "pianokeys"
        case .a4Tuning: "tuningfork"
        case .trackIsolation: "person.2"
        case .markers: "flag"
        case .balance: "circle.lefthalf.filled"
        case .reverb: "wind"
        case .delay: "timer"
        case .distortion: "bolt"
        case .linkTempoPitch: "link"
        case .mono: "speaker"
        case .invertStereo: "arrow.left.arrow.right"
        }
    }

    /// Controls with real macOS UI. Everything else renders as a
    /// "not available on Mac yet" note inside its preset.
    public var isSupportedOnMac: Bool {
        switch self {
        case .waveform, .reverse, .tempo, .pitch, .loop, .equalizer, .volume:
            return true
        default:
            return false
        }
    }

    /// Default Custom preset contents, matching the iOS screenshot's
    /// Included Controls (supported + preamp/volume for transport).
    public static var defaultCustom: [PlayerControl] {
        [.waveform, .reverse, .tempo, .pitch, .loop, .equalizer, .preamp, .volume]
    }

    /// Default widget-style size for a freshly added card.
    public var defaultCardSize: CardSize {
        switch self {
        case .waveform, .loop, .equalizer: .large
        case .mono: .small
        default: .medium
        }
    }

    /// One-line gallery caption shown under the card previews.
    public var galleryBlurb: String {
        switch self {
        case .waveform: "Scrub through the track waveform."
        case .reverse: "Play backwards from the current position."
        case .tempo: "Change speed without changing pitch."
        case .pitch: "Change key without changing tempo."
        case .loop: "Repeat a practice section A–B."
        case .equalizer: "Shape the tone with 31 bands."
        case .preamp: "Boost or cut the overall signal."
        case .volume: "Control the output level."
        case .art: "Show the track artwork."
        case .bpm: "Detect and display the tempo in BPM."
        case .key: "Detect and display the musical key."
        case .a4Tuning: "Adjust the A4 tuning frequency."
        case .trackIsolation: "Isolate vocals or instruments."
        case .markers: "Jump between song markers."
        case .balance: "Shift the stereo balance."
        case .reverb: "Add room ambience."
        case .delay: "Add echo repeats."
        case .distortion: "Add grit and drive."
        case .linkTempoPitch: "Lock tempo and pitch together."
        case .mono: "Collapse the mix to mono."
        case .invertStereo: "Swap the left and right channels."
        }
    }
}

/// Size variants for player cards, mirroring macOS widget S/M/L sizes.
/// Small spans one grid column, medium spans two, large spans the full row.
public enum CardSize: String, CaseIterable, Codable, Sendable {
    case small
    case medium
    case large

    public var title: String {
        switch self {
        case .small: "Small"
        case .medium: "Medium"
        case .large: "Large"
        }
    }

    public var shortTitle: String {
        switch self {
        case .small: "S"
        case .medium: "M"
        case .large: "L"
        }
    }

    /// Grid columns spanned in the player's 4-column layout.
    public var span: Int {
        switch self {
        case .small: 1
        case .medium: 2
        case .large: 4
        }
    }
}

/// Editor Mode presets, matching the iOS Settings screen.
public enum EditorPreset: String, CaseIterable, Codable, Identifiable, Sendable {
    case `default`
    case bpmAndKey
    case markers
    case equalizer
    case trackIsolation
    case effects
    case reverse
    case a4Tuning
    case player
    case volumeControls
    case custom

    public var id: Self { self }

    public var title: String {
        switch self {
        case .default: "Default"
        case .bpmAndKey: "BPM and Musical Key"
        case .markers: "Markers"
        case .equalizer: "Equalizer"
        case .trackIsolation: "Track Isolation"
        case .effects: "Effects"
        case .reverse: "Reverse"
        case .a4Tuning: "A4 Tuning Frequency"
        case .player: "Player"
        case .volumeControls: "Volume Controls"
        case .custom: "Custom"
        }
    }

    public var subtitle: String {
        switch self {
        case .default: "Tempo, Pitch, Loop, Waveform"
        case .bpmAndKey: "Tempo, BPM, Pitch, Key, Loop, Waveform"
        case .markers: "Tempo, Pitch, Loop, Markers, Waveform"
        case .equalizer: "Preamp, Balance, Equalizer"
        case .trackIsolation: "Tempo, Pitch, Loop, Track Isolation, Waveform"
        case .effects: "Reverb, Delay, Distortion, Mono, Invert Stereo"
        case .reverse: "Reverse, Tempo, Pitch, Loop, Waveform"
        case .a4Tuning: "A4 Tuning, Pitch, Key, Tempo, Waveform"
        case .player: "Art, Volume, Tempo"
        case .volumeControls: "Volume, Preamp, Balance, Equalizer"
        case .custom: "Your configured controls"
        }
    }

    public var icon: String {
        switch self {
        case .default: "waveform"
        case .bpmAndKey: "metronome"
        case .markers: "flag"
        case .equalizer: "slider.horizontal.3"
        case .trackIsolation: "person.2"
        case .effects: "wand.and.stars"
        case .reverse: "arrow.uturn.backward"
        case .a4Tuning: "tuningfork"
        case .player: "play.circle"
        case .volumeControls: "speaker.wave.2"
        case .custom: "star"
        }
    }

    /// Built-in control sets. `.custom` is resolved from settings.
    public var includedControls: [PlayerControl] {
        switch self {
        case .default: [.tempo, .pitch, .loop, .waveform]
        case .bpmAndKey: [.tempo, .bpm, .pitch, .key, .loop, .waveform]
        case .markers: [.tempo, .pitch, .loop, .markers, .waveform]
        case .equalizer: [.preamp, .balance, .equalizer]
        case .trackIsolation: [.tempo, .pitch, .loop, .trackIsolation, .waveform]
        case .effects: [.reverb, .delay, .distortion, .mono, .invertStereo]
        case .reverse: [.reverse, .tempo, .pitch, .loop, .waveform]
        case .a4Tuning: [.a4Tuning, .pitch, .key, .tempo, .waveform]
        case .player: [.art, .volume, .tempo]
        case .volumeControls: [.volume, .preamp, .balance, .equalizer]
        case .custom: PlayerControl.defaultCustom
        }
    }
}
