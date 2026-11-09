import Foundation

/// Lenient semantic-version comparison for release tags.
/// Handles `1.0`, `v1.0.0`, and prerelease suffixes such as `1.0.0-devbeta.1`:
/// a version with a prerelease suffix is older than the same core without one.
/// Release channel derived from a version or tag string.
public enum ReleaseChannel: Sendable {
    case stable
    case devbeta
    case publicbeta

    public static func of(_ versionOrTag: String) -> ReleaseChannel {
        let v = versionOrTag.lowercased()
        if v.contains("publicbeta") { return .publicbeta }
        if v.contains("beta") { return .devbeta }
        return .stable
    }
}

public enum AppVersion {
    /// Strips leading `v`s (`v1.0.0` → `1.0.0`). Leading-only on purpose:
    /// `trimmingCharacters` would also eat a meaningful trailing `v`.
    public static func normalized(_ tag: String) -> String {
        var v = tag
        while v.hasPrefix("v") || v.hasPrefix("V") { v.removeFirst() }
        return v
    }

    public static func isNewer(_ a: String, than b: String) -> Bool {
        compare(a, b) == .orderedDescending
    }

    public static func compare(_ a: String, _ b: String) -> ComparisonResult {
        let (ac, ap) = split(normalized(a))
        let (bc, bp) = split(normalized(b))
        for i in 0..<max(ac.count, bc.count) {
            let x = i < ac.count ? ac[i] : 0
            let y = i < bc.count ? bc[i] : 0
            if x != y { return x < y ? .orderedAscending : .orderedDescending }
        }
        // Cores equal: plain release beats prerelease.
        switch (ap, bp) {
        case (nil, nil): return .orderedSame
        case (nil, _): return .orderedDescending
        case (_, nil): return .orderedAscending
        case let (x?, y?): return compareIdentifiers(x, y)
        }
    }

    private static func split(_ v: String) -> (core: [Int], prerelease: [String]?) {
        let halves = v.split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
        let core = halves[0].split(separator: ".").map { Int($0) ?? 0 }
        let pre: [String]? = halves.count > 1 ? halves[1].split(separator: ".").map(String.init) : nil
        return (core, pre)
    }

    private static func compareIdentifiers(_ a: [String], _ b: [String]) -> ComparisonResult {
        for i in 0..<max(a.count, b.count) {
            if i >= a.count { return .orderedAscending }
            if i >= b.count { return .orderedDescending }
            let r = compareIdentifier(a[i], b[i])
            if r != .orderedSame { return r }
        }
        return .orderedSame
    }

    private static func compareIdentifier(_ a: String, _ b: String) -> ComparisonResult {
        // SemVer: numeric identifiers sort below alphanumeric ones.
        switch (Int(a), Int(b)) {
        case let (x?, y?): if x != y { return x < y ? .orderedAscending : .orderedDescending }
        case (_?, nil): return .orderedAscending
        case (nil, _?): return .orderedDescending
        default: break
        }
        if a != b { return a < b ? .orderedAscending : .orderedDescending }
        return .orderedSame
    }
}
