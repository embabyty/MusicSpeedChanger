import SwiftUI
import MSCCore
import UniformTypeIdentifiers

/// Player tab: waveform, time readout, glass transport, tempo/pitch/loop/EQ cards.
/// Which cards appear is driven by the selected Editor Mode preset.
/// Cards can be edited widget-style: jiggle mode with remove/resize/reorder
/// and a + gallery to add cards.
struct PlayerView: View {
    @EnvironmentObject var engine: AudioEngine
    @EnvironmentObject var settings: SettingsStore

    let onStep: (Int) -> Void
    let onOpen: () -> Void
    let onExport: () -> Void
    let onToggleFiles: () -> Void
    let exporting: Bool
    /// Ordered controls for the active preset (custom order when .custom).
    var visibleControls: [PlayerControl] = EditorPreset.default.includedControls

    /// Widget-style editing state (owned by ContentView).
    var isEditingCards: Bool = false
    var cardSize: (PlayerControl) -> CardSize = { $0.defaultCardSize }
    var onRemoveControl: ((PlayerControl) -> Void)?
    var onSetCardSize: ((PlayerControl, CardSize) -> Void)?
    var onMoveControl: ((IndexSet, Int) -> Void)?
    var onOpenGallery: (() -> Void)?
    var onRequestEdit: (() -> Void)?
    var onDoneEditing: (() -> Void)?
    /// Gallery drag payloads ("card:<control>:<size>") dropped on the grid.
    var onDropNewCard: ((PlayerControl, CardSize) -> Void)?

    @State private var seekDragging = false
    @State private var seekValue: Double = 0
    /// Card currently hovered as a reorder drop target, and the card being
    /// dragged. Both drive the drag-and-drop highlight + dimming.
    @State private var dropTarget: PlayerControl?
    @State private var draggingControl: PlayerControl?
    /// Waveform zoom level 0…10, mirroring the iOS app (5 = natural scale).
    @State private var zoomLevel = 5

    var body: some View {
        ZStack(alignment: .topTrailing) {
            ScrollView {
                VStack(spacing: 12) {
                    cardGrid
                        // Cap the content width so cards keep readable
                        // proportions on wide windows instead of stretching.
                        .frame(maxWidth: 980)
                        .frame(maxWidth: .infinity)
                }
                .padding()
                .padding(.bottom, 140) // clearance so content slides under the floating bar
                .onDrop(of: [.text], delegate: CardGalleryDropDelegate(onDrop: { control, size in
                    onDropNewCard?(control, size)
                }))
            }
            .navigationTitle(engine.fileName ?? "Now Playing")
            .contextMenu {
                if isEditingCards {
                    Button("Add Cards…", systemImage: "plus") { onOpenGallery?() }
                    Divider()
                    Button("Done", systemImage: "checkmark") { onDoneEditing?() }
                } else {
                    Button("Edit Cards", systemImage: "square.grid.2x2") { onRequestEdit?() }
                }
            }
            // Top-right + button, like the macOS desktop in jiggle mode.
            if isEditingCards {
                HStack(spacing: 8) {
                    Button { onOpenGallery?() } label: {
                        Image(systemName: "plus")
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(.primary)
                            .frame(width: 30, height: 30)
                            .background(.quaternary.opacity(0.9), in: Circle())
                    }
                    .buttonStyle(.plain)
                    .help("Add cards")
                    Button("Done") { onDoneEditing?() }
                        .buttonStyle(.glass)
                }
                .padding(12)
            }
        }
        .onChange(of: engine.reverseEnabled) { _, _ in
            // Restart so the direction change takes effect immediately.
            // Loop state is preserved: a valid loop keeps looping, backwards.
            if engine.isPlaying { engine.play() }
        }
        .overlay(alignment: .bottom) {
            floatingTransport
                .padding(.bottom, 14)
        }
    }

