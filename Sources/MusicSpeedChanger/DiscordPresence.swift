import AppKit
import Combine
import Foundation
import MSCCore

/// Pushes playback state to Discord Rich Presence.
/// Silent when Discord isn't running or the toggle is off.
@MainActor
final class DiscordPresenceController: ObservableObject {
    private static let clientID = "1556146469155897526"
    private static let repoURL = "https://github.com/embabyty/MusicSpeedChangerMacOS"

    private let rpc = DiscordRPC(clientID: clientID)
    private weak var engine: AudioEngine?
    private weak var settings: SettingsStore?
    private var cancellables = Set<AnyCancellable>()
    private var debounce: Task<Void, Never>?
    private var lastSignature = ""
    private var heartbeat: Timer?

    func configure(engine: AudioEngine, settings: SettingsStore) {
        guard self.engine == nil else { return }
        self.engine = engine
        self.settings = settings
        engine.objectWillChange
            .sink { [weak self] _ in self?.scheduleRefresh() }
            .store(in: &cancellables)
        settings.objectWillChange
            .sink { [weak self] _ in self?.scheduleRefresh() }
            .store(in: &cancellables)
        NotificationCenter.default.addObserver(
            self, selector: #selector(handleTerminate),
            name: NSApplication.willTerminateNotification, object: nil)
        // Re-push periodically: covers Discord (re)starting mid-session.
        heartbeat = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        scheduleRefresh()
    }

    @objc private nonisolated func handleTerminate() {
        rpc.shutdownSync()
    }

    private func scheduleRefresh() {
        debounce?.cancel()
        debounce = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 700_000_000)
            guard !Task.isCancelled else { return }
            self?.refresh()
        }
    }

    private func refresh() {
        guard let engine, let settings else { return }
        guard settings.settings.discordRPCEnabled,
              engine.isLoaded,
              let name = engine.fileName, !name.isEmpty
        else { clearIfNeeded(); return }

        var mods: [String] = []
        if engine.tempoPercent != 100 { mods.append("\(Int(engine.tempoPercent.rounded()))%") }
        if engine.pitchSemitones != 0 {
            mods.append(String(format: "%+.1f st", engine.pitchSemitones))
        }
        if engine.loopEnabled && engine.loopIsValid { mods.append("AB loop") }
        if engine.reverseEnabled { mods.append("Reversed") }

        let signature = "\(name)|\(engine.isPlaying)|\(mods.joined(separator: ","))"
        guard signature != lastSignature else { return }
        lastSignature = signature

        if engine.isPlaying {
            // Elapsed output seconds, so Discord's timer tracks the stretch.
            let rate = max(engine.tempoPercent, 1) / 100
            let start = Int(Date().timeIntervalSince1970 - engine.position / rate)
            rpc.setActivity(DiscordActivity(
                details: name,
                state: mods.isEmpty ? "Playing" : mods.joined(separator: " • "),
                startTimestamp: start,
                largeImageKey: "app-icon", largeImageText: "Music Speed Changer",
                smallImageKey: "play", smallImageText: "Playing",
                buttons: [("Get the app", Self.repoURL)]))
        } else {
            rpc.setActivity(DiscordActivity(
                details: name,
                state: mods.isEmpty ? "Paused" : "Paused • " + mods.joined(separator: " • "),
                largeImageKey: "app-icon", largeImageText: "Music Speed Changer",
                smallImageKey: "pause", smallImageText: "Paused",
                buttons: [("Get the app", Self.repoURL)]))
        }
    }

    private func clearIfNeeded() {
        guard !lastSignature.isEmpty else { return }
        lastSignature = ""
        rpc.clearActivity()
    }
}
