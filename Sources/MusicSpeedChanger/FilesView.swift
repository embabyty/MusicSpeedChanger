import SwiftUI
import AppKit
import UniformTypeIdentifiers
import MSCCore

/// Files inspector: trailing file list in the Apple Music queue style,
/// with a header, Clear action, and Add/Remove footer.
struct FilesView: View {
    @EnvironmentObject var tracks: TrackList

    let onSelect: (TrackItem) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Files").font(.headline)
                Spacer()
            }
            .padding(.horizontal, 12)
            .padding(.top, 10)

            if tracks.tracks.isEmpty {
                ContentUnavailableView(
                    "No Files",
                    systemImage: "music.note.list",
                    description: Text("Open audio files or drop them here.")
                )
                .frame(maxHeight: .infinity)
            } else {
                List(selection: $tracks.selectedID) {
                    ForEach(tracks.tracks) { track in
                        Label {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(track.name).lineLimit(1).truncationMode(.middle)
                                Text(track.durationText)
                                    .font(.caption2)
                                    .foregroundStyle(.secondary)
                            }
                            .padding(.vertical, 2)
                        } icon: {
                            RoundedRectangle(cornerRadius: 7)
                                .fill(.ultraThinMaterial)
                                .frame(width: 30, height: 30)
                                .overlay(
                                    Image(systemName: "music.note")
                                        .foregroundStyle(.secondary)
                                )
                        }
                        .tag(track.id)
                        .contextMenu {
                            Button("Play", systemImage: "play.fill") {
                                tracks.selectedID = track.id
                                onSelect(track)
                            }
                            Button("Show in Finder", systemImage: "folder") {
                                NSWorkspace.shared.activateFileViewerSelecting([track.url])
                            }
                            Divider()
                            Button("Remove", systemImage: "trash", role: .destructive) {
                                tracks.remove(track)
                            }
                        }
                    }
                }
                .listStyle(.sidebar)
                .onChange(of: tracks.selectedID) { _, newID in
                    guard let id = newID,
                          let track = tracks.tracks.first(where: { $0.id == id })
                    else { return }
                    onSelect(track)
                }
            }

        }
        .frame(minWidth: 230)
        .onDrop(of: [UTType.fileURL], isTargeted: nil) { providers in
            handleDrop(providers)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        for provider in providers {
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                let url: URL? = {
                    if let u = item as? URL { return u }
                    if let data = item as? Data { return URL(dataRepresentation: data, relativeTo: nil) }
                    return nil
                }()
                guard let url else { return }
                DispatchQueue.main.async {
                    tracks.add(urls: [url])
                    onSelect(TrackItem(url: url))
                }
            }
        }
        return true
    }
}