    private var timeRow: some View {
        HStack {
            Text(TrackItem.formatClock(seekDragging ? seekValue : engine.position))
                .frame(width: 70, alignment: .leading)
                .monospacedDigit()
            Slider(value: Binding(
                get: { seekDragging ? seekValue : engine.position },
                set: { seekValue = $0; seekDragging = true }),
                   in: 0...max(engine.duration, 0.001)) { _ in
                seekDragging = false
                engine.seek(to: seekValue)
            }
            .disabled(!engine.isLoaded)
            Text(TrackItem.formatClock(engine.duration))
                .frame(width: 70, alignment: .trailing)
                .monospacedDigit()
            Text(engine.effectiveDurationText)
                .foregroundStyle(.secondary)
                .frame(minWidth: 140, alignment: .leading)
        }
    }

    /// Floating glass control bar, Apple Music style: scrubber on top,
    /// playback buttons and volume below. Hovers over the content
    /// instead of sitting in the layout flow.
    private var floatingTransport: some View {
        VStack(spacing: 4) {
            timeRow
            HStack(spacing: 16) {
                HStack(spacing: 22) {
                Button {
                    engine.shuffleEnabled.toggle()
                    settings.settings.shuffleEnabled = engine.shuffleEnabled
                    settings.persist()
                } label: {
                    Image(systemName: "shuffle")
                }
                .foregroundStyle(engine.shuffleEnabled ? Color.accentColor : Color.primary)
                .help("Shuffle")
                Button {
                    onStep(-1)
                } label: {
                    Image(systemName: "backward.fill")
                }
                .help("Previous file")
                Button {
                    engine.stop()
                } label: {
                    Image(systemName: "stop.fill")
                }
                .help("Stop")
                Button {
                    if engine.isPlaying { engine.pause() } else { engine.play() }
                } label: {
                    Image(systemName: engine.isPlaying ? "pause.fill" : "play.fill")
                }
                .help(engine.isPlaying ? "Pause" : "Play")
                Button {
                    onStep(1)
                } label: {
                    Image(systemName: "forward.fill")
                }
                .help("Next file")
                if has(.reverse) {
                    Button {
                        engine.reverseEnabled.toggle()
                    } label: {
                        Image(systemName: "arrow.uturn.backward")
                    }
                    .foregroundStyle(engine.reverseEnabled ? .red : .primary)
                    .help("Play backwards from the current position")
                }
                Button {
                    engine.repeatMode = engine.repeatMode.next
                    settings.settings.repeatMode = engine.repeatMode
                    settings.persist()
                } label: {
                    Image(systemName: engine.repeatMode == .one ? "repeat.1" : "repeat")
                }
                .foregroundStyle(engine.repeatMode == .off ? Color.primary : Color.accentColor)
                .help("Repeat: \(engine.repeatMode == .one ? "one" : engine.repeatMode == .all ? "all" : "off")")
                .onChange(of: engine.reverseEnabled) { _, on in
                    if on { engine.loopEnabled = false }
                }
            }
            .buttonStyle(.plain)
            .font(.system(size: 16))
            Divider().frame(height: 20)
            if has(.volume) {
                HStack {
                    Image(systemName: "speaker.wave.2")
                    Slider(value: $engine.volumePercent, in: 0...100).frame(width: 120)
                }
            }
            }
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 10)
        .frame(minWidth: 440, maxWidth: 620)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 16))
        .shadow(color: .black.opacity(0.4), radius: 16, y: 6)
        .disabled(!engine.isLoaded)
        .opacity(engine.isLoaded ? 1 : 0.35)
    }

    private var tempoCard: some View {
        Card("Tempo (speed, keeps pitch)", content: {
            HStack {
                Slider(value: $engine.tempoPercent, in: 50...200, step: settings.settings.tempoSliderStep)
                Text(String(format: "%.0f%%", engine.tempoPercent))
                    .frame(width: 64, alignment: .trailing).fontWeight(.semibold)
                    .monospacedDigit()
            }
            .disabled(!engine.isLoaded)
            TypablePresetPicker(
                title: "Tempo percent", value: $engine.tempoPercent,
                range: 50...200,
                format: { String(format: "%.0f%%", $0) },
                parse: Self.parseTempo,
                presets: [("0.5x", 50), ("0.75x", 75), ("1x", 100),
                          ("1.25x", 125), ("1.5x", 150), ("2x", 200)])
                .disabled(!engine.isLoaded)
        }, trailing: {
            Button {
                engine.tempoPercent = 100
            } label: {
                Image(systemName: "arrow.trianglehead.counterclockwise")
            }
            .buttonStyle(.glass)
            .disabled(!engine.isLoaded)
            .help("Reset tempo")
        })
    }

    private var pitchCard: some View {
        Card("Pitch (key, keeps tempo)", content: {
            HStack {
                Slider(value: $engine.pitchSemitones, in: -12...12, step: settings.settings.pitchSliderStep)
                Text(String(format: "%+.1f st", engine.pitchSemitones))
                    .frame(width: 64, alignment: .trailing).fontWeight(.semibold)
                    .monospacedDigit()
            }
            .disabled(!engine.isLoaded)
            TypablePresetPicker(
                title: "Pitch semitones", value: $engine.pitchSemitones,
                range: -12...12,
                format: { String(format: "%+.1f st", $0) },
                parse: Self.parsePitch,
                presets: [("-5", -5), ("-2", -2), ("0", 0), ("+2", 2), ("+5", 5)])
                .disabled(!engine.isLoaded)
        }, trailing: {
            Button {
                engine.pitchSemitones = 0
            } label: {
                Image(systemName: "arrow.trianglehead.counterclockwise")
            }
            .buttonStyle(.glass)
            .disabled(!engine.isLoaded)
            .help("Reset pitch")
        })
    }

    // MARK: - Widget-style card grid

    private func has(_ control: PlayerControl) -> Bool {
        visibleControls.contains(control)
    }

    /// Whether a control renders at all: panel toggles apply in normal
    /// mode, and jiggle mode shows everything else so it can be edited.
    private func shouldShow(_ control: PlayerControl) -> Bool {
        guard has(control) else { return false }
        if isEditingCards { return true }
        switch control {
        case .tempo: return settings.settings.showTempoPanel
        case .pitch: return settings.settings.showPitchPanel
        case .loop: return settings.settings.showLoopPanel
        case .equalizer: return settings.settings.showEqPanel
        default: return true
        }
    }

    private var displayedControls: [PlayerControl] {
        visibleControls.filter(shouldShow)
    }

    /// Span units (S=1, M=2, L=4) parallel to `displayedControls`.
    private var cardSpans: [Int] {
        displayedControls.map { min(max(cardSize($0).span, 1), 4) }
    }

    private var cardGrid: some View {
        CardSpanLayout(spans: cardSpans, spacing: 12) {
            ForEach(displayedControls) { control in
                cardCell(for: control)
            }
        }
        // Spring the other cards into place on reorder, add, remove, resize.
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: visibleControls)
        .animation(.spring(response: 0.35, dampingFraction: 0.8), value: cardSpans)
    }

    @ViewBuilder
    private func cardCell(for control: PlayerControl) -> some View {
        let size = cardSize(control)
        switch control {
        case .waveform:
            editableChrome(control: control, size: size) { waveformCell }
        case .tempo:
            editableChrome(control: control, size: size) { tempoCard }
        case .pitch:
            editableChrome(control: control, size: size) { pitchCard }
        case .loop:
            editableChrome(control: control, size: size) { loopCard }
        case .equalizer:
            editableChrome(control: control, size: size) { EqualizerCard() }
        case .reverse:
            editableChrome(control: control, size: size) { reverseCard }
        case .volume:
            editableChrome(control: control, size: size) { volumeCard }
        case .reverb:
            editableChrome(control: control, size: size) { reverbCard }
        case .delay:
            editableChrome(control: control, size: size) { delayCard }
        case .distortion:
            editableChrome(control: control, size: size) { distortionCard }
        default:
            editableChrome(control: control, size: size) { soonCard(for: control) }
        }
    }

    /// Wraps a card with jiggle, a remove badge, S/M/L resize and
    /// drag-to-reorder while editing. Pass-through otherwise.
    private func editableChrome<Content: View>(
        control: PlayerControl, size: CardSize,
        @ViewBuilder content: () -> Content
    ) -> some View {
        ZStack(alignment: .topLeading) {
            VStack(spacing: 6) {
                content()
                    .jiggle(enabled: isEditingCards)
                    .overlay {
                        if isEditingCards {
                            if dropTarget == control {
                                // Drop landing spot: solid accent ring + glow.
                                RoundedRectangle(cornerRadius: 12)
                                    .stroke(Color.accentColor, lineWidth: 2.5)
                                    .shadow(color: .accentColor.opacity(0.45), radius: 8)
                            } else {
                                RoundedRectangle(cornerRadius: 12)
                                    .stroke(Color.accentColor.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [6, 4]))
                            }
                        }
                    }
                    .opacity(draggingControl == control ? 0.4 : 1)
                if isEditingCards {
                    HStack(spacing: 4) {
                        Button { move(control, by: -1) } label: {
                            Image(systemName: "chevron.left")
                        }
                        .buttonStyle(.plain)
                        .disabled(isFirst(control))
                        .help("Move left")
                        Button { move(control, by: 1) } label: {
                            Image(systemName: "chevron.right")
                        }
                        .buttonStyle(.plain)
                        .disabled(isLast(control))
                        .help("Move right")
                        Spacer(minLength: 4)
                        Picker("Size", selection: Binding(
                            get: { size },
                            set: { onSetCardSize?(control, $0) })) {
                            ForEach(CardSize.allCases, id: \.self) { s in
                                Text(s.shortTitle).tag(s)
                            }
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                        .frame(width: 110)
                        .help("Card size: Small, Medium, or Large")
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 4)
                }
            }
            if isEditingCards {
                Button { onRemoveControl?(control) } label: {
                    Image(systemName: "minus.circle.fill")
                        .font(.system(size: 22))
                        .foregroundStyle(.red)
                        .background(Circle().fill(.white))
                }
                .buttonStyle(.plain)
                .help("Remove \(control.title)")
                .offset(x: -10, y: -10)
            }
        }
        .onDrag { NSItemProvider(object: control.rawValue as NSString) }
        .onDrop(of: [.text], delegate: CardReorderDropDelegate(
            target: control, controls: visibleControls,
            onMove: { offsets, dest in
                dropTarget = nil
                draggingControl = nil
                onMoveControl?(offsets, dest)
            },
            onHover: { dragged, target in
                draggingControl = dragged
                dropTarget = target
            },
            onExit: {
                dropTarget = nil
                draggingControl = nil
            }))
    }

    private func isFirst(_ control: PlayerControl) -> Bool {
        visibleControls.first == control
    }

    private func isLast(_ control: PlayerControl) -> Bool {
        visibleControls.last == control
    }

    private func move(_ control: PlayerControl, by offset: Int) {
        guard let from = visibleControls.firstIndex(of: control) else { return }
        let to = from + offset
        guard visibleControls.indices.contains(to) else { return }
        onMoveControl?(IndexSet(integer: from), offset > 0 ? to + 1 : to)
    }

    private var waveformCell: some View {
        ZStack(alignment: .topTrailing) {
            WaveformView(data: engine.waveform, duration: engine.duration,
                         position: engine.position,
                         loopA: engine.loopA, loopB: engine.loopB,
                         loopEnabled: engine.loopEnabled,
                         zoomLevel: zoomLevel) { seconds in
                if settings.settings.clickToSeek, engine.isLoaded {
                    engine.seek(to: seconds)
                }
            }
            .frame(minHeight: 220)
            .clipShape(RoundedRectangle(cornerRadius: 12))
            Menu {
                ForEach(0...10, id: \.self) { level in
                    Button {
                        zoomLevel = level
                    } label: {
                        if level == zoomLevel {
                            Label("Level \(level)", systemImage: "checkmark")
                        } else {
                            Text("Level \(level)")
                        }
                    }
                }
            } label: {
                Image(systemName: "magnifyingglass")
            }
            .labelStyle(.iconOnly)
            .buttonStyle(.glass)
            .menuStyle(.borderlessButton)
            .disabled(!engine.isLoaded || isEditingCards)
            .help("Waveform zoom level")
            .padding(10)
        }
    }

    private var reverseCard: some View {
        Card("Reverse") {
            HStack {
                Toggle("Play backwards", isOn: $engine.reverseEnabled)
                Spacer(minLength: 8)
                Image(systemName: "arrow.uturn.backward")
                    .foregroundStyle(engine.reverseEnabled ? .red : .secondary)
            }
        }
    }

    private var volumeCard: some View {
        Card("Volume") {
            HStack {
                Image(systemName: "speaker.wave.2")
                Slider(value: $engine.volumePercent, in: 0...100)
                Text(String(format: "%.0f", engine.volumePercent))
                    .frame(width: 36, alignment: .trailing)
                    .monospacedDigit()
            }
            .disabled(!engine.isLoaded || isEditingCards)
        }
    }

    private var reverbCard: some View {
        Card("Reverb (room)") {
            Toggle("Enable", isOn: $engine.reverbEnabled)
            HStack {
                Text("Mix")
                Slider(value: Binding(get: { Double(engine.reverbMix) },
                                      set: { engine.reverbMix = Float($0) }), in: 0...100)
                Text(String(format: "%.0f%%", engine.reverbMix))
                    .frame(width: 44, alignment: .trailing).monospacedDigit()
            }
            .disabled(!engine.reverbEnabled)
        }
        .disabled(!engine.isLoaded || isEditingCards)
    }

    private var delayCard: some View {
        Card("Delay (echo)") {
            Toggle("Enable", isOn: $engine.delayEnabled)
            HStack {
                Text("Mix")
                Slider(value: Binding(get: { Double(engine.delayMix) },
                                      set: { engine.delayMix = Float($0) }), in: 0...100)
                Text(String(format: "%.0f%%", engine.delayMix))
                    .frame(width: 44, alignment: .trailing).monospacedDigit()
            }
            HStack {
                Text("Time")
                Slider(value: $engine.delayTime, in: 0...2)
                Text(String(format: "%.2fs", engine.delayTime))
                    .frame(width: 52, alignment: .trailing).monospacedDigit()
            }
            HStack {
                Text("Echo")
                Slider(value: Binding(get: { Double(engine.delayFeedback) },
                                      set: { engine.delayFeedback = Float($0) }), in: 0...100)
                Text(String(format: "%.0f%%", engine.delayFeedback))
                    .frame(width: 44, alignment: .trailing).monospacedDigit()
            }
            .disabled(!engine.delayEnabled)
        }
        .disabled(!engine.isLoaded || isEditingCards)
    }

    private var distortionCard: some View {
        Card("Distortion (drive)") {
            Toggle("Enable", isOn: $engine.distortionEnabled)
            HStack {
                Text("Mix")
                Slider(value: Binding(get: { Double(engine.distortionMix) },
                                      set: { engine.distortionMix = Float($0) }), in: 0...100)
                Text(String(format: "%.0f%%", engine.distortionMix))
                    .frame(width: 44, alignment: .trailing).monospacedDigit()
            }
            HStack {
                Text("Drive")
                Slider(value: Binding(get: { Double(engine.distortionGain) },
                                      set: { engine.distortionGain = Float($0) }), in: -20...20)
                Text(String(format: "%+.0fdB", engine.distortionGain))
                    .frame(width: 52, alignment: .trailing).monospacedDigit()
            }
            .disabled(!engine.distortionEnabled)
        }
        .disabled(!engine.isLoaded || isEditingCards)
    }

    private func soonCard(for control: PlayerControl) -> some View {
        Card(control.title) {
            HStack(spacing: 8) {
                Image(systemName: control.icon)
                    .foregroundStyle(.secondary)
                    .frame(width: 22)
                Text(control.galleryBlurb)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Spacer(minLength: 8)
                Text("Mac soon")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(.quaternary.opacity(0.4), in: Capsule())
            }
        }
    }
    private static func parseTempo(_ raw: String) -> Double? {
        var s = raw.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "%", with: "")
        var scale = 1.0
        if s.hasSuffix("x") || s.hasSuffix("X") {
            s = String(s.dropLast())
            scale = 100
        }
        guard let v = Double(s.trimmingCharacters(in: .whitespaces)) else { return nil }
        return v * scale
    }

    /// "+2", "-2" or "2 st" → semitones.
    private static func parsePitch(_ raw: String) -> Double? {
        let s = raw.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "st", with: "")
            .trimmingCharacters(in: .whitespaces)
        return Double(s)
    }

    private var loopCard: some View {
        Card("AB Loop (practice sections)") {
            HStack(spacing: 14) {
                Spacer(minLength: 0)
                Button { engine.setLoopA() } label: {
                    Image(systemName: engine.loopA == nil ? "a.circle" : "a.circle.fill")
                        .font(.system(size: 24))
                        .foregroundStyle(engine.loopA == nil ? Color.primary : Color.accentColor)
                }
                .buttonStyle(.glass)
                .disabled(!engine.isLoaded)
                .help(engine.loopA == nil ? "Set loop start (A)" : "Loop start set — tap to move it")
                Button { engine.setLoopB() } label: {
                    Image(systemName: engine.loopB == nil ? "b.circle" : "b.circle.fill")
                        .font(.system(size: 24))
                        .foregroundStyle(engine.loopB == nil ? Color.primary : Color.accentColor)
                }
                .buttonStyle(.glass)
                .disabled(!engine.isLoaded)
                .help(engine.loopB == nil ? "Set loop end (B)" : "Loop end set — tap to move it")
                Button { engine.clearLoop() } label: {
                    Image(systemName: "x.circle")
                        .font(.system(size: 24))
                }
                .buttonStyle(.glass)
                .disabled(!engine.isLoaded || (engine.loopA == nil && engine.loopB == nil))
                .help("Clear loop")
                Group {
                    if let a = engine.loopA, let b = engine.loopB, b > a {
                        Text("A \(TrackItem.formatClock(a))   B \(TrackItem.formatClock(b))")
                    } else {
                        Text("No loop set")
                    }
                }
                .foregroundStyle(.yellow)
                .monospacedDigit()
                Spacer(minLength: 0)
            }
        }
    }
}

