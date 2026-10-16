import AppKit
import SwiftUI

/// Launch gate shown by supporter-gated Beta builds before the main window.
///
/// The user signs in with Patreon; the app proceeds only when an active
/// EmAppleFlagship membership is found. Non-supporters get a link to the
/// campaign page and a Quit button (stable builds stay public and ungated).
struct PatreonGateView: View {
    @ObservedObject var auth: PatreonAuth
    var onVerified: () -> Void

    @State private var pastedCode = ""
    @State private var showManual = false

    private var channel: PatreonConfig.Channel {
        PatreonConfig.Channel.of(UpdateChecker.currentVersion ?? "")
    }

    var body: some View {
        VStack(spacing: 16) {
            Spacer()
            if let icon = NSApp.applicationIconImage {
                Image(nsImage: icon)
                    .resizable()
                    .frame(width: 96, height: 96)
                    .clipShape(RoundedRectangle(cornerRadius: 22))
            }
            Text("Supporter-only beta")
                .font(.largeTitle)
                .fontWeight(.bold)
            Text("Beta builds of Music Speed Changer are an early-access perk for "
                + "\(channel.tierDescription) on Patreon. Sign in to verify your "
                + "membership and continue.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 460)

            switch auth.status {
            case .verified(let email):
                Label("Verified — \(email)", systemImage: "checkmark.seal.fill")
                    .foregroundStyle(.green)
                Button("Continue") { onVerified() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
            case .signingIn(let message):
                ProgressView(message)
            case .failed(let message):
                Text(message)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 460)
                signInButton
            case .notConfigured:
                Text("Beta sign-in isn't configured in this build. Grab the latest "
                    + "Beta from Patreon, or use the public stable release.")
                    .multilineTextAlignment(.center)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: 460)
            case .unknown:
                signInButton
            }

            if showManual, !isVerified {
                VStack(spacing: 8) {
                    Text("If your browser couldn't reach the app, paste the `code` "
                        + "from the Patreon redirect address bar:")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    HStack {
                        TextField("Paste code", text: $pastedCode)
                            .textFieldStyle(.roundedBorder)
                            .frame(maxWidth: 320)
                        Button("Verify") { auth.verifyPastedCode(pastedCode) }
                    }
                }
            } else if !isVerified {
                Button("Having trouble? Paste a code instead") { showManual = true }
                    .buttonStyle(.link)
                    .font(.caption)
            }

            HStack(spacing: 12) {
                Button("Get EAF+") { NSWorkspace.shared.open(PatreonConfig.pageURL) }
                Button("Quit") { NSApp.terminate(nil) }
                    .buttonStyle(.link)
            }
            Spacer()
        }
        .padding(32)
        .frame(minWidth: 560, minHeight: 560)
        .onChange(of: isVerified) { verified in
            if verified { onVerified() }
        }
    }

    private var isVerified: Bool {
        if case .verified = auth.status { true } else { false }
    }

    private var signInButton: some View {
        Button("Sign in with Patreon") { auth.startSignIn() }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
    }
}
