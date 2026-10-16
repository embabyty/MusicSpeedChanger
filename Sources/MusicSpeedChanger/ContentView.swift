import SwiftUI
import MSCCore
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject var engine: AudioEngine
    @EnvironmentObject var tracks: TrackList
    @EnvironmentObject var settings: SettingsStore

    @State private var showFiles = true
    @State private var exporting = false
    @State private var isEditingCards = false
    @State private var showCardGallery = false
    @State private var showSaveDialog = false
    @State private var saveLoopOnly = false
    @State private var saveRepeat = 1
    @State private var saveFileName = ""
    @State private var saveFormat: AudioExportFormat = .wav
    @State private var errorMessage: String?
    @State private var exportProgress = 0.0
    @State private var exportFileName = ""
    @State private var showExportProgress = false
    @State private var exportTask: Task<Void, Never>?

    private var hasLoop: Bool { engine.loopIsValid }

    /// Controls shown in the Player for the active preset, in order.
    private var activeControls: [PlayerControl] {
        settings.settings.editorPreset == .custom
            ? settings.settings.customControls
            : settings.settings.editorPreset.includedControls
    }

    var body: some View {
        NavigationSplitView {
            FinderSidebar(
                selection: Binding(
                    get: { settings.settings.editorPreset },
                    set: { settings.settings.editorPreset = $0; settings.persist() }),
                presetsEnabled: !tracks.tracks.isEmpty)
                .navigationSplitViewColumnWidth(min: 230, ideal: 260, max: 330)
        } detail: {
            PlayerView(onStep: stepTrack, onOpen: openPanel,
                       onExport: beginExport, onToggleFiles: toggleFiles,
                       exporting: exporting,
                       visibleControls: activeControls,
                       isEditingCards: isEditingCards,
                       cardSize: { settings.settings.cardSize(for: $0) },
                       onRemoveControl: removeControl,
                       onSetCardSize: { setCardSize($1, for: $0) },
                       onMoveControl: moveControl,
                       onOpenGallery: { showCardGallery = true },
                       onRequestEdit: { isEditingCards = true },
                       onDoneEditing: { isEditingCards = false },
                       onDropNewCard: { addControl($0, size: $1) })
                .modifier(LibraryToolbar(exporting: exporting, onOpen: openPanel,
                                          onExport: beginExport, onToggleFiles: toggleFiles,
                                          isEditingCards: isEditingCards,
                                          onToggleEdit: { isEditingCards.toggle() },
                                          onOpenGallery: { showCardGallery = true }))
        }
        .inspector(isPresented: $showFiles) {
            FilesView(onSelect: loadTrack)
        }
        .animation(.easeInOut(duration: 0.25), value: showFiles)
        .optionalTint(settings.settings.useSystemAccent
                      ? nil : Color(hex: settings.settings.customAccentHex))
        .sheet(isPresented: $showCardGallery) {
            CardGalleryView(
                includedControls: activeControls,
                sizeFor: { settings.settings.cardSize(for: $0) },
                onAdd: { addControl($0, size: $1) },
                onRemove: removeControl)
        }
        .onReceive(NotificationCenter.default.publisher(for: .showConfigureCustomPresets)) { _ in
            isEditingCards = true
            showCardGallery = true
        }
        .onChange(of: tracks.tracks.isEmpty) { _, empty in
            // With no files, fall back to Default so a stale preset can't
            // leave the Player showing an empty/disabled layout.
            if empty, settings.settings.editorPreset != .default {
                settings.settings.editorPreset = .default
                settings.persist()
            }
        }
        .sheet(isPresented: $showSaveDialog) {
            SaveDialogSheet(fileName: $saveFileName, loopOnly: $saveLoopOnly,
                            loopRepeat: $saveRepeat, format: $saveFormat,
                            hasLoop: hasLoop) { startExport() }
                .frame(width: 460)
                .padding()
        }
        .sheet(isPresented: $showExportProgress) {
            VStack(spacing: 12) {
                Text("Saving Audio").font(.title2.weight(.semibold))
                Text(exportFileName)
                    .foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                ProgressView(value: exportProgress)
                    .progressViewStyle(.linear)
                Text("\(Int((exportProgress * 100).rounded()))%")
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                HStack {
                    Spacer()
                    Button("Cancel") { cancelExport() }
                }
            }
            .frame(width: 380)
            .padding()
            .interactiveDismissDisabled()
        }
        .alert("Error", isPresented: .constant(errorMessage != nil)) {
            Button("OK") { errorMessage = nil }
        } message: { Text(errorMessage ?? "") }
        .onChange(of: tracks.tracks.map(\.url)) { _, new in
            settings.tracksPathCache = new.map(\.path)
        }
        .onAppear {
            applyStartupEffects()
            NSApp.activate(ignoringOtherApps: true)
            engine.onTrackEnded = { handleTrackEnded() }
            // Always have something loaded: resume the last file (or the
            // first remembered one) instead of idling with an empty engine.
            if !engine.isLoaded, let track = launchTrack() {
                loadTrack(track)
            }
        }
        .task {
            // Pin the window to a usable size on the primary display: the
            // layout needs ~1220pt for sidebar + content + inspector, and a
            // restored frame can come back smaller (clipping both side columns).
            for _ in 0..<50 {
                let placed = await MainActor.run { () -> Bool in
                    guard let window = NSApp.keyWindow ?? NSApp.windows.first(where: { $0.isVisible }),
                          let screen = NSScreen.screens.first
                    else { return false }
                    let minSize = NSSize(width: 1220, height: 800)
                    window.minSize = minSize
                    var frame = window.frame
                    if frame.width < minSize.width || frame.height < minSize.height {
                        let size = NSSize(width: max(frame.width, minSize.width),
                                          height: max(frame.height, minSize.height))
                        frame = NSRect(
                            x: screen.frame.midX - size.width / 2,
                            y: screen.frame.midY - size.height / 2,
                            width: size.width, height: size.height)
                        window.setFrame(frame, display: true)
                    } else {
                        window.setFrameOrigin(NSPoint(
                            x: screen.frame.midX - frame.width / 2,
                            y: screen.frame.midY - frame.height / 2))
                    }
                    window.makeKeyAndOrderFront(nil)
                    return true
                }
                if placed { break }
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
    }

    private func applyStartupEffects() {
        engine.shuffleEnabled = settings.settings.shuffleEnabled
        engine.repeatMode = settings.settings.repeatMode
        if settings.settings.rememberEffects {
            engine.tempoPercent = settings.settings.lastTempoPercent
            engine.pitchSemitones = settings.settings.lastPitchSemitones
            engine.volumePercent = settings.settings.lastVolumePercent
            if let gains = settings.settings.lastEqGains { engine.eqGains = gains }
            engine.eqEnabled = settings.settings.lastEqEnabled
            engine.reverbEnabled = settings.settings.lastReverbEnabled
            engine.reverbMix = settings.settings.lastReverbMix
            engine.delayEnabled = settings.settings.lastDelayEnabled
            engine.delayMix = settings.settings.lastDelayMix
            engine.delayTime = settings.settings.lastDelayTime
            engine.delayFeedback = settings.settings.lastDelayFeedback
            engine.distortionEnabled = settings.settings.lastDistortionEnabled
            engine.distortionMix = settings.settings.lastDistortionMix
            engine.distortionGain = settings.settings.lastDistortionGain
        } else if settings.settings.applyDefaultsOnFileLoad {
            engine.tempoPercent = settings.settings.defaultTempoPercent
            engine.pitchSemitones = settings.settings.defaultPitchSemitones
        }
    }

    // MARK: Actions

    func loadTrack(_ track: TrackItem) {
        // Selecting the loaded row (or double-loading) is a no-op.
        if engine.isLoaded, engine.fileURL == track.url { return }
        do {
            try engine.load(url: track.url)
            tracks.selectedID = track.id
            settings.settings.lastFilePath = track.url.path
            settings.persist()
            if settings.settings.applyDefaultsOnFileLoad && !settings.settings.rememberEffects {
                engine.tempoPercent = settings.settings.defaultTempoPercent
                engine.pitchSemitones = settings.settings.defaultPitchSemitones
            }
            // Waveform peaks are decoded off the main thread (applies to this file).
            engine.waveform = nil
            let peaks = settings.settings.waveformPeaks
            let url = track.url
            Task {
                let data = await Task.detached(priority: .userInitiated) {
                    WaveformData.fromFile(url, bucketCount: peaks)
                }.value
                engine.waveform = data
            }
        } catch {
            errorMessage = "Could not load \(track.name):\n\(error.localizedDescription)"
        }
    }

    private func toggleFiles() { withAnimation { showFiles.toggle() } }

    // MARK: - Card editing (widget-style)

    /// Copies the active preset into Custom on first mutation, so edits
    /// never clobber the built-in presets.
    private func ensureCustomForEdit() {
        if settings.settings.editorPreset != .custom {
            let current = activeControls
            settings.settings.editorPreset = .custom
            settings.settings.customControls = current
        }
    }

    private func addControl(_ control: PlayerControl, size: CardSize) {
        ensureCustomForEdit()
        if !settings.settings.customControls.contains(control) {
            settings.settings.customControls.append(control)
        }
        settings.settings.setCardSize(size, for: control)
        settings.persist()
    }

    private func removeControl(_ control: PlayerControl) {
        ensureCustomForEdit()
        settings.settings.customControls.removeAll(where: { $0 == control })
        if settings.settings.customControls.isEmpty {
            settings.settings.customControls = PlayerControl.defaultCustom
        }
        settings.persist()
    }

    private func setCardSize(_ size: CardSize, for control: PlayerControl) {
        ensureCustomForEdit()
        settings.settings.setCardSize(size, for: control)
        settings.persist()
    }

    private func moveControl(from offsets: IndexSet, to destination: Int) {
        ensureCustomForEdit()
        withAnimation(.spring(response: 0.35, dampingFraction: 0.8)) {
            settings.settings.customControls.move(fromOffsets: offsets, toOffset: destination)
            settings.persist()
        }
    }

    /// Track to auto-load at launch: the last-played file when it is still
    /// in the list, otherwise the first remembered file.
    private func launchTrack() -> TrackItem? {
        let list = tracks.tracks
        guard !list.isEmpty else { return nil }
        if let last = settings.settings.lastFilePath,
           let match = list.first(where: { $0.url.path == last })
        {
            return match
        }
        return list.first
    }

    private func stepTrack(_ direction: Int) {
        if direction > 0, engine.shuffleEnabled, tracks.tracks.count > 1 {
            playRandomTrack()
            return
        }
        guard let track = tracks.peek(direction, wrapping: engine.repeatMode == .all) else { return }
        loadTrack(track)
        engine.play()
    }

    /// Natural end of track: repeat-one replays, repeat-all advances
    /// (wrapping the queue), otherwise playback stays stopped.
    private func handleTrackEnded() {
        if engine.repeatMode == .one {
            engine.seek(to: engine.reverseEnabled ? engine.duration : 0)
            engine.play()
            return
        }
        guard !tracks.tracks.isEmpty else { return }
        if engine.shuffleEnabled, tracks.tracks.count > 1 {
            playRandomTrack()
            return
        }
        let current = tracks.selectedID.flatMap { id in
            tracks.tracks.firstIndex(where: { $0.id == id })
        } ?? (tracks.tracks.count - 1)
        let next = current + 1
        if next < tracks.tracks.count {
            loadTrack(tracks.tracks[next])
            engine.play()
        } else if engine.repeatMode == .all, let first = tracks.tracks.first {
            loadTrack(first)
            engine.play()
        }
    }

    private func playRandomTrack() {
        let list = tracks.tracks
        guard list.count > 1 else { return }
        let current = tracks.selectedID.flatMap { id in list.firstIndex(where: { $0.id == id }) }
        var idx = Int.random(in: 0..<list.count)
        while idx == current { idx = Int.random(in: 0..<list.count) }
        loadTrack(list[idx])
        engine.play()
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [.audio]
        if panel.runModal() == .OK {
            tracks.add(urls: panel.urls)
            if let first = panel.urls.first { loadTrack(TrackItem(url: first)) }
        }
    }

    private func beginExport() {
        guard engine.isLoaded else { return }
        let pitch = engine.pitchSemitones
        let suggested = engine.fileName.map { name in
            let base = (name as NSString).deletingPathExtension
            return "\(base)_\(Int(engine.tempoPercent))pct_\(pitch >= 0 ? "+" : "")\(String(format: "%.1f", pitch))st"
                + (engine.eqEnabled && !engine.eqIsFlat ? "_eq" : "")
                + (engine.effectsActive ? "_fx" : "")
        } ?? "track"
        saveFileName = SaveDialogSheet.sanitizeFileName(suggested)
        saveLoopOnly = hasLoop
        saveRepeat = 1
        showSaveDialog = true
    }

    private func startExport() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [saveContentType]
        panel.nameFieldStringValue = "\((saveFileName as NSString).deletingPathExtension).\(saveFormat.fileExtension)"
        panel.isExtensionHidden = false
        panel.directoryURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Music")
        guard panel.runModal() == .OK, let url = panel.url else { return }

        exporting = true
        let wasPlaying = engine.isPlaying
        if wasPlaying { engine.pause() }

        exportFileName = url.lastPathComponent
        exportProgress = 0
        showExportProgress = true
        let tempo = engine.tempoPercent
        let pitch = engine.pitchSemitones
        let loopOnly = saveLoopOnly && hasLoop
        let repeats = saveRepeat
        exportTask = Task {
            do {
                try await engine.exportAudio(to: url, format: saveFormat,
                                             tempoPercent: tempo, pitchSemitones: pitch,
                                             loopOnly: loopOnly, loopRepeat: repeats) { p in
                    exportProgress = p
                }
            } catch let error as NSError where error.code == 999 {
                // Cancelled: remove the partial file, stay silent.
                try? FileManager.default.removeItem(at: url)
            } catch {
                try? FileManager.default.removeItem(at: url)
                errorMessage = "Save failed:\n\(error.localizedDescription)"
            }
            showExportProgress = false
            exporting = false
            if wasPlaying { engine.play() }
        }
    }

    private func cancelExport() {
        engine.exportCancelled = true
        exportTask?.cancel()
    }

    private var saveContentType: UTType {
        switch saveFormat {
        case .wav: .wav
        case .m4a: .mpeg4Audio
        }
    }
}