/// Type-to-set preset picker: an editable field (commits on Return or
/// focus loss, clamped to `range`) with a chevron menu of presets.
/// Invalid input reverts to the current value.
struct TypablePresetPicker: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: (Double) -> String
    let parse: (String) -> Double?
    let presets: [(label: String, value: Double)]

    @State private var draft: String?
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 2) {
            TextField(title, text: Binding(
                get: { draft ?? format(value) },
                set: { draft = $0 }))
                .focused($focused)
                .onSubmit(commit)
                .onChange(of: focused) { _, isFocused in if !isFocused { commit() } }
                .onChange(of: value) { _, _ in if !focused { draft = nil } }
                .frame(width: 84)
                .multilineTextAlignment(.trailing)
                .textFieldStyle(.roundedBorder)
            Menu {
                ForEach(presets, id: \.label) { preset in
                    Button(preset.label) {
                        value = preset.value
                        draft = nil
                    }
                }
            } label: {
                Image(systemName: "chevron.down")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(6)
            }
            .menuStyle(.borderlessButton)
            .help("Presets")
        }
    }

    private func commit() {
        defer { draft = nil }
        guard let raw = draft, !raw.isEmpty, let parsed = parse(raw) else { return }
        value = min(max(parsed, range.lowerBound), range.upperBound)
    }
}

