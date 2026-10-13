import SwiftUI
import AppKit
import MSCCore

enum SettingsPane: String, CaseIterable, Identifiable {
    case general, playback, files, advanced

    var id: Self { self }

    var title: String {
        switch self {
        case .general: "General"
        case .playback: "Playback"
        case .files: "Files"
        case .advanced: "Advanced"
        }
    }

    var icon: String {
        switch self {
        case .general: "gearshape"
        case .playback: "play.circle"
        case .files: "folder"
        case .advanced: "gearshape.2"
        }
    }
}

struct SettingsView: View {
    @EnvironmentObject var settingsStore: SettingsStore

    @State private var pane: SettingsPane = .general
    @State private var checking = false
    @State private var updateStatus = ""
    @State private var pendingRelease: UpdateChecker.Release?

    var body: some View {
        VStack(spacing: 0) {
            Text(pane.title)
                .font(.headline)
                .padding(.top, 10)
            HStack(spacing: 2) {
                ForEach(SettingsPane.allCases) { item in
                    Button {
                        pane = item
                    } label: {
                        VStack(spacing: 3) {
                            Image(systemName: item.icon)
                                .font(.system(size: 22))
                            Text(item.title)
                                .font(.callout)
                        }
                        .foregroundStyle(pane == item ? Color.accentColor : Color.secondary)
                        .frame(width: 92, height: 56)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(pane == item ? Color.primary.opacity(0.12) : .clear)
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.vertical, 8)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    switch pane {
                    case .general: generalPane
                    case .playback: playbackPane
                    case .files: filesPane
                    case .advanced: advancedPane
                    }
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
            }
            .frame(width: 660)
            .frame(minHeight: 380)
        }
    }

    // MARK: - Panes

    private var generalPane: some View {
        Group {
            PrefGroup {
                PrefRow("Version:") {
                    Text(UpdateChecker.currentVersion.map { "Music Speed Changer \($0)" }
                         ?? "Music Speed Changer (development build)")
                        .foregroundStyle(.secondary)
                }
            }
            PrefGroup {
                PrefRow("Updates:") {
                    Toggle("Automatically check for updates on startup",
                           isOn: binding(\.autoCheckUpdates))
                    Toggle("Include beta (pre-release) versions",
                           isOn: binding(\.includeBetaUpdates))
                    PrefNote("When on, the app checks this project's GitHub releases on launch, betas included.")
                }
                PrefRow("Discord:") {
                    Toggle("Show listening activity in Discord (Rich Presence)",
                           isOn: binding(\.discordRPCEnabled))
                    PrefNote("Track name, tempo, and pitch appear on your Discord profile while music is loaded. Needs the Discord desktop app running.")
                }
                PrefRow("Feed:") {
                    TextField("GitHub Releases API URL", text: binding(\.updateFeedUrl))
                    HStack {
                        Button(checking ? "Checking…" : "Check now") { checkNow() }
                            .disabled(checking)
                        if pendingRelease != nil {
                            Button("View Release") { viewRelease() }
                        }
                    }
                    if !updateStatus.isEmpty {
                        Text(updateStatus)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                    }
                }
            }
            PrefGroup {
                PrefRow("Appearance:") {
                    Toggle("Use the system accent color", isOn: binding(\.useSystemAccent))
                }
                PrefRow("Accent:") {
                    HStack {
                        ColorPicker("Custom accent (when above is off)",
                                    selection: accentBinding(),
                                    supportsOpacity: false)
                            .labelsHidden()
                            .disabled(settingsStore.settings.useSystemAccent)
                        Text(settingsStore.settings.customAccentHex)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .monospaced()
                    }
                    PrefNote("Applies to transport highlights, selections, and the EQ curve.")
                }
            }
        }
    }

    private var playbackPane: some View {
        Group {
            PrefGroup {
                PrefRow("Defaults:") {
                    Stepper("Default tempo: \(Int(settingsStore.settings.defaultTempoPercent))%",
                            value: binding(\.defaultTempoPercent), in: 25...300, step: 5)
                    Stepper("Default pitch: \(settingsStore.settings.defaultPitchSemitones, specifier: "%.1f") st",
                            value: binding(\.defaultPitchSemitones), in: -12...12, step: 0.5)
                }
                PrefRow("") {
                    Toggle("Reset to these defaults when a file is opened",
                           isOn: binding(\.applyDefaultsOnFileLoad))
                    PrefNote("Only applies when effects are not being remembered below.")
                }
            }
            PrefGroup {
                PrefRow("Sliders:") {
                    Stepper("Tempo step: \(settingsStore.settings.tempoSliderStep, specifier: "%.1f")",
                            value: binding(\.tempoSliderStep), in: 0.5...10, step: 0.5)
                    Stepper("Pitch step: \(settingsStore.settings.pitchSliderStep, specifier: "%.1f")",
                            value: binding(\.pitchSliderStep), in: 0.1...1, step: 0.1)
                    PrefNote("How far each slider moves per tick.")
                }
            }
        }
    }

    private var filesPane: some View {
        PrefGroup {
            PrefRow("Library:") {
                Toggle("Remember the file list between sessions",
                       isOn: binding(\.rememberFileList))
                PrefNote("Open files reappear in the Files panel on startup, and the last file reloads automatically.")
            }
        }
    }

    private var advancedPane: some View {
        Group {
            PrefGroup {
                PrefRow("Panels:") {
                    Toggle("Show Tempo panel", isOn: binding(\.showTempoPanel))
                    Toggle("Show Pitch panel", isOn: binding(\.showPitchPanel))
                    Toggle("Show AB Loop panel", isOn: binding(\.showLoopPanel))
                }
            }
            PrefGroup {
                PrefRow("Waveform:") {
                    Stepper("Detail (bars): \(settingsStore.settings.waveformPeaks)",
                            value: binding(\.waveformPeaks), in: 100...8000, step: 100)
                    PrefNote("Applies to the next file you open.")
                    Toggle("Click / drag the waveform to seek", isOn: binding(\.clickToSeek))
                }
            }
            PrefGroup {
                PrefRow("Effects:") {
                    Toggle("Save effects and restore them on startup",
                           isOn: binding(\.rememberEffects))
                    PrefNote("Tempo, pitch, volume, and EQ. When off, newly opened files use the Playback defaults instead.")
                }
            }
        }
    }

    // MARK: - Helpers

    private func binding<Value>(_ keyPath: ReferenceWritableKeyPath<AppSettings, Value>) -> Binding<Value> {
        Binding(get: { settingsStore.settings[keyPath: keyPath] },
                set: { settingsStore.settings[keyPath: keyPath] = $0; settingsStore.persist() })
    }

    private func accentBinding() -> Binding<Color> {
        Binding(get: { Color(hex: settingsStore.settings.customAccentHex) },
                set: {
                    settingsStore.settings.customAccentHex = $0.toHex()
                    settingsStore.persist()
                })
    }

    private func checkNow() {
        guard !checking else { return }
        checking = true
        pendingRelease = nil
        updateStatus = "Checking…"
        let feed = settingsStore.settings.updateFeedUrl
        let includeBeta = settingsStore.settings.includeBetaUpdates
        Task {
            let outcome = await UpdateChecker.check(feed: feed, includeBeta: includeBeta)
            switch outcome {
            case .upToDate(let current):
                updateStatus = "You're up to date (\(current))."
            case .available(let release):
                pendingRelease = release
                updateStatus = "Version \(release.version)\(release.prerelease ? " (beta)" : "") is available."
            case .failed(let message):
                updateStatus = "Check failed: \(message)"
            }
            checking = false
        }
    }

    private func viewRelease() {
        guard let html = pendingRelease?.htmlURL, let url = URL(string: html) else { return }
        NSWorkspace.shared.open(url)
    }
}

// MARK: - Apple Music style rows

/// One labeled row: right-aligned label on the left, controls on the right.
struct PrefRow<Content: View>: View {
    let label: String
    @ViewBuilder let content: () -> Content

