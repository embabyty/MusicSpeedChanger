import SwiftUI
import MSCCore

/// Waveform display with playhead, loop-region highlight, click/drag to seek.
/// Zoom levels mirror the iOS app: 5 shows the whole file with a moving
/// playhead; above 5 the playhead pins to the center and the waveform
/// scrolls beneath it through a narrowing time window.
struct WaveformView: View {
    let data: WaveformData?
    let duration: Double
    let position: Double
    let loopA: Double?
    let loopB: Double?
    let loopEnabled: Bool
    /// 0…10 (5 = natural scale). Drives vertical zoom and follow-window width.
    let zoomLevel: Int
    let onSeek: (Double) -> Void

    private var amplitudeScale: Double { Double(zoomLevel) / 5.0 }
    private var follow: Bool { zoomLevel > 5 }

    /// Visible bucket range. Whole file at level ≤ 5, narrowing window above.
    private func window(total: Int) -> (start: Double, count: Double) {
        guard total > 0 else { return (0, 1) }
        if !follow || duration <= 0 { return (0, Double(total)) }
        let fraction = 5.0 / Double(zoomLevel)
        let count = max(Double(total) * fraction, 1)
        let center = position / duration * Double(total)
        let start = min(max(center - count / 2, 0), max(Double(total) - count, 0))
        return (start, count)
    }

    private func xForBucket(_ bucket: Double, start: Double, count: Double, width: Double) -> Double {
        (bucket - start) / count * width
    }

    private func secondsForX(_ x: Double, width: Double, total: Int) -> Double {
        guard width > 0, total > 0, duration > 0 else { return 0 }
        let (start, count) = window(total: total)
        let bucket = start + min(max(x / width, 0), 1) * count
        return min(max(bucket / Double(total) * duration, 0), duration)
    }

    var body: some View {
        GeometryReader { geo in
            Canvas { context, size in
                let peaks = data?.peaks ?? []
                let total = peaks.count
                let (start, count) = window(total: total)

                // background
                context.fill(Path(CGRect(origin: .zero, size: size)),
                             with: .color(Color(white: 0.10)))

                // A/B badges appear as soon as each point is set; the region
                // highlight needs a valid pair with the loop switched on.
                if duration > 0 {
                    let totalD = Double(total)
                    func xForSeconds(_ seconds: Double) -> Double {
                        xForBucket(seconds / duration * totalD,
                                   start: start, count: count, width: Double(size.width))
                    }
                    func badge(_ label: String, seconds: Double) {
                        let mx = xForSeconds(seconds)
                        guard mx >= -13, mx <= Double(size.width) + 13 else { return }
                        let cx = min(max(mx, 12), Double(size.width) - 12)
                        let cy = Double(size.height) / 2
                        context.fill(Path(CGRect(x: cx - 0.75, y: 0, width: 1.5,
                                                 height: Double(size.height))),
                                     with: .color(.accentColor))
                        let r = 11.0
                        context.fill(Path(ellipseIn: CGRect(x: cx - r, y: cy - r,
                                                            width: r * 2, height: r * 2)),
                                     with: .color(.accentColor))
                        context.draw(Text(label).font(.system(size: 11, weight: .bold))
                                        .foregroundColor(.white),
                                     at: CGPoint(x: cx, y: cy))
                    }
                    if let a = loopA, let b = loopB, b > a, loopEnabled {
                        let x0 = xForSeconds(a)
                        let x1 = xForSeconds(b)
                        let clamped = CGRect(x: max(x0, 0), y: 0,
                                             width: min(x1, Double(size.width)) - max(x0, 0),
                                             height: Double(size.height))
                        if clamped.width > 0 {
                            context.fill(Path(clamped),
                                         with: .color(Color(red: 1, green: 0.76, blue: 0.03).opacity(0.15)))
                        }
                    }
                    if let a = loopA { badge("A", seconds: a) }
                    if let b = loopB { badge("B", seconds: b) }
                }

                // waveform bars sampled through the visible window
                if total > 0 {
                    let bars = min(total, max(Int(size.width), 1))
                    let barWidth = size.width / CGFloat(bars)
                    for i in 0..<bars {
                        let frac = (Double(i) + 0.5) / Double(bars)
                        let bucket = min(Int(start + frac * count), total - 1)
                        let peak = min(peaks[max(bucket, 0)] * Float(amplitudeScale), 1)
                        let h = max(CGFloat(peak) * size.height * 0.9, 1)
                        let rect = CGRect(x: CGFloat(i) * barWidth,
                                          y: (size.height - h) / 2,
                                          width: max(barWidth - 1, 1), height: h)
                        context.fill(Path(rect), with: .color(Color(red: 0.49, green: 0.62, blue: 1.0).opacity(0.85)))
                    }
                }

                // playhead: travels at level ≤ 5, pinned center when following.
                // Hidden until a file is loaded so an empty waveform stays clean.
                if duration > 0 {
                    let x: Double = {
                        if follow { return Double(size.width) / 2 }
                        return Double(size.width) * position / duration
                    }()
                    context.fill(Path(CGRect(x: x - 1, y: 0, width: 2, height: Double(size.height))),
                                 with: .color(.white))
                }
            }
            .gesture(DragGesture(minimumDistance: 0).onChanged { value in
                guard duration > 0 else { return }
                let x = min(max(value.location.x, 0), geo.size.width)
                onSeek(secondsForX(x, width: Double(geo.size.width), total: data?.peaks.count ?? 0))
            })
        }
    }
}
