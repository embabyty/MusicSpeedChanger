import Foundation

/// OAuth + campaign configuration for the Patreon supporter gate.
///
/// Beta builds (v1.0.1 Beta 3 and later) require the user to sign in with
/// Patreon and hold an active membership of the EmAppleFlagship campaign
/// before the app proceeds past launch.
///
/// The Client ID / secret live in `PatreonSecrets.swift` (git-ignored).
/// Copy `scripts/PatreonSecrets.template.swift` to
/// `Sources/MusicSpeedChanger/PatreonGate/PatreonSecrets.swift` and fill it
/// in. CI generates that file from the `PATREON_CLIENT_ID` /
/// `PATREON_CLIENT_SECRET` repo secrets.
enum PatreonConfig {
    /// Campaign ID for EmAppleFlagship (https://www.patreon.com/EmAppleFlagship).
    static let campaignID = "14934463"
    static let pageURLString = "https://www.patreon.com/EmAppleFlagship"

    static var pageURL: URL {
        URL(string: pageURLString)!
    }

    /// Fixed loopback port for the OAuth callback. This exact URI must be
    /// registered in the Patreon OAuth client settings.
    static let callbackPort: UInt16 = 61934
    static var redirectURI: String {
        "http://127.0.0.1:\(callbackPort)/callback"
    }

    static let scopes = "identity identity[email]"

    /// EAF+ tier (https://www.patreon.com/checkout/EmAppleFlagship?rid=29528310).
    /// Beta builds and devbeta updates require an active membership entitled
    /// to this tier — not just any patronage.
    static let requiredTierIDs = ["29528310"]
    static let requiredTierNames = ["EAF+"]

    /// EAF Basic tier unlocks the publicbeta branch. ID is the campaign's
    /// other reward — confirm in the Patreon portal; matching falls back to
    /// the tier name either way. EAF+ members inherit Basic access.
    static let basicTierIDs = ["27021453"]
    static let basicTierNames = ["EAF Basic"]

    /// Release channel derived from a version or tag string.
    enum Channel {
        case stable
        case devbeta
        case publicbeta

        static func of(_ versionOrTag: String) -> Channel {
            let v = versionOrTag.lowercased()
            if v.contains("publicbeta") { return .publicbeta }
            if v.contains("beta") { return .devbeta }
            return .stable
        }

        /// Tier description shown on the gate for this channel.
        var tierDescription: String {
            switch self {
            case .devbeta: "EAF+ members"
            case .publicbeta: "EAF Basic members or higher"
            case .stable: "everyone"
            }
        }
    }

    static var clientID: String { PatreonSecrets.clientID }
    static var clientSecret: String { PatreonSecrets.clientSecret }

    static var isConfigured: Bool {
        !clientID.trimmingCharacters(in: .whitespaces).isEmpty
            && !clientSecret.trimmingCharacters(in: .whitespaces).isEmpty
    }

    /// User ID of the campaign owner (bypasses the tier check).
    static let creatorUserID = "14376995"
    /// Owner's Patreon account email — second bypass signal, matched
    /// case-insensitively against the verified identity email.
    static let ownerEmail = "ios11emiry@gmail.com"

    static var authorizeURL: URL {
        URL(string: "https://www.patreon.com/oauth2/authorize")!
    }

    static var tokenURL: URL {
        URL(string: "https://www.patreon.com/api/oauth2/token")!
    }

    static var identityURL: URL {
        var c = URLComponents(string: "https://www.patreon.com/api/oauth2/v2/identity")!
        c.queryItems = [
            URLQueryItem(name: "include", value: "memberships.campaign,memberships.currently_entitled_tiers"),
            URLQueryItem(name: "fields[user]", value: "email,full_name"),
            URLQueryItem(name: "fields[member]", value: "patron_status"),
            URLQueryItem(name: "fields[tier]", value: "title"),
        ]
        return c.url!
    }
}
