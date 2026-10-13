import SwiftUI
import MSCCore

@main
struct MusicSpeedChangerApp: App {
    @StateObject private var engine = AudioEngine()
    @StateObject private var tracks = TrackList()
    @StateObject private var settings = SettingsStore()
    @StateObject private var discord = DiscordPresenceController()

    var body: some Scene {
        WindowGroup("Music Speed Changer") {
            ContentView()
                .environmentObject(engine)
                .environmentObject(tracks)
                .environmentObject(settings)
                .preferredColorScheme(.dark)
                .frame(minWidth: 900, minHeight: 720)
                .onAppear {
                    NSApp.activate(ignoringOtherApps: true)
                    discord.configure(engine: engine, settings: settings)
                    if settings.settings.autoCheckUpdates {
                        UpdateChecker.checkIfNewer()
                    }
                }
                .onDisappear {
                    settings.persistLastEffects(engine: engine)
                }
        }
        .defaultSize(width: 1280, height: 860)
        .defaultPosition(.center)
        .windowStyle(.titleBar)
        .commands {
            CommandGroup(replacing: .newItem) { }
            CommandGroup(after: .pasteboard) {
                Button("Clear All Files") { tracks.clear() }
                    .disabled(tracks.tracks.isEmpty)
                Divider()
                Button("Edit Cards…") {
                    NotificationCenter.default.post(name: .showConfigureCustomPresets, object: nil)
                }
            }
        }

        Settings {
            SettingsView()
                .environmentObject(settings)
                .preferredColorScheme(.dark)
        }
    }
}

@MainActor
final class SettingsStore: ObservableObject {
    @Published var settings: AppSettings

    init() {
        self.settings = AppSettings.load()
    }

    func persist() { settings.save() }

    func persistLastEffects(engine: AudioEngine) {
        settings.lastTempoPercent = engine.tempoPercent
        settings.lastPitchSemitones = engine.pitchSemitones
        settings.lastVolumePercent = engine.volumePercent
        settings.lastEqGains = engine.eqGains
        settings.lastEqEnabled = engine.eqEnabled
        if settings.rememberFileList {
            settings.fileListPaths = tracksPathCache
        }
        settings.save()
    }

    /// Set by the track list so we can remember it on save.
    var tracksPathCache: [String] = []
}

@MainActor
final class TrackList: ObservableObject {
    @Published var tracks: [TrackItem] = []
    @Published var selectedID: TrackItem.ID?

    init() {
        let s = AppSettings.load()
        if s.rememberFileList {
            tracks = s.fileListPaths.compactMap { p in
                FileManager.default.fileExists(atPath: p) ? TrackItem(url: URL(fileURLWithPath: p)) : nil
            }
        }
    }

    var paths: [String] { tracks.map { $0.url.path } }

    func add(urls: [URL]) {
        for url in urls where !tracks.contains(where: { $0.url == url }) {
            tracks.append(TrackItem(url: url))
        }
    }

    func removeSelected() {
        guard let id = selectedID, let idx = tracks.firstIndex(where: { $0.id == id }) else { return }
        tracks.remove(at: idx)
        selectedID = nil
    }

    func remove(_ track: TrackItem) {
        tracks.removeAll(where: { $0.id == track.id })
        if selectedID == track.id { selectedID = nil }
    }

    func clear() { tracks.removeAll(); selectedID = nil }

    func step(_ direction: Int) -> TrackItem? {
        guard !tracks.isEmpty else { return nil }
        let idx = selectedID.flatMap { id in tracks.firstIndex(where: { $0.id == id }) } ?? 0
        let next = min(max(idx + direction, 0), tracks.count - 1)
        selectedID = tracks[next].id
        return tracks[next]
    }

    /// Linear step that can wrap the queue (repeat-all); nil at the ends
    /// when not wrapping. Unlike `step`, a missing selection starts before
    /// the first item (forward) instead of jumping onto it.
    func peek(_ direction: Int, wrapping: Bool) -> TrackItem? {
        guard !tracks.isEmpty else { return nil }
        let idx = selectedID.flatMap { id in tracks.firstIndex(where: { $0.id == id }) }
            ?? (direction > 0 ? -1 : 0)
        var next = idx + direction
        if wrapping { next = (next + tracks.count) % tracks.count }
        guard tracks.indices.contains(next) else { return nil }
        selectedID = tracks[next].id
        return tracks[next]
    }
}
