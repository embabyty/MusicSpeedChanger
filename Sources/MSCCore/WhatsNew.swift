import Foundation

/// Per-version highlights shown on the What's New splash after an update.
public enum WhatsNew {
    /// True when the splash should appear: a versioned build whose version
    /// differs from the last one that showed it.
    public static func shouldShow(current: String?, lastSeen: String?) -> Bool {
        guard let current, !current.isEmpty else { return false }
        return lastSeen != current
    }

    /// Feature bullets for a version. Falls back to era-appropriate notes so
    /// every update — stable or beta — gets a meaningful screen.
    public static func notes(for version: String) -> [String] {
        let v = AppVersion.normalized(version)
        let lower = v.lowercased()
        if lower.contains("publicbeta") {
            return [
                "Public beta channel — EAF Basic members and higher",
                "Early access to upcoming features before stable",
                "Thanks for helping test — report issues on GitHub",
            ]
        }
        let isBeta = lower.contains("beta")
        if isBeta {
            // Beta 3 introduced the EAF+ gate + splash; earlier betas had the
            // card/icon/loop refresh.
            if AppVersion.compare(v, "1.0.1-devbeta.3") != .orderedAscending {
                return [
                    "Supporter-only betas — sign in with Patreon (EAF+ tier) to continue",
                    "New Liquid Glass splash and What's New screen",
                    "Devbeta updates now require EAF+ verification in Settings",
                ]
            }
            return [
                "Widget-style card editing in the player",
                "Fresh app icon",
                "New loop controls",
            ]
        }
        return [
            "Tempo 50–200% with pitch preserved, pitch ±12 semitones",
            "AB loop with reverse playback for drilling sections",
            "31-band graphic EQ applied live and in WAV/M4A exports",
        ]
    }
}