/// Frosted card container used by the Now Playing tab, with an optional
/// top-right control (e.g. a reset button).
struct Card<Content: View, Trailing: View>: View {
    let title: String
    @ViewBuilder let content: () -> Content
    @ViewBuilder let trailing: () -> Trailing

    init(_ title: String, @ViewBuilder content: @escaping () -> Content,
         @ViewBuilder trailing: @escaping () -> Trailing) {
        self.title = title
        self.content = content
        self.trailing = trailing
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Spacer(minLength: 8)
                trailing()
            }
            content()
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
    }
}

extension Card where Trailing == EmptyView {
    init(_ title: String, @ViewBuilder content: @escaping () -> Content) {
        self.init(title, content: content, trailing: { EmptyView() })
    }
}

// MARK: - Jiggle + reorder helpers

/// 4-column span layout for player cards (S=1, M=2, L=4 units).
/// Unlike `Grid`, every cell width derives from the proposed container
/// width, so rows can never overflow the window: with default sizes this
/// matches the original layout (tempo/pitch halves, loop/EQ/waveform full).
struct CardSpanLayout: Layout {
    var spans: [Int]
    var spacing: CGFloat = 12
    var columns = 4

    private func cellWidth(span: Int, totalWidth: CGFloat) -> CGFloat {
        let unit = (totalWidth - CGFloat(columns - 1) * spacing) / CGFloat(columns)
        return unit * CGFloat(span) + spacing * CGFloat(span - 1)
    }