// MARK: Shared window toolbar (Open / Export), shown on every tab.
struct LibraryToolbar: ViewModifier {
    @EnvironmentObject var engine: AudioEngine
    let exporting: Bool
    let onOpen: () -> Void
    let onExport: () -> Void
    let onToggleFiles: () -> Void
    var isEditingCards: Bool = false
    var onToggleEdit: (() -> Void)?
    var onOpenGallery: (() -> Void)?

    func body(content: Content) -> some View {
        content.toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Open", systemImage: "arrow.up.document", action: onOpen)
                    .help("Open audio file")
            }
            ToolbarItem(placement: .primaryAction) {
                Button("Export", systemImage: "square.and.arrow.down", action: onExport)
                    .disabled(!engine.isLoaded || exporting)
                    .help("Save edited audio")
            }
            ToolbarItem(placement: .primaryAction) {
                Button("Files", systemImage: "list.bullet", action: onToggleFiles)
                    .help("Show or hide the file list")
            }
            ToolbarItem(placement: .primaryAction) {
                Button(isEditingCards ? "Done" : "Edit Cards",
                       systemImage: isEditingCards ? "checkmark" : "square.grid.2x2") {
                    onToggleEdit?()
                }
                .help(isEditingCards ? "Finish editing cards" : "Edit player cards")
                // Right-clicking Edit Cards offers Add Cards (reveals +).
                .contextMenu {
                    Button("Add Cards…", systemImage: "plus") { onOpenGallery?() }
                }
            }
            if isEditingCards {
                ToolbarItem(placement: .primaryAction) {
                    Button("Add Cards", systemImage: "plus") { onOpenGallery?() }
                        .help("Add cards to the player")
                }
            }
        }
    }
}

