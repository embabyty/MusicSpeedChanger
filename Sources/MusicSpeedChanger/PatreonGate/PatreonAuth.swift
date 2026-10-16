import AppKit
import Foundation
import MSCCore
import Network

/// Patreon supporter verification for gated Beta builds.
///
/// Flow: browser-based OAuth (loopback callback, with manual-code fallback)
/// → token exchange → `identity` lookup → active membership of the
/// EmAppleFlagship campaign → cached 30-day pass so the app also works
/// offline / without re-signing every launch.
@MainActor
final class PatreonAuth: ObservableObject {
    enum Status: Equatable {
        case unknown
        case signingIn(String) // progress message
        case verified(email: String)
        case failed(String)
        case notConfigured
    }

    @Published private(set) var status: Status = .unknown

    /// Grace period for a cached verification (offline-friendly).
    private static let cacheDays = 30.0

    var verifiedEmail: String? {
        if case .verified(let email) = status { email } else { nil }
    }

    // MARK: - Gate decision

    /// True when the running build is a supporter-gated beta. The gate code
    /// first shipped in v1.0.1 Beta 3, so any beta that contains it is gated.
    static var gateRequired: Bool {
        guard let v = UpdateChecker.currentVersion else { return false }
        return v.range(of: "beta", options: .caseInsensitive) != nil
    }

    /// Silent pass when the cached entitlement satisfies this build's channel.
    func restoreCached() -> Bool {
        Self.gateSatisfied() && {
            let s = AppSettings.load()
            if let email = s.patreonSupporterEmail {
                status = .verified(email: email)
                return true
            }
            return false
        }()
    }

    /// Whether the cached verification unlocks the running build: devbeta
    /// needs EAF+, publicbeta needs EAF Basic or better.
    static func gateSatisfied() -> Bool {
        guard let current = UpdateChecker.currentVersion else { return false }
        switch PatreonConfig.Channel.of(current) {
        case .devbeta: return cachedEAFPlus() != nil
        case .publicbeta: return cachedBasic() != nil
        case .stable: return true
        }
    }

    /// Cached EAF+ email when verification is fresh, nil otherwise. Used by
    /// the launch gate and the devbeta update channel.
    static func cachedEAFPlus() -> String? {
        let s = AppSettings.load()
        guard s.patreonHasEAFPlus,
              let email = s.patreonSupporterEmail, !email.isEmpty,
              let until = s.patreonVerifiedUntil, until > Date()
        else { return nil }
        return email
    }

    /// Cached EAF Basic (or better) email when verification is fresh.
    static func cachedBasic() -> String? {
        let s = AppSettings.load()
        guard s.patreonHasEAFPlus || s.patreonHasEAFBasic,
              let email = s.patreonSupporterEmail, !email.isEmpty,
              let until = s.patreonVerifiedUntil, until > Date()
        else { return nil }
        return email
    }

    /// True when cached verification unlocks any beta channel (EAF+ implies
    /// Basic). Used by the devbeta/publicbeta update checks.
    static var betaUpdatesEntitled: Bool { cachedBasic() != nil }

    func signOut() {
        PatreonKeychain.delete()
        let s = AppSettings.load()
        s.patreonSupporterEmail = nil
        s.patreonVerifiedUntil = nil
        s.patreonHasEAFPlus = false
        s.patreonHasEAFBasic = false
        s.save()
        status = .unknown
    }

    // MARK: - Sign in