    private func pack(count: Int) -> [[Int]] {
        var rows: [[Int]] = [[]]
        var used = 0
        for i in 0..<count {
            let span = i < spans.count ? min(max(spans[i], 1), columns) : columns
            if used + span > columns {
                rows.append([])
                used = 0
            }
            rows[rows.count - 1].append(i)
            used += span
        }
        return rows.filter { !$0.isEmpty }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 800
        var height: CGFloat = 0
        let rows = pack(count: subviews.count)
        for (r, row) in rows.enumerated() {
            var rowHeight: CGFloat = 0
            for i in row {
                let span = i < spans.count ? min(max(spans[i], 1), columns) : columns
                let dims = subviews[i].dimensions(in: ProposedViewSize(
                    width: cellWidth(span: span, totalWidth: width), height: nil))
                rowHeight = max(rowHeight, dims.height)
            }
            height += rowHeight
            if r < rows.count - 1 { height += spacing }
        }
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let rows = pack(count: subviews.count)
        var y = bounds.minY
        for (r, row) in rows.enumerated() {
            var x = bounds.minX
            var rowHeight: CGFloat = 0
            for i in row {
                let span = i < spans.count ? min(max(spans[i], 1), columns) : columns
                let w = cellWidth(span: span, totalWidth: bounds.width)
                let dims = subviews[i].dimensions(in: ProposedViewSize(width: w, height: nil))
                rowHeight = max(rowHeight, dims.height)
            }
            for i in row {
                let span = i < spans.count ? min(max(spans[i], 1), columns) : columns
                let w = cellWidth(span: span, totalWidth: bounds.width)
                subviews[i].place(at: CGPoint(x: x, y: y),
                                  proposal: ProposedViewSize(width: w, height: rowHeight))
                x += w + spacing
            }
            y += rowHeight
            if r < rows.count - 1 { y += spacing }
        }
    }
}

