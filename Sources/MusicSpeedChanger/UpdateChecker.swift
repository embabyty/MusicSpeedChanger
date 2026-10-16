import Foundation
import AppKit
import MSCCore

/// Update checks against this project's GitHub Releases
/// (same feed shape as the Windows build).
@MainActor
enum UpdateChecker {
    struct Release {
        let version: String
        let notes: String
        let htmlURL: String?
        let prerelease: Bool
    }

    enum Outcome {
        case upToDate(current: String)
        case available(Release)
        case failed(String)
    }

    /// Current bundle version, nil for version-less dev builds.
    static var currentVersion: String? {
        guard let v = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String,
              !v.isEmpty
        else { return nil }
        return v
    }

    /// One-shot check used by Settings → Check now.
    /// Stable channel reads `.../releases/latest` (full releases only).
    /// Beta channel reads the releases list so pre-releases are visible too.
    static func check(feed: String, includeBeta: Bool) async -> Outcome {
        let current = currentVersion ?? "development build"
        guard let url = URL(string: feed.trimmingCharacters(in: .whitespaces)), !feed.isEmpty else {
            return .failed("The update feed URL is empty.")
        }
        do {
            if includeBeta, let listURL = releasesListURL(from: url) {
                let (data, _) = try await URLSession.shared.data(from: listURL)
                guard let arr = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
                    return .failed("Unexpected response from the releases feed.")
                }
                // Newest first; skip drafts and channels the user isn't
                // entitled to (devbeta needs EAF+, publicbeta needs Basic+).
                let plus = PatreonAuth.cachedEAFPlus() != nil
                let basic = PatreonAuth.cachedBasic() != nil
                guard let obj = arr.first(where: {
                    guard ($0["draft"] as? Bool) != true,
                          let tag = $0["tag_name"] as? String
                    else { return false }
                    switch PatreonConfig.Channel.of(tag) {
                    case .stable: return true
                    case .devbeta: return plus
                    case .publicbeta: return basic
                    }
                }),
                      let tag = obj["tag_name"] as? String
                else {
                    // No published releases yet.
                    return .upToDate(current: current)
                }
                return evaluate(tag: tag,
                                prerelease: (obj["prerelease"] as? Bool) ?? false,
                                body: obj["body"] as? String ?? "",
                                htmlURL: obj["html_url"] as? String,
                                current: current)
            }
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = obj["tag_name"] as? String
            else {
                // No releases published yet (GitHub returns 404 "Not Found" JSON).
                return .upToDate(current: current)
            }
            return evaluate(tag: tag,
                            prerelease: (obj["prerelease"] as? Bool) ?? false,
                            body: obj["body"] as? String ?? "",
                            htmlURL: obj["html_url"] as? String,
                            current: current)
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// `.../releases/latest` → `.../releases?per_page=20`. Nil for custom feeds,
    /// which keep single-object behavior.
    private static func releasesListURL(from url: URL) -> URL? {
        var s = url.absoluteString
        while s.hasSuffix("/") { s.removeLast() }
        guard s.hasSuffix("/releases/latest") else { return nil }
        return URL(string: String(s.dropLast("/latest".count)) + "?per_page=20")
    }

    private static func evaluate(tag: String, prerelease: Bool, body: String,
                                 htmlURL: String?, current: String) -> Outcome {
        let version = AppVersion.normalized(tag)
        guard currentVersion != nil else {
            // Dev build: report what's out there without claiming an update.
            return .available(Release(version: version, notes: body,
                                      htmlURL: htmlURL, prerelease: prerelease))
        }
        if AppVersion.isNewer(version, than: current) {
            return .available(Release(version: version, notes: body,
                                      htmlURL: htmlURL, prerelease: prerelease))
        }
        return .upToDate(current: current)
    }

    private static var checked = false

    /// Startup check: silent for version-less dev builds.
    static func checkIfNewer() {
        guard !checked else { return }
        checked = true
        guard currentVersion != nil else { return }
        let settings = AppSettings.load()
        let feed = settings.updateFeedUrl
        // Beta updates are a perk: without a fresh EAF Basic (or better)
        // verification the check stays on the stable channel.
        let includeBeta = settings.includeBetaUpdates && PatreonAuth.betaUpdatesEntitled
        Task {
            let outcome = await check(feed: feed, includeBeta: includeBeta)
            if case .available(let release) = outcome {
                prompt(tag: release.version, htmlURL: release.htmlURL)
            }
        }
    }

    static func prompt(tag: String, htmlURL: String?) {
        let alert = NSAlert()
        alert.messageText = "Update Available"
        alert.informativeText = "A new version of Music Speed Changer (\(tag)) is available."
        alert.addButton(withTitle: "View Release")
        alert.addButton(withTitle: "Later")
        if alert.runModal() == .alertFirstButtonReturn,
           let htmlURL, let url = URL(string: htmlURL)
        {
            NSWorkspace.shared.open(url)
        }
    }
}
