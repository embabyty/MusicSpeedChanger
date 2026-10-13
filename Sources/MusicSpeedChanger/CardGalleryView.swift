import SwiftUI
import MSCCore

/// Widget-gallery-style card picker, modeled on the macOS desktop widget
/// gallery: searchable sidebar on the left, card previews on the right,
/// hint + Done bar at the bottom. Previews support click-to-add and drag.
struct CardGalleryView: View {
    var includedControls: [PlayerControl]
    var sizeFor: (PlayerControl) -> CardSize
    var onAdd: (PlayerControl, CardSize) -> Void
    var onRemove: (PlayerControl) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    @State private var selection: PlayerControl?

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                sidebar
                    .frame(width: 250)
                Divider()
                detail
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            HStack {
                Text("Drag a card to place it on the player, or click + to add it…")
                    .foregroundStyle(.secondary)
                Spacer()
                Button("Done") { dismiss() }
                    .buttonStyle(.glassProminent)
                    .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(minWidth: 920, minHeight: 600)
    }

    // MARK: - Sidebar

    private var filteredControls: [PlayerControl] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return PlayerControl.allCases }
        return PlayerControl.allCases.filter {
            $0.title.lowercased().contains(q) || $0.rawValue.lowercased().contains(q)
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "magnifyingglass")
                    .foregroundStyle(.secondary)
                TextField("Search Cards", text: $search)
                    .textFieldStyle(.plain)
            }
            .padding(8)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 10))
            .padding(.horizontal, 12)
            .padding(.top, 12)

            Button {
                selection = nil
            } label: {
                HStack {
                    Image(systemName: "square.grid.2x2")
                        .frame(width: 26)
                    Text("All Cards")
                    Spacer()
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
                .background(selection == nil ? Color.white.opacity(0.14) : .clear,
                            in: RoundedRectangle(cornerRadius: 8))
            }
            .buttonStyle(.plain)
            .padding(.horizontal, 8)

            ScrollView {
                VStack(spacing: 1) {
                    ForEach(filteredControls) { control in
                        Button { selection = control } label: {
                            HStack {
                                Image(systemName: control.icon)
                                    .frame(width: 26)
                                    .foregroundStyle(.secondary)
                                Text(control.title)
                                    .lineLimit(1)
                                Spacer(minLength: 4)
                                if includedControls.contains(control) {
                                    Image(systemName: "checkmark.circle.fill")
                                        .foregroundStyle(.green)
                                        .help("On the player")
                                }
                            }
                            .padding(.horizontal, 12)
                            .padding(.vertical, 6)
                            .background(selection == control ? Color.white.opacity(0.14) : .clear,
                                        in: RoundedRectangle(cornerRadius: 8))
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 8)
                .padding(.bottom, 12)
            }
        }
    }

    // MARK: - Detail

    @ViewBuilder
    private var detail: some View {
        if let control = selection {
            singleControlDetail(control)
        } else {
            allCardsDetail
        }
    }

    private var allCardsDetail: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                Text("All Cards")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 28)
                    .padding(.top, 20)
                // Uniform tiles for every card so the grid stays even —
                // per-card player sizes only apply in the single-card view.
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 300), spacing: 24)], spacing: 32) {
                    ForEach(filteredControls) { control in
                        VStack(spacing: 10) {
                            galleryTile(control: control)
                            Text(control.title).font(.headline)
                            Text(control.galleryBlurb)
                                .font(.callout)
                                .foregroundStyle(.secondary)
                                .multilineTextAlignment(.center)
                                .lineLimit(2)
                                .frame(minHeight: 38, alignment: .top)
                            addButton(control: control, size: sizeFor(control))
                                .frame(height: 30)
                        }
                    }
                }
                .padding(.horizontal, 28)
                .padding(.bottom, 28)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func singleControlDetail(_ control: PlayerControl) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text(control.title)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 28)
                    .padding(.top, 20)
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(alignment: .top, spacing: 36) {
                        ForEach(CardSize.allCases, id: \.self) { size in
                            VStack(spacing: 10) {
                                previewMock(control: control, size: size)
                                Text(size.title)
                                    .font(.headline)
                                Text(control.galleryBlurb)
                                    .font(.callout)
                                    .foregroundStyle(.secondary)
                                    .multilineTextAlignment(.center)
                                    .lineLimit(2)
                                    .frame(width: 220)
                                    .frame(minHeight: 38, alignment: .top)
                                addButton(control: control, size: size)
                                    .frame(height: 30)
                            }
                        }
                    }
                    .padding(.horizontal, 28)
                    .padding(.bottom, 28)
                }
            }
        }
    }

    @ViewBuilder
    private func addButton(control: PlayerControl, size: CardSize) -> some View {
        let included = includedControls.contains(control)
        let current = sizeFor(control)
        if included && current == size {
            Menu {
                Button("Remove from Player", role: .destructive) { onRemove(control) }
            } label: {
                Label("Added", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.green)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 5)
                    .background(.quaternary.opacity(0.5), in: Capsule())
            }
            .menuStyle(.borderlessButton)
        } else if included {
            Button("Apply \(size.shortTitle)") { onAdd(control, size) }
                .buttonStyle(.glass)
                .help("Resize to \(size.title)")
        } else {
            Button {
                onAdd(control, size)
            } label: {
                Image(systemName: "plus.circle.fill")
                    .font(.system(size: 26))
                    .foregroundStyle(.green)
            }
            .buttonStyle(.plain)
            .help("Add \(control.title) (\(size.title))")
        }
    }

    // MARK: - Preview mock

    /// Uniform tile for the All Cards grid: fixed size for every card so
    /// rows stay aligned regardless of each card's player size.
    private func galleryTile(control: PlayerControl) -> some View {
        let size = CardSize.medium
        return previewBody(control: control, size: size, width: 264, height: 148)
            .onDrag { NSItemProvider(object: "card:\(control.rawValue):\(size.rawValue)" as NSString) }
            .onTapGesture {
                if !includedControls.contains(control) {
                    onAdd(control, size)
                } else {
                    selection = control
                }
            }
            .help("Drag onto the player or click + to add")
    }

    private func previewMock(control: PlayerControl, size: CardSize) -> some View {
        let w: CGFloat = switch size {
        case .small: 140
        case .medium: 280
        case .large: 420
        }
        return previewBody(control: control, size: size,
                           width: w, height: size == .small ? 150 : 130)
            .onDrag { NSItemProvider(object: "card:\(control.rawValue):\(size.rawValue)" as NSString) }
            .onTapGesture {
                if !includedControls.contains(control) {
                    onAdd(control, size)
                } else {
                    selection = control
                }
            }
            .help("Drag onto the player or click + to add")
    }

    private func previewBody(control: PlayerControl, size: CardSize, width: CGFloat, height: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: 16)
            .fill(Color(white: 0.16))
            .strokeBorder(Color.white.opacity(0.12))
            .frame(width: width, height: height)
            .overlay {
                VStack(spacing: 8) {
                    Image(systemName: control.icon)
                        .font(.system(size: 24))
                        .foregroundStyle(.secondary)
                    if size != .small {
                        Capsule()
                            .fill(Color.white.opacity(0.22))
                            .frame(width: min(width * 0.6, 220), height: 8)
                        Capsule()
                            .fill(Color.white.opacity(0.1))
                            .frame(width: min(width * 0.4, 150), height: 6)
                    } else {
                        Text(control.title)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    if !control.isSupportedOnMac {
                        Text("Mac soon")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 8)
                            .padding(.vertical, 2)
                            .background(.quaternary.opacity(0.5), in: Capsule())
                    }
                }
                .padding()
            }
    }
}

extension Notification.Name {
    static let showConfigureCustomPresets = Notification.Name("showConfigureCustomPresets")
}