extension View {
    /// macOS-widget-style jiggle while editing cards.
    @ViewBuilder
    func jiggle(enabled: Bool) -> some View {
        if enabled {
            self.phaseAnimator([false, true], content: { content, phase in
                content
                    .rotationEffect(.degrees(phase ? 0.7 : -0.7))
                    .offset(y: phase ? 0.6 : -0.6)
            }, animation: { _ in .easeInOut(duration: 0.14) })
        } else {
            self
        }
    }
}

/// Drag-to-reorder drop target for player cards. The drag payload is the
/// control's raw value as plain text; gallery drags ("card:<c>:<s>") are
/// ignored here — the grid-level CardGalleryDropDelegate handles those.
struct CardReorderDropDelegate: DropDelegate {
    let target: PlayerControl
    let controls: [PlayerControl]
    let onMove: (IndexSet, Int) -> Void
    /// Called when a reorder drag hovers this card (dragged, target).
    var onHover: ((PlayerControl, PlayerControl) -> Void)?
    /// Called when the drag leaves this card or is cancelled.
    var onExit: (() -> Void)?

    func validateDrop(info: DropInfo) -> Bool {
        info.hasItemsConforming(to: [.text])
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        DropProposal(operation: .move)
    }

    /// Resolve the dragged control from the payload, if it's a reorder drag.
    private func draggedControl(from info: DropInfo, completion: @escaping (PlayerControl?) -> Void) {
        guard let provider = info.itemProviders(for: [.text]).first else {
            completion(nil)
            return
        }
        _ = provider.loadObject(ofClass: NSString.self) { value, _ in
            guard let raw = (value as? NSString) as String?,
                  !raw.hasPrefix("card:"),
                  let dragged = PlayerControl(rawValue: raw),
                  dragged != target
            else {
                completion(nil)
                return
            }
            completion(dragged)
        }
    }