// MARK: Save dialog sheet
struct SaveDialogSheet: View {
    @Binding var fileName: String
    @Binding var loopOnly: Bool
    @Binding var loopRepeat: Int
    @Binding var format: AudioExportFormat
    let hasLoop: Bool
    let onSave: () -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Save Edited Audio").font(.title2.weight(.semibold))
            HStack {
                Text("New Filename")
                TextField("filename", text: $fileName)
            }
            Picker("Format", selection: $format) {
                ForEach(AudioExportFormat.allCases) { format in
                    Text(format.displayName).tag(format)
                }
            }
            .pickerStyle(.menu)
            Text("Loop Extraction").font(.headline).padding(.top, 8)
            Toggle("Save Loop Only", isOn: $loopOnly).disabled(!hasLoop)
            Stepper("Loop Repeat (iterations): \(loopRepeat)", value: $loopRepeat, in: 1...1000)
                .disabled(!hasLoop || !loopOnly)
            if !hasLoop {
                Text("Set an AB loop to enable loop extraction.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                Button("Save") { dismiss(); onSave() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding()
    }

    static func sanitizeFileName(_ name: String) -> String {
        let invalid = CharacterSet(charactersIn: "/\\?%*|\"<>:")
        let cleaned = name.components(separatedBy: invalid).joined(separator: "_")
        return cleaned.isEmpty ? "track" : cleaned
    }
}