    func startSignIn() {
        guard PatreonConfig.isConfigured else {
            status = .notConfigured
            return
        }
        status = .signingIn("Opening Patreon in your browser…")
        let state = UUID().uuidString
        var components = URLComponents(url: PatreonConfig.authorizeURL, resolvingAgainstBaseURL: false)!
        components.queryItems = [
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "client_id", value: PatreonConfig.clientID),
            URLQueryItem(name: "redirect_uri", value: PatreonConfig.redirectURI),
            URLQueryItem(name: "scope", value: PatreonConfig.scopes),
            URLQueryItem(name: "state", value: state),
        ]
        guard let url = components.url else {
            status = .failed("Could not build the Patreon sign-in URL.")
            return
        }
        NSWorkspace.shared.open(url)
        Task { await waitForCallback(expectedState: state) }
    }

    /// Fallback when the browser can't reach the loopback callback: the user
    /// pastes the `code` value from the Patreon redirect address bar.
    func verifyPastedCode(_ raw: String) {
        let code = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !code.isEmpty else {
            status = .failed("Paste the code from the Patreon redirect URL.")
            return
        }
        guard PatreonConfig.isConfigured else {
            status = .notConfigured
            return
        }
        status = .signingIn("Verifying code with Patreon…")
        Task { await finish(code: code) }
    }

    // MARK: - internals

    private func waitForCallback(expectedState: String) async {
        do {
            let code = try await loopbackCode(expectedState: expectedState,
                                              port: PatreonConfig.callbackPort,
                                              timeout: 180)
            await finish(code: code)
        } catch is CancellationError {
            status = .failed("Sign-in timed out. Try again, or paste the code manually.")
        } catch {
            status = .failed("Sign-in was cancelled (\(error.localizedDescription)).")
        }
    }

    private func finish(code: String) async {
        do {
            status = .signingIn("Exchanging code with Patreon…")
            let tokens = try await exchange(code: code)
            PatreonKeychain.save(tokens)
            status = .signingIn("Checking EAF membership…")
            let membership = try await verifyMembership(accessToken: tokens.accessToken)
            let s = AppSettings.load()
            s.patreonSupporterEmail = membership.email
            s.patreonVerifiedUntil = Date().addingTimeInterval(Self.cacheDays * 86400)
            s.patreonHasEAFPlus = membership.plus
            s.patreonHasEAFBasic = membership.basic
            s.save()
            status = .verified(email: "\(membership.email) · \(membership.tierTitle)")
        } catch let e as PatreonError {
            status = .failed(e.message)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    // MARK: - HTTP

    struct TokenResponse: Decodable {
        var access_token: String
        var refresh_token: String?
        var expires_in: Double?
    }

    private func exchange(code: String) async throws -> PatreonKeychain.Tokens {
        var req = URLRequest(url: PatreonConfig.tokenURL)
        req.httpMethod = "POST"
        var body = URLComponents()
        body.queryItems = [
            URLQueryItem(name: "grant_type", value: "authorization_code"),
            URLQueryItem(name: "code", value: code),
            URLQueryItem(name: "client_id", value: PatreonConfig.clientID),
            URLQueryItem(name: "client_secret", value: PatreonConfig.clientSecret),
            URLQueryItem(name: "redirect_uri", value: PatreonConfig.redirectURI),
        ]
        req.httpBody = body.percentEncodedQuery?.data(using: .utf8)
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else {
            throw PatreonError("Patreon rejected the sign-in code. Please try again.")
        }
        let decoded = try JSONDecoder().decode(TokenResponse.self, from: data)
        let expiry = decoded.expires_in.map { Date().addingTimeInterval($0) }
        return PatreonKeychain.Tokens(accessToken: decoded.access_token,
                                      refreshToken: decoded.refresh_token,
                                      expiresAt: expiry)
    }

    struct IdentityResponse: Decodable {
        struct UserAttributes: Decodable {
            var email: String?
            var full_name: String?
        }
        struct UserResource: Decodable {
            var id: String?
            var attributes: UserAttributes?
        }
        struct Ref: Decodable {
            var id: String?
            var type: String?
        }
        struct RefData: Decodable {
            var data: Ref?
        }
        struct RefList: Decodable {
            var data: [Ref]?
        }
        struct MemberRelationships: Decodable {
            var campaign: RefData?
            var currently_entitled_tiers: RefList?
        }
        struct Attributes: Decodable {
            var patron_status: String?
            var title: String?
        }
        struct IncludedObject: Decodable {
            var id: String?
            var type: String?
            var attributes: Attributes?
            var relationships: MemberRelationships?
        }
        var data: UserResource?
        var included: [IncludedObject]?
    }

    struct Membership {
        var email: String
        var tierTitle: String
        var plus: Bool
        var basic: Bool
    }

    /// Active EmAppleFlagship membership entitled to EAF Basic or better.
    /// Devbeta builds additionally require EAF+ (checked by callers via the
    /// stored flags). Tiers match by ID first, then by title.
    private func verifyMembership(accessToken: String) async throws -> Membership {
        var req = URLRequest(url: PatreonConfig.identityURL)
        req.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else {
            throw PatreonError("Could not read your Patreon membership. Please try again.")
        }
        let identity = try JSONDecoder().decode(IdentityResponse.self, from: data)
        // Creator bypass: the campaign owner holds no membership of their
        // own campaign, but must always pass the gate. Matched by Patreon
        // user ID or by the verified account email.
        let accountEmail = identity.data?.attributes?.email?.lowercased()
        if identity.data?.id == PatreonConfig.creatorUserID
            || accountEmail == PatreonConfig.ownerEmail.lowercased()
        {
            return Membership(email: memberEmail(identity), tierTitle: "Creator",
                              plus: true, basic: true)
        }
        let objects = identity.included ?? []
        let tierTitlesByID = Dictionary(
            uniqueKeysWithValues: objects.compactMap { obj -> (String, String)? in
                guard obj.type == "tier",
                      let id = obj.id, let title = obj.attributes?.title
                else { return nil }
                return (id, title)
            })
        let wantedPlusIDs = Set(PatreonConfig.requiredTierIDs)
        let wantedPlusNames = Set(PatreonConfig.requiredTierNames.map { $0.lowercased() })
        let wantedBasicIDs = Set(PatreonConfig.basicTierIDs)
        let wantedBasicNames = Set(PatreonConfig.basicTierNames.map { $0.lowercased() })
        var activeMember = false
        for obj in objects {
            guard obj.type == "member",
                  obj.attributes?.patron_status == "active_patron",
                  obj.relationships?.campaign?.data?.id == PatreonConfig.campaignID
            else { continue }
            activeMember = true
            let entitled = obj.relationships?.currently_entitled_tiers?.data ?? []
            var plusTitle: String?
            var basicTitle: String?
            for tier in entitled {
                guard let id = tier.id else { continue }
                let title = tierTitlesByID[id]
                if wantedPlusIDs.contains(id)
                    || (title.map { wantedPlusNames.contains($0.lowercased()) } ?? false)
                {
                    plusTitle = title ?? id
                }
                if wantedBasicIDs.contains(id)
                    || (title.map { wantedBasicNames.contains($0.lowercased()) } ?? false)
                {
                    basicTitle = title ?? id
                }
            }
            // EAF+ inherits Basic access.
            if let title = plusTitle {
                return Membership(email: memberEmail(identity), tierTitle: title,
                                  plus: true, basic: true)
            }
            if let title = basicTitle {
                return Membership(email: memberEmail(identity), tierTitle: title,
                                  plus: false, basic: true)
            }
        }
        if activeMember {
            throw PatreonError(
                "This Patreon account supports EmAppleFlagship but is not on the EAF Basic "
                    + "or EAF+ tier. Join EAF Basic for public betas, or EAF+ for dev betas.")
        }
        throw PatreonError(
            "No active EmAppleFlagship membership found on this Patreon account. "
                + "Join EAF Basic for public betas, or EAF+ for dev betas.")
    }

    private func memberEmail(_ identity: IdentityResponse) -> String {
        identity.data?.attributes?.email
            ?? identity.data?.attributes?.full_name
            ?? "Patreon supporter"
    }

    // MARK: - Loopback callback server

    /// Listens on 127.0.0.1 for the OAuth redirect and returns the `code`.
    private func loopbackCode(expectedState: String, port: UInt16, timeout: TimeInterval) async throws -> String {
        try await withThrowingTaskGroup(of: String.self) { group in
            group.addTask {
                try await self.serveOnce(expectedState: expectedState, port: port)
            }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                throw CancellationError()
            }
            guard let code = try await group.next() else { throw CancellationError() }
            group.cancelAll()
            return code
        }
    }

    private func serveOnce(expectedState: String, port: UInt16) async throws -> String {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<String, Error>) in
            let listener: NWListener
            do {
                listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
            } catch {
                cont.resume(throwing: error)
                return
            }
            // All callbacks run on .main (see `start(queue:)` below), so this
            // box is safe to share across the Sendable connection handlers.
            final class ResumeBox: @unchecked Sendable {
                var resumed = false
                let cont: CheckedContinuation<String, Error>
                let listener: NWListener
                init(_ cont: CheckedContinuation<String, Error>, _ listener: NWListener) {
                    self.cont = cont
                    self.listener = listener
                }
                func done(_ result: Result<String, Error>) {
                    guard !resumed else { return }
                    resumed = true
                    listener.cancel()
                    switch result {
                    case .success(let code): cont.resume(returning: code)
                    case .failure(let error): cont.resume(throwing: error)
                    }
                }
            }
            let box = ResumeBox(cont, listener)
            listener.newConnectionHandler = { conn in
                conn.start(queue: .main)
                conn.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, _ in
                    defer { conn.cancel() }
                    guard let data, let request = String(data: data, encoding: .utf8),
                          let code = Self.parseCallback(request: request)
                    else {
                        Self.reply(conn, ok: false)
                        box.done(.failure(PatreonError("Invalid redirect from Patreon.")))
                        return
                    }
                    guard code.state == expectedState else {
                        Self.reply(conn, ok: false)
                        box.done(.failure(PatreonError("Sign-in response did not match this request.")))
                        return
                    }
                    Self.reply(conn, ok: true)
                    box.done(.success(code.code))
                }
            }
            listener.start(queue: .main)
        }
    }

    private struct Callback {
        var code: String
        var state: String
    }

    private static func parseCallback(request: String) -> Callback? {
        // e.g. "GET /callback?code=XYZ&state=ABC HTTP/1.1"
        guard let line = request.components(separatedBy: "\r\n").first,
              line.hasPrefix("GET ")
        else { return nil }
        let path = line.dropFirst(4).prefix(while: { $0 != " " })
        let comps = URLComponents(string: "http://127.0.0.1\(path)")
        let items = comps?.queryItems ?? []
        guard let code = items.first(where: { $0.name == "code" })?.value,
              let state = items.first(where: { $0.name == "state" })?.value,
              !code.isEmpty
        else { return nil }
        return Callback(code: code, state: state)
    }

    private static func reply(_ conn: NWConnection, ok: Bool) {
        let body = ok
            ? "<h1>Signed in — return to Music Speed Changer.</h1><p>You can close this tab.</p>"
            : "<h1>Sign-in failed.</h1><p>Return to Music Speed Changer and try again.</p>"
        let html = "<html><body style='font-family:-apple-system;padding:40px'>\(body)</body></html>"
        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(html.utf8.count)\r\nConnection: close\r\n\r\n\(html)"
        conn.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in })
    }
}

struct PatreonError: Error {
    var message: String
    init(_ message: String) { self.message = message }
}
