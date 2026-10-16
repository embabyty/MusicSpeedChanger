import Foundation

/// Persisted settings, mirroring the Windows AppSettings (JSON on disk).
public final class AppSettings: Codable {
    public static let defaultFeedUrl =
        "https://api.github.com/repos/embabyty/MusicSpeedChangerMacOS/releases/latest"

    /// Previous default pointing at the Windows repo — migrated on load.
    public static let legacyWindowsFeedUrl =
        "https://api.github.com/repos/embabyty/MusicSpeedChanger/releases/latest"

    // Updates
    public var autoCheckUpdates: Bool = true
    public var updateFeedUrl: String = AppSettings.defaultFeedUrl
    /// When true, update checks also consider pre-release (beta) versions.
    public var includeBetaUpdates: Bool = false
    /// When true, the current track and tempo/pitch show on the Discord profile.
    public var discordRPCEnabled: Bool = true

    // Speed & pitch
    public var defaultTempoPercent: Double = 100
    public var defaultPitchSemitones: Double = 0
    public var applyDefaultsOnFileLoad: Bool = true
    public var tempoSliderStep: Double = 1
    public var pitchSliderStep: Double = 0.1

    // Editor controls
    public var showTempoPanel: Bool = true
    public var showPitchPanel: Bool = true
    public var showLoopPanel: Bool = true
    public var showEqPanel: Bool = true
    public var editorPreset: EditorPreset = .default
    public var customControls: [PlayerControl] = PlayerControl.defaultCustom
    /// Per-card widget sizes, keyed by PlayerControl raw value. Missing =
    /// the control's defaultCardSize.
    public var customCardSizes: [String: CardSize] = [:]
    public var waveformPeaks: Int = 1400
    public var clickToSeek: Bool = true

    // Effects
    /// When true, tempo/pitch/volume/EQ are restored on startup and kept across files.
    public var rememberEffects: Bool = true

    // Queue
    public var rememberFileList: Bool = true
    public var fileListPaths: [String] = []
    public var lastFilePath: String?
    public var shuffleEnabled: Bool = false
    public var repeatMode: RepeatMode = .off

    // Last effect state (written on exit, restored on startup)
    public var lastTempoPercent: Double = 100
    public var lastPitchSemitones: Double = 0
    public var lastVolumePercent: Double = 80
    public var lastEqGains: [Float]?
    public var lastEqEnabled: Bool = true
    public var lastReverbEnabled: Bool = false
    public var lastReverbMix: Float = 30
    public var lastDelayEnabled: Bool = false
    public var lastDelayMix: Float = 30
    public var lastDelayTime: Double = 0.35
    public var lastDelayFeedback: Float = 40
    public var lastDistortionEnabled: Bool = false
    public var lastDistortionMix: Float = 50
    public var lastDistortionGain: Float = 0

    // Appearance
    public var useSystemAccent: Bool = true
    public var customAccentHex: String = "#2E7D32"

    // Patreon supporter gate (Beta builds)
    public var patreonSupporterEmail: String?
    public var patreonVerifiedUntil: Date?
    public var patreonHasEAFPlus: Bool = false
    public var patreonHasEAFBasic: Bool = false

    /// Last app version that showed the What's New splash. Nil = never shown.
    public var lastSeenVersion: String?

