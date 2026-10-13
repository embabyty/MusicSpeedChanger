import SwiftUI
import MSCCore

/// Graphical 31-band editor card (20 Hz – 20 kHz), embedded in the Player
/// under the AB Loop card. The curve shows the combined response of the
/// 31 peaking filters; drag any node vertically to set its band gain.
struct EqualizerCard: View {
    @EnvironmentObject var engine: AudioEngine

    @State private var selectedPreset = "Flat"
    @State private var activeBand: Int?
    @State private var touchedBand: Int?

    var body: some View {
        Card("Graphical EQ", content: {
            HStack(spacing: 12) {
                Toggle("Enable EQ", isOn: $engine.eqEnabled)
                Text("Preset:").foregroundStyle(.secondary)
                Picker("Preset", selection: Binding(
                    get: { selectedPreset },
                    set: { name in
                        if let p = EQBands.presets.first(where: { $0.name == name }) {
                            engine.eqGains = p.gains
                        }
                        selectedPreset = name
                    })) {
                    ForEach(EQBands.presets, id: \.name) { Text($0.name).tag($0.name) }
                    Text("Custom").tag("Custom")
                }
                .labelsHidden()
                .frame(width: 150)
                Button("Flat") {
                    engine.eqGains = EQBands.presets[0].gains
                    selectedPreset = "Flat"
                }
                .buttonStyle(.glass)
                if let band = touchedBand {
                    Text("\(EQBands.shortLabels[band]) Hz: \(String(format: "%+.1f dB", engine.eqGains[band]))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                } else {
                    Text("Drag nodes to adjust ±15 dB")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            EQCurveEditor(gains: engine.eqGains,
                          sampleRate: engine.currentSampleRate,
                          activeBand: $activeBand,
                          onAdjust: { band, gain in
                              engine.eqGains[band] = gain
                              touchedBand = band
                              selectedPreset = "Custom"
                          })
            .frame(minHeight: 300)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
        })
    }
}

// MARK: - Curve editor

/// Log-frequency response plot with draggable band nodes.
struct EQCurveEditor: View {
    let gains: [Float]
    let sampleRate: Double
    @Binding var activeBand: Int?
    let onAdjust: (Int, Float) -> Void

    private let minFreq = 20.0
    private let maxFreq = 20000.0
    private let maxDb = 16.0

    var body: some View {
        GeometryReader { geo in
            Canvas { context, size in
                drawGrid(context: context, size: size)
                drawCurve(context: context, size: size)
                drawNodes(context: context, size: size)
            }
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        let size = geo.size
                        guard size.width > 0, size.height > 0 else { return }
                        if activeBand == nil {
                            activeBand = nearestBand(to: value.location.x, width: size.width)
                        }
                        guard let band = activeBand else { return }
                        let gain = Float(min(max(yToDb(value.location.y, height: size.height),
                                                 Double(EQBands.minGainDb)),
                                           Double(EQBands.maxGainDb)))
                        onAdjust(band, gain)
                    }
                    .onEnded { _ in activeBand = nil }
            )
        }
    }

    // MARK: Geometry

    private func xPos(_ freq: Double, width: Double) -> Double {
        let lo = log10(minFreq), hi = log10(maxFreq)
        return (log10(max(minFreq, min(maxFreq, freq))) - lo) / (hi - lo) * width
    }

    private func yPos(_ db: Double, height: Double) -> Double {
        height / 2 - db / maxDb * (height / 2 - 8)
    }

    private func yToDb(_ y: Double, height: Double) -> Double {
        (height / 2 - y) / (height / 2 - 8) * maxDb
    }

    private func nearestBand(to x: Double, width: Double) -> Int? {
        var best: Int?
        var bestDist = 30.0
        for band in 0..<EQBands.bandCount {
            let d = abs(x - xPos(Double(EQBands.frequencies[band]), width: width))
            if d < bestDist { bestDist = d; best = band }
        }
        return best
    }

    // MARK: Response math (RBJ peaking magnitude, cascaded → dB adds)

    private func response(at freq: Double) -> Double {
        var db = 0.0
        for band in 0..<EQBands.bandCount {
            let gain = Double(gains[band])
            if abs(gain) < 0.001 { continue }
            let fc = Double(EQBands.frequencies[band])
            if fc >= sampleRate * 0.45 { continue }
            db += peakingDb(freq: freq, center: fc, q: 4.318, gainDb: gain)
        }
        return db
    }

    private func peakingDb(freq: Double, center: Double, q: Double, gainDb: Double) -> Double {
        let a = pow(10.0, gainDb / 40.0)
        let w0 = 2 * Double.pi * center / sampleRate
        let alpha = sin(w0) / (2 * q)
        let cosW = cos(w0)
        let b0 = 1 + alpha * a, b1 = -2 * cosW, b2 = 1 - alpha * a
        let a0 = 1 + alpha / a, a1 = -2 * cosW, a2 = 1 - alpha / a
        let w = 2 * Double.pi * freq / sampleRate
        let cosW1 = cos(w), sinW1 = sin(w)
        let cosW2 = cos(2 * w), sinW2 = sin(2 * w)
        // H = (b0 + b1·e^-jw + b2·e^-j2w) / (a0 + a1·e^-jw + a2·e^-j2w)
        let numRe = b0 + b1 * cosW1 + b2 * cosW2
        let numIm = -(b1 * sinW1 + b2 * sinW2)
        let denRe = a0 + a1 * cosW1 + a2 * cosW2
        let denIm = -(a1 * sinW1 + a2 * sinW2)
        let mag = sqrt(numRe * numRe + numIm * numIm) / max(sqrt(denRe * denRe + denIm * denIm), 1e-9)
        return 20 * log10(max(mag, 1e-9))
    }

    // MARK: Drawing

    private func drawGrid(context: GraphicsContext, size: CGSize) {
        let w = Double(size.width), h = Double(size.height)
        var grid = Path()
        // Octave + decade lines
        var f = 20.0
        while f <= maxFreq {
            let x = xPos(f, width: w)
            grid.move(to: CGPoint(x: x, y: 0))
            grid.addLine(to: CGPoint(x: x, y: h))
            f *= 2
        }
        // 0 dB + ±5/±10 dB lines
        for db in [-10.0, -5.0, 0.0, 5.0, 10.0] {
            let y = yPos(db, height: h)
            grid.move(to: CGPoint(x: 0, y: y))
            grid.addLine(to: CGPoint(x: w, y: y))
        }
        context.stroke(grid, with: .color(.white.opacity(0.12)), lineWidth: 1)
        // Frequency labels
        for label in [(20, "20"), (50, "50"), (100, "100"), (200, "200"), (500, "500"),
                      (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k"), (20000, "20k")] {
            context.draw(Text(label.1).font(.system(size: 9)).foregroundStyle(.secondary),
                         at: CGPoint(x: xPos(Double(label.0), width: w), y: h - 8))
        }
    }

    private func drawCurve(context: GraphicsContext, size: CGSize) {
        let w = Double(size.width), h = Double(size.height)
        let steps = 160
        var path = Path()
        for i in 0...steps {
            let frac = Double(i) / Double(steps)
            let freq = minFreq * pow(maxFreq / minFreq, frac)
            let x = frac * w
            let y = min(max(yPos(response(at: freq), height: h), 0), h)
            if i == 0 { path.move(to: CGPoint(x: x, y: y)) }
            else { path.addLine(to: CGPoint(x: x, y: y)) }
        }
        context.stroke(path, with: .color(.white), lineWidth: 2)
        // Fill under the curve
        var fill = path
        fill.addLine(to: CGPoint(x: w, y: h))
        fill.addLine(to: CGPoint(x: 0, y: h))
        fill.closeSubpath()
        context.fill(fill, with: .linearGradient(
            Gradient(colors: [.accentColor.opacity(0.35), .clear]),
            startPoint: CGPoint(x: 0, y: 0), endPoint: CGPoint(x: 0, y: h)))
    }

    private func drawNodes(context: GraphicsContext, size: CGSize) {
        let w = Double(size.width), h = Double(size.height)
        for band in 0..<EQBands.bandCount {
            let x = xPos(Double(EQBands.frequencies[band]), width: w)
            let y = yPos(Double(gains[band]), height: h)
            let isActive = band == activeBand
            let r: Double = isActive ? 7 : 4.5
            let circle = Path(ellipseIn: CGRect(x: x - r, y: y - r, width: r * 2, height: r * 2))
            if isActive {
                context.fill(circle, with: .color(.accentColor))
            } else {
                context.fill(circle, with: .color(Color(white: 0.85)))
                context.stroke(circle, with: .color(.black.opacity(0.4)), lineWidth: 1)
            }
        }
    }
}
