import AppKit
import SwiftUI

/// Apple-styled What's New splash: Liquid Glass card, app icon, and the new
/// features in this update. Shown once per app version after an update —
/// the user continues past it into the Patreon gate (beta) or the main
/// window (stable).
struct SplashView: View {
    var version: String?
    var notes: [String] = []
    var onContinue: () -> Void = {}

    private let glyphs: [(String, String)] = [
        ("waveform", "Waveform"),
        ("speedometer", "Tempo"),
        ("slider.horizontal.3", "EQ"),
    ]

    var body: some View {
        ZStack {
            LinearGradient(
                colors: [Color(nsColor: .windowBackgroundColor), Color.black.opacity(0.35)],
                startPoint: .top,
                endPoint: .bottom
            )
            .ignoresSafeArea()

            VStack(spacing: 18) {
                Spacer()
                if let icon = NSApp.applicationIconImage {
                    Image(nsImage: icon)
                        .resizable()
                        .frame(width: 128, height: 128)
                        .clipShape(RoundedRectangle(cornerRadius: 30))
                        .glassEffect(.regular.interactive(), in: .rect(cornerRadius: 30))
                        .shadow(radius: 24)
                }
                Text("Music Speed Changer")
                    .font(.largeTitle)
                    .fontWeight(.bold)
                if let version {
                    Text("What's new in \(version)")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 4)
                        .glassEffect(.regular, in: .capsule)
                }
                if !notes.isEmpty {
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(notes, id: \.self) { note in
                            Label(note, systemImage: "sparkles")
                                .font(.body)
                                .foregroundStyle(.primary)
                        }
                    }
                    .padding(.horizontal, 20)
                    .padding(.vertical, 16)
                    .glassEffect(.regular, in: .rect(cornerRadius: 20))
                    .frame(maxWidth: 480)
                } else {
                    HStack(spacing: 10) {
                        ForEach(glyphs, id: \.0) { glyph in
                            Label(glyph.1, systemImage: glyph.0)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .padding(.horizontal, 12)
                                .padding(.vertical, 7)
                                .glassEffect(.regular, in: .capsule)
                        }
                    }
                }
                Button("Continue") { onContinue() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .padding(.top, 4)
                Spacer()
                Text("Beta builds are an EAF+ perk · patreon.com/EmAppleFlagship")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding(40)
        }
        .frame(minWidth: 640, minHeight: 560)
    }
}