    init(_ label: String, @ViewBuilder content: @escaping () -> Content) {
        self.label = label
        self.content = content
    }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Text(label)
                .frame(width: 120, alignment: .trailing)
                .foregroundStyle(.primary)
            VStack(alignment: .leading, spacing: 6) {
                content()
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 8)
    }
}

/// A group of rows separated from the next group by a divider.
struct PrefGroup<Content: View>: View {
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            content()
        }
        Divider()
            .padding(.vertical, 4)
    }
}

/// Small explanatory text under a control.
struct PrefNote: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.callout)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: Hex color conversion

extension Color {
    init(hex: String) {
        var h = hex.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if h.hasPrefix("#") { h.removeFirst() }
        var r = 0.46, g = 0.49, b = 0.2 // fallback: #2E7D32-ish green
        if h.count == 6, let v = UInt32(h, radix: 16) {
            r = Double((v >> 16) & 0xFF) / 255
            g = Double((v >> 8) & 0xFF) / 255
            b = Double(v & 0xFF) / 255
        }
        self.init(red: r, green: g, blue: b)
    }

    func toHex() -> String {
        let ns = NSColor(self).usingColorSpace(.sRGB) ?? NSColor.green
        let r = Int((ns.redComponent * 255).rounded())
        let g = Int((ns.greenComponent * 255).rounded())
        let b = Int((ns.blueComponent * 255).rounded())
        return String(format: "#%02X%02X%02X", r, g, b)
    }
}

extension View {
    /// Applies a custom tint, or leaves the system accent alone when nil.
    @ViewBuilder
    func optionalTint(_ color: Color?) -> some View {
        if let color {
            self.tint(color)
        } else {
            self
        }
    }
}