    enum CodingKeys: String, CodingKey {
        case autoCheckUpdates, updateFeedUrl, includeBetaUpdates, discordRPCEnabled
        case defaultTempoPercent, defaultPitchSemitones, applyDefaultsOnFileLoad, tempoSliderStep, pitchSliderStep
        case showTempoPanel, showPitchPanel, showLoopPanel, showEqPanel, editorPreset, customControls, customCardSizes, waveformPeaks, clickToSeek
        case rememberEffects, rememberFileList, fileListPaths, lastFilePath
        case shuffleEnabled, repeatMode
        case lastTempoPercent, lastPitchSemitones, lastVolumePercent, lastEqGains, lastEqEnabled
        case lastReverbEnabled, lastReverbMix, lastDelayEnabled, lastDelayMix, lastDelayTime, lastDelayFeedback
        case lastDistortionEnabled, lastDistortionMix, lastDistortionGain
        case useSystemAccent, customAccentHex
        case patreonSupporterEmail, patreonVerifiedUntil, patreonHasEAFPlus, patreonHasEAFBasic
        case lastSeenVersion
    }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        autoCheckUpdates = try c.decodeIfPresent(Bool.self, forKey: .autoCheckUpdates) ?? true
        updateFeedUrl = try c.decodeIfPresent(String.self, forKey: .updateFeedUrl) ?? AppSettings.defaultFeedUrl
        includeBetaUpdates = try c.decodeIfPresent(Bool.self, forKey: .includeBetaUpdates) ?? false
        discordRPCEnabled = try c.decodeIfPresent(Bool.self, forKey: .discordRPCEnabled) ?? true
        defaultTempoPercent = try c.decodeIfPresent(Double.self, forKey: .defaultTempoPercent) ?? 100
        defaultPitchSemitones = try c.decodeIfPresent(Double.self, forKey: .defaultPitchSemitones) ?? 0
        applyDefaultsOnFileLoad = try c.decodeIfPresent(Bool.self, forKey: .applyDefaultsOnFileLoad) ?? true
        tempoSliderStep = try c.decodeIfPresent(Double.self, forKey: .tempoSliderStep) ?? 1
        pitchSliderStep = try c.decodeIfPresent(Double.self, forKey: .pitchSliderStep) ?? 0.1
        showTempoPanel = try c.decodeIfPresent(Bool.self, forKey: .showTempoPanel) ?? true
        showPitchPanel = try c.decodeIfPresent(Bool.self, forKey: .showPitchPanel) ?? true
        showLoopPanel = try c.decodeIfPresent(Bool.self, forKey: .showLoopPanel) ?? true
        showEqPanel = try c.decodeIfPresent(Bool.self, forKey: .showEqPanel) ?? true
        editorPreset = try c.decodeIfPresent(EditorPreset.self, forKey: .editorPreset) ?? .default
        customControls = try c.decodeIfPresent([PlayerControl].self, forKey: .customControls) ?? PlayerControl.defaultCustom
        customCardSizes = try c.decodeIfPresent([String: CardSize].self, forKey: .customCardSizes) ?? [:]
        waveformPeaks = try c.decodeIfPresent(Int.self, forKey: .waveformPeaks) ?? 1400
        clickToSeek = try c.decodeIfPresent(Bool.self, forKey: .clickToSeek) ?? true
        rememberEffects = try c.decodeIfPresent(Bool.self, forKey: .rememberEffects) ?? true
        rememberFileList = try c.decodeIfPresent(Bool.self, forKey: .rememberFileList) ?? true
        fileListPaths = try c.decodeIfPresent([String].self, forKey: .fileListPaths) ?? []
        lastFilePath = try c.decodeIfPresent(String.self, forKey: .lastFilePath)
        shuffleEnabled = try c.decodeIfPresent(Bool.self, forKey: .shuffleEnabled) ?? false
        repeatMode = try c.decodeIfPresent(RepeatMode.self, forKey: .repeatMode) ?? .off
        lastTempoPercent = try c.decodeIfPresent(Double.self, forKey: .lastTempoPercent) ?? 100
        lastPitchSemitones = try c.decodeIfPresent(Double.self, forKey: .lastPitchSemitones) ?? 0
        lastVolumePercent = try c.decodeIfPresent(Double.self, forKey: .lastVolumePercent) ?? 80
        lastEqGains = try c.decodeIfPresent([Float].self, forKey: .lastEqGains)
        lastEqEnabled = try c.decodeIfPresent(Bool.self, forKey: .lastEqEnabled) ?? true
        lastReverbEnabled = try c.decodeIfPresent(Bool.self, forKey: .lastReverbEnabled) ?? false
        lastReverbMix = try c.decodeIfPresent(Float.self, forKey: .lastReverbMix) ?? 30
        lastDelayEnabled = try c.decodeIfPresent(Bool.self, forKey: .lastDelayEnabled) ?? false
        lastDelayMix = try c.decodeIfPresent(Float.self, forKey: .lastDelayMix) ?? 30
        lastDelayTime = try c.decodeIfPresent(Double.self, forKey: .lastDelayTime) ?? 0.35
        lastDelayFeedback = try c.decodeIfPresent(Float.self, forKey: .lastDelayFeedback) ?? 40
        lastDistortionEnabled = try c.decodeIfPresent(Bool.self, forKey: .lastDistortionEnabled) ?? false
        lastDistortionMix = try c.decodeIfPresent(Float.self, forKey: .lastDistortionMix) ?? 50
        lastDistortionGain = try c.decodeIfPresent(Float.self, forKey: .lastDistortionGain) ?? 0
        useSystemAccent = try c.decodeIfPresent(Bool.self, forKey: .useSystemAccent) ?? true
        customAccentHex = try c.decodeIfPresent(String.self, forKey: .customAccentHex) ?? "#2E7D32"
        patreonSupporterEmail = try c.decodeIfPresent(String.self, forKey: .patreonSupporterEmail)
        patreonVerifiedUntil = try c.decodeIfPresent(Date.self, forKey: .patreonVerifiedUntil)
        patreonHasEAFPlus = try c.decodeIfPresent(Bool.self, forKey: .patreonHasEAFPlus) ?? false
        patreonHasEAFBasic = try c.decodeIfPresent(Bool.self, forKey: .patreonHasEAFBasic) ?? false
        lastSeenVersion = try c.decodeIfPresent(String.self, forKey: .lastSeenVersion)
        clamp()
    }

    public func clamp() {
        defaultTempoPercent = min(max(defaultTempoPercent, 25), 300)
        defaultPitchSemitones = min(max(defaultPitchSemitones, -12), 12)
        tempoSliderStep = min(max(tempoSliderStep, 0.5), 10)
        pitchSliderStep = min(max(pitchSliderStep, 0.1), 1)
        waveformPeaks = min(max(waveformPeaks, 100), 8000)
        lastTempoPercent = min(max(lastTempoPercent, 25), 300)
        lastPitchSemitones = min(max(lastPitchSemitones, -12), 12)
        lastVolumePercent = min(max(lastVolumePercent, 0), 100)
        if updateFeedUrl.trimmingCharacters(in: .whitespaces).isEmpty {
            updateFeedUrl = AppSettings.defaultFeedUrl
        }
        // Point installs that still reference the Windows repo at this project's releases.
        if updateFeedUrl == AppSettings.legacyWindowsFeedUrl {
            updateFeedUrl = AppSettings.defaultFeedUrl
        }
        if customAccentHex.trimmingCharacters(in: .whitespaces).isEmpty {
            customAccentHex = "#2E7D32"
        }
        if let g = lastEqGains, g.count != EQBands.bandCount { lastEqGains = nil }
        if customControls.isEmpty { customControls = PlayerControl.defaultCustom }
    }

    /// Effective widget size for a control (stored override or default).
    public func cardSize(for control: PlayerControl) -> CardSize {
        customCardSizes[control.rawValue] ?? control.defaultCardSize
    }

    public func setCardSize(_ size: CardSize, for control: PlayerControl) {
        customCardSizes[control.rawValue] = size
    }

    public static var fileURL: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MusicSpeedChanger", isDirectory: true)
        return dir.appendingPathComponent("settings.json")
    }

    public static func load() -> AppSettings {
        if let data = try? Data(contentsOf: fileURL),
           let settings = try? JSONDecoder().decode(AppSettings.self, from: data)
        {
            return settings
        }
        return AppSettings()
    }

    public func save() {
        do {
            try FileManager.default.createDirectory(at: AppSettings.fileURL.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(self)
            try data.write(to: AppSettings.fileURL)
        } catch { /* best effort */ }
    }
}
