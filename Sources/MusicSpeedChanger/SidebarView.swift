import SwiftUI
import MSCCore

/// Finder-style source list: a "Presets" section with the iOS Editor Mode
/// options. Selecting a preset filters the Player; card layout itself is
/// edited from the Player (Edit Cards / + gallery).
struct FinderSidebar: View {
    @Binding var selection: EditorPreset
    /// When false (no audio files), every preset except Default is disabled.
    var presetsEnabled: Bool = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                SidebarSection(title: "Presets") {
                    ForEach(EditorPreset.allCases) { preset in
                        let enabled = presetsEnabled || preset == .default
                        SidebarRow(icon: preset.icon, title: preset.title,
                                   subtitle: preset.subtitle,
                                   selected: selection == preset,
                                   disabled: !enabled) {
                            selection = preset
                        }
                    }
                }
            }
            .padding(.horizontal, 10)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
    }
}

/// Small semibold gray header, like Finder's "Favorites" / "Locations".
private struct SidebarSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.horizontal, 8)
                .padding(.top, 10)
                .padding(.bottom, 2)
            content()
        }
    }
}

/// One icon row with hover feedback and a Finder-like selection pill.
/// An optional subtitle (e.g. a preset's control list) renders underneath.
private struct SidebarRow: View {
    let icon: String
    let title: String
    var subtitle: String?
    let selected: Bool
    var disabled: Bool = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                    .font(.system(size: 15))
                    .foregroundStyle(selected ? .primary : .secondary)
                    .frame(width: 22)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.system(size: 13))
                        .foregroundStyle(.primary)
                        .lineLimit(1)
                    if let subtitle {
                        Text(subtitle)
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.tail)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 8)
                    .fill(selected ? Color.white.opacity(0.14)
                          : hovering ? Color.white.opacity(0.06) : .clear)
            )
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .opacity(disabled ? 0.4 : 1)
        .onHover { hovering = $0 && !disabled }
    }
}
