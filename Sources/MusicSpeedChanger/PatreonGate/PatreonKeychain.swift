import Foundation
import Security

/// Minimal Keychain wrapper for the Patreon OAuth tokens. Tokens must not
/// live in `settings.json` next to everything else.
enum PatreonKeychain {
    private static let service = "com.embabyty.MusicSpeedChangerMacOS.patreon"

    struct Tokens {
        var accessToken: String
        var refreshToken: String?
        var expiresAt: Date?
    }

    static func load() -> Tokens? {
        guard let data = loadData(account: "tokens"),
              let dict = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let access = dict["access"] as? String, !access.isEmpty
        else { return nil }
        let refresh = dict["refresh"] as? String
        let expiresAt = (dict["expires_at"] as? Double).map { Date(timeIntervalSince1970: $0) }
        return Tokens(accessToken: access, refreshToken: refresh, expiresAt: expiresAt)
    }

    static func save(_ tokens: Tokens) {
        let dict: [String: Any] = [
            "access": tokens.accessToken,
            "refresh": tokens.refreshToken ?? "",
            "expires_at": tokens.expiresAt?.timeIntervalSince1970 ?? 0,
        ]
        if let data = try? JSONSerialization.data(withJSONObject: dict) {
            saveData(data, account: "tokens")
        }
    }

    static func delete() {
        deleteData(account: "tokens")
    }

    // MARK: - primitives

    private static func saveData(_ data: Data, account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
        var attrs = query
        attrs[kSecValueData as String] = data
        attrs[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(attrs as CFDictionary, nil)
    }

    private static func loadData(account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    private static func deleteData(account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
