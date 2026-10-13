import Foundation

/// What to show on Discord. Everything except `details` is optional.
public struct DiscordActivity: Sendable {
    public var details: String
    public var state: String?
    /// Unix epoch seconds; Discord renders it as elapsed time.
    public var startTimestamp: Int?
    public var largeImageKey: String?
    public var largeImageText: String?
    public var smallImageKey: String?
    public var smallImageText: String?
    public var buttons: [(label: String, url: String)]?

    public init(details: String, state: String? = nil, startTimestamp: Int? = nil,
                largeImageKey: String? = nil, largeImageText: String? = nil,
                smallImageKey: String? = nil, smallImageText: String? = nil,
                buttons: [(label: String, url: String)]? = nil) {
        self.details = details
        self.state = state
        self.startTimestamp = startTimestamp
        self.largeImageKey = largeImageKey
        self.largeImageText = largeImageText
        self.smallImageKey = smallImageKey
        self.smallImageText = smallImageText
        self.buttons = buttons
    }
}

/// Minimal Discord Rich Presence client over the local `discord-ipc-N` socket.
/// No dependencies. All calls are safe from any thread and cheap when the
/// Discord desktop app isn't running (the connection just refuses fast and
/// every operation becomes a no-op until the next attempt).
public final class DiscordRPC: @unchecked Sendable {
    private enum Opcode: Int32 { case handshake = 0, frame = 1 }

    private let clientID: String
    private let queue = DispatchQueue(label: "com.musicspeedchanger.discordrpc")
    private let lock = NSLock()
    private var fd: Int32 = -1

    public init(clientID: String) { self.clientID = clientID }

    // MARK: - Public API

    /// Show (or update) the presence. Connects first if needed.
    public func setActivity(_ activity: DiscordActivity) {
        queue.async { [weak self] in
            guard let self, self.ensureConnected() else { return }
            self.send(opcode: .frame, payload: self.activityPayload(activity))
        }
    }

    /// Remove the presence, keeping the connection.
    public func clearActivity() {
        queue.async { [weak self] in
            guard let self, self.isConnected else { return }
            self.send(opcode: .frame, payload: self.activityPayload(nil))
        }
    }

    /// Best-effort clear + close. Safe to call from a termination handler:
    /// bounded by short socket timeouts.
    public func shutdownSync() {
        queue.sync {
            if isConnected {
                send(opcode: .frame, payload: activityPayload(nil))
            }
            dropConnection()
        }
    }

    // MARK: - Connection (queue only, except where noted)

    private var isConnected: Bool { lock.withLock { fd != -1 } }

    /// True when connected (connecting first if needed).
    private func ensureConnected() -> Bool {
        if isConnected { return true }
        return tryConnect()
    }

    private func tryConnect() -> Bool {
        for path in candidatePaths() {
            let fd = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
            guard fd != -1 else { continue }
            setTimeouts(fd, seconds: 2)
            setNoSigPipe(fd)
            guard connectTo(fd, path: path) else { Darwin.close(fd); continue }
            guard handshake(fd) else { Darwin.close(fd); continue }
            lock.withLock { self.fd = fd }
            // No subscriptions, so the server only ever talks on close (EOF).
            // A dedicated thread (not a dispatch queue) blocks for that moment.
            Thread { [weak self] in self?.drain(fd: fd) }.start()
            return true
        }
        return false
    }

    private func handshake(_ fd: Int32) -> Bool {
        guard sendRaw(fd, opcode: .handshake,
                      payload: ["v": 1, "client_id": clientID]) else { return false }
        // Expect DISPATCH READY; anything else means this isn't Discord.
        guard let (_, obj) = readFrame(fd), obj["evt"] as? String == "READY" else { return false }
        // Handshake done: block indefinitely from here; EOF means Discord went away.
        setTimeouts(fd, seconds: 0)
        return true
    }

    /// Blocks until the peer closes, then drops the connection.
    private func drain(fd: Int32) {
        while readFrame(fd) != nil {}
        lock.withLock {
            if self.fd == fd {
                Darwin.close(self.fd)
                self.fd = -1
            }
        }
    }

    @discardableResult
    private func send(opcode: Opcode, payload: [String: Any]) -> Bool {
        let current = lock.withLock { fd }
        guard current != -1, sendRaw(current, opcode: opcode, payload: payload) else {
            dropConnection()
            return false
        }
        return true
    }

    private func dropConnection() {
        lock.withLock {
            if fd != -1 { Darwin.close(fd); fd = -1 }
        }
    }

    // MARK: - Payloads

