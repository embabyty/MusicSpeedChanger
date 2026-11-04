import Foundation

/// 32-band ISO 1/3-octave center frequencies (Hz).
public enum EQBands {
    public static let bandCount = 32
    public static let frequencies: [Float] = [
        16, 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500,
        630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
        10000, 12500, 16000, 20000,
    ]
    public static let shortLabels: [String] = [
        "16", "20", "25", "31", "40", "50", "63", "80", "100", "125", "160", "200",
        "250", "315", "400", "500", "630", "800", "1k", "1.25k", "1.6k", "2k",
        "2.5k", "3.15k", "4k", "5k", "6.3k", "8k", "10k", "12.5k", "16k", "20k",
    ]
    public static let minGainDb: Float = -15
    public static let maxGainDb: Float = 15

    /// Existing 31-band curves, retained as the basis for the 32-band set.
    private static let legacyPresets: [(name: String, gains: [Float])] = [
        ("Flat", Array(repeating: 0, count: 31)),
        ("Rock", [5, 5, 4, 4, 3, 3, 2, 1, 0, -1, -2, -2, -1, -1, 0, 0, 1, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 5, 5, 5, 6]),
        ("Pop", [3, 3, 2, 2, 1, 1, 0, 0, -1, -1, -1, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 3, 3, 3]),
        ("Jazz", [4, 4, 3, 3, 2, 2, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 2, 2, 2, 1, 1]),
        ("Classical", [4, 3, 3, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5, 5]),
        ("Dance", [7, 6, 6, 5, 5, 4, 3, 2, 1, 0, -1, -1, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 5, 5, 4]),
        ("Bass Boost", [8, 8, 7, 7, 6, 6, 5, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        ("Treble Boost", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 5, 6, 7, 7, 8]),
        ("Vocal", [-3, -3, -2, -2, -1, -1, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 4, 3, 3, 2, 2, 1, 1, 0, 0, 0]),
    ]

    /// Curves include the new 16 Hz band, initialized from the former 20 Hz
    /// point so old presets retain their original shape.
    public static let presets: [(name: String, gains: [Float])] =
        legacyPresets.map { ($0.name, [$0.gains[0]] + $0.gains) } + [
            ("Loudness", [5, 5, 5, 4, 5, 4, 3, 2, 1, 0, -1, -1, 0, 0, 0, 0,
                          0, 0, 0, 0, 0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 4, 5]),
        ]
}
