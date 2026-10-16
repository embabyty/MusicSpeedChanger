import Foundation

/// Copy this file to `Sources/MusicSpeedChanger/PatreonGate/PatreonSecrets.swift`
/// and fill it in from the Patreon Developer Portal
///
/// Register this exact redirect URI on the OAuth client:
///   http://127.0.0.1:61934/callback
///
/// CI note: the Beta release workflow generates `PatreonSecrets.swift` from
/// the `PATREON_CLIENT_ID` / `PATREON_CLIENT_SECRET` repository secrets.
enum PatreonSecrets {
    static let clientID = ""
    static let clientSecret = ""
}