    private func activityPayload(_ activity: DiscordActivity?) -> [String: Any] {
        var args: [String: Any] = ["pid": Int(Darwin.getpid())]
        if let a = activity {
            var act: [String: Any] = ["details": String(a.details.prefix(100))]
            if let s = a.state, !s.isEmpty { act["state"] = String(s.prefix(100)) }
            if let t = a.startTimestamp { act["timestamps"] = ["start": t] }
            var assets: [String: Any] = [:]
            if let k = a.largeImageKey { assets["large_image"] = k }
            if let t = a.largeImageText { assets["large_text"] = t }
            if let k = a.smallImageKey { assets["small_image"] = k }
            if let t = a.smallImageText { assets["small_text"] = t }
            if !assets.isEmpty { act["assets"] = assets }
            if let b = a.buttons, !b.isEmpty {
                act["buttons"] = b.prefix(2).map { ["label": String($0.label.prefix(32)),
                                                     "url": $0.url] }
            }
            args["activity"] = act
        } else {
            args["activity"] = NSNull()
        }
        return ["cmd": "SET_ACTIVITY", "args": args, "nonce": UUID().uuidString]
    }

    // MARK: - Sockets

    private func candidatePaths() -> [String] {
        var dirs: [String] = []
        let env = ProcessInfo.processInfo.environment
        if let t = env["TMPDIR"], !t.isEmpty { dirs.append(t) }
        if let x = env["XDG_RUNTIME_DIR"], !x.isEmpty { dirs.append(x) }
        dirs.append("/tmp")
        dirs.append(FileManager.default.temporaryDirectory.path)
        var seen = Set<String>()
        var out: [String] = []
        for d in dirs where seen.insert(d).inserted {
            for i in 0..<10 {
                out.append((d as NSString).appendingPathComponent("discord-ipc-\(i)"))
            }
        }
        return out
    }

    private func connectTo(_ fd: Int32, path: String) -> Bool {
        var addr = sockaddr_un()
        memset(&addr, 0, MemoryLayout<sockaddr_un>.size)
        addr.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8)
        // sun_path is 104 bytes; leave room for NUL.
        guard bytes.count < 104 else { return false }
        withUnsafeMutableBytes(of: &addr.sun_path) { dst in
            guard let base = dst.baseAddress else { return }
            bytes.withUnsafeBufferPointer { src in
                base.copyMemory(from: src.baseAddress!, byteCount: bytes.count)
            }
            base.advanced(by: bytes.count).storeBytes(of: 0, as: UInt8.self)
        }
        return withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) == 0
            }
        }
    }

    private func setTimeouts(_ fd: Int32, seconds: Int) {
        var tv = timeval(tv_sec: seconds, tv_usec: 0)
        withUnsafePointer(to: &tv) { ptr in
            _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, ptr,
                                   socklen_t(MemoryLayout<timeval>.size))
            _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, ptr,
                                   socklen_t(MemoryLayout<timeval>.size))
        }
    }

    private func setNoSigPipe(_ fd: Int32) {
        var on: Int32 = 1
        _ = Darwin.setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on,
                               socklen_t(MemoryLayout<Int32>.size))
    }

    @discardableResult
    private func sendRaw(_ fd: Int32, opcode: Opcode, payload: [String: Any]) -> Bool {
        guard let body = try? JSONSerialization.data(withJSONObject: payload) else { return false }
        var header = Data(count: 8)
        header.withUnsafeMutableBytes { (dst: UnsafeMutableRawBufferPointer) in
            dst.storeBytes(of: opcode.rawValue.littleEndian, as: Int32.self)
            dst.storeBytes(of: Int32(body.count).littleEndian, toByteOffset: 4, as: Int32.self)
        }
        return writeAll(fd, header) && writeAll(fd, body)
    }

    private func writeAll(_ fd: Int32, _ data: Data) -> Bool {
        var sent = 0
        while sent < data.count {
            let n = data.withUnsafeBytes { (ptr: UnsafeRawBufferPointer) -> Int in
                Darwin.write(fd, ptr.baseAddress!.advanced(by: sent), data.count - sent)
            }
            if n > 0 { sent += n; continue }
            if n == -1 && errno == EINTR { continue }
            return false
        }
        return true
    }

    /// One framed message, or nil on EOF/error/timeout.
    private func readFrame(_ fd: Int32) -> (Int32, [String: Any])? {
        guard let header = readExact(fd, 8), header.count == 8 else { return nil }
        let opcode: Int32 = header.withUnsafeBytes { $0.load(as: Int32.self).littleEndian }
        let length: Int32 = header.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self).littleEndian }
        guard length >= 0, length < 1024 * 1024 else { return nil }
        guard let body = readExact(fd, Int(length)), body.count == length else { return nil }
        guard let obj = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return nil }
        return (opcode, obj)
    }

    private func readExact(_ fd: Int32, _ count: Int) -> Data? {
        var out = Data(count: count)
        var got = 0
        while got < count {
            let n = out.withUnsafeMutableBytes { (ptr: UnsafeMutableRawBufferPointer) -> Int in
                Darwin.read(fd, ptr.baseAddress!.advanced(by: got), count - got)
            }
            if n > 0 { got += n; continue }
            return nil // EOF, error, or (handshake-only) timeout
        }
        return out
    }
}