    func dropEntered(info: DropInfo) {
        draggedControl(from: info) { dragged in
            guard let dragged else { return }
            DispatchQueue.main.async {
                onHover?(dragged, target)
            }
        }
    }

    func dropExited(info: DropInfo) {
        onExit?()
    }

    func performDrop(info: DropInfo) -> Bool {
        guard let provider = info.itemProviders(for: [.text]).first else { return false }
        _ = provider.loadObject(ofClass: NSString.self) { value, _ in
            guard let raw = (value as? NSString) as String?,
                  let dragged = PlayerControl(rawValue: raw),
                  let from = controls.firstIndex(of: dragged),
                  let to = controls.firstIndex(of: target),
                  from != to
            else { return }
            DispatchQueue.main.async {
                onMove(IndexSet(integer: from), to > from ? to + 1 : to)
            }
        }
        return true
    }
}

/// Grid-level drop target for gallery drags ("card:<control>:<size>").
/// Reorder drags (bare raw values) are ignored here — the per-card
/// CardReorderDropDelegate handles those.
struct CardGalleryDropDelegate: DropDelegate {
    let onDrop: (PlayerControl, CardSize) -> Void

    func validateDrop(info: DropInfo) -> Bool {
        info.hasItemsConforming(to: [.text])
    }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        DropProposal(operation: .copy)
    }

    func performDrop(info: DropInfo) -> Bool {
        guard let provider = info.itemProviders(for: [.text]).first else { return false }
        _ = provider.loadObject(ofClass: NSString.self) { value, _ in
            guard let raw = (value as? NSString) as String?,
                  raw.hasPrefix("card:") else { return }
            let parts = raw.split(separator: ":")
            guard parts.count == 3,
                  let control = PlayerControl(rawValue: String(parts[1])),
                  let size = CardSize(rawValue: String(parts[2]))
            else { return }
            DispatchQueue.main.async {
                onDrop(control, size)
            }
        }
        return true
    }
}
