import Foundation
import AVFoundation
import Combine
import AppKit

/// Queue repeat behavior for end-of-track.
public enum RepeatMode: String, Codable, CaseIterable, Sendable {
    case off, all, one

    /// Button cycles off → all → one → off.
    public var next: RepeatMode {
        switch self {
        case .off: return .all
        case .all: return .one
        case .one: return .off
        }
    }
}

public enum AudioExportFormat: String, CaseIterable, Identifiable, Sendable {
    case wav, m4a

    public var id: Self { self }
    public var fileExtension: String { rawValue }

    public var displayName: String {
        switch self {
        case .wav: "WAV"
        case .m4a: "M4A"
        }
    }
}

/// Playback engine: decode + independent tempo/pitch (AVAudioUnitTimePitch) +
/// 31-band EQ (two chained AVAudioUnitEQ nodes, 16 + 15 bands) +
/// distortion → delay → reverb inserts + limiter.
/// Position is tracked on the *source* stream; effective output duration = source / tempo.
@MainActor
public final class AudioEngine: ObservableObject {
    // MARK: Published state
    @Published public var fileName: String?
    @Published public var fileURL: URL?
    @Published public var isLoaded = false
    @Published public var isPlaying = false
    @Published public var duration: Double = 0
    @Published public var position: Double = 0
    @Published public var waveform: WaveformData?

    /// 50–200 % (AVAudioUnitTimePitch range). Pitch is preserved.
    @Published public var tempoPercent: Double = 100 {
        didSet { timePitch.rate = Float(tempoPercent / 100) }
    }
    /// -12…+12 semitones in 0.1 steps. Tempo is preserved.
    @Published public var pitchSemitones: Double = 0 {
        didSet { timePitch.pitch = Float(pitchSemitones * 100) }
    }
    @Published public var volumePercent: Double = 80 {
        didSet { engine?.mainMixerNode.outputVolume = Float(volumePercent / 100) }
    }
    @Published public var reverseEnabled = false
    @Published public var eqEnabled = true { didSet { applyEQ() } }
    @Published public var reverbEnabled = false { didSet { applyReverb() } }
    @Published public var reverbMix: Float = 30 { didSet { applyReverb() } }
    @Published public var delayEnabled = false { didSet { applyDelay() } }
    @Published public var delayMix: Float = 30 { didSet { applyDelay() } }
    /// Seconds, 0…2 (AVAudioUnitDelay range).
    @Published public var delayTime: Double = 0.35 { didSet { applyDelay() } }
    /// Percent, 0…100.
    @Published public var delayFeedback: Float = 40 { didSet { applyDelay() } }
    @Published public var distortionEnabled = false { didSet { applyDistortion() } }
    @Published public var distortionMix: Float = 50 { didSet { applyDistortion() } }
    /// dB, -80…20 (AVAudioUnitDistortion preGain).
    @Published public var distortionGain: Float = 0 { didSet { applyDistortion() } }
    @Published public var eqGains: [Float] = Array(repeating: 0, count: EQBands.bandCount) {
        didSet {
            if eqGains.count != EQBands.bandCount {
                eqGains = Array(repeating: 0, count: EQBands.bandCount)
                return
            }
            applyEQ()
        }
    }
    @Published public var loopEnabled = false
    @Published public var loopA: Double?
    @Published public var loopB: Double?

    // Queue modes.
    @Published public var shuffleEnabled = false
    @Published public var repeatMode: RepeatMode = .off
    /// Fired (on the main actor) when a non-looping segment plays to its
    /// natural end. The engine is already stopped; the hook decides whether
    /// to replay or advance the queue.
    public var onTrackEnded: (() -> Void)?

    public var eqIsFlat: Bool {
        eqGains.allSatisfy { abs($0) <= 0.001 }
    }

    /// Sample rate of the loaded file (drives EQ curve rendering).
    public var currentSampleRate: Double { format?.sampleRate ?? 44100 }

    /// "plays as 2:00 @ 150%" — effective output duration.
    public var effectiveDurationText: String {
        guard duration > 0 else { return "" }
        let eff = duration / (tempoPercent / 100)
        return String(format: "(plays as %d:%02d @ %.0f%%)", Int(eff) / 60, Int(eff) % 60, tempoPercent)
    }

    public var loopIsValid: Bool {
        guard let a = loopA, let b = loopB else { return false }
        return b > a && duration > 0
    }

    // MARK: Audio graph
    private var engine: AVAudioEngine?
    private let player = AVAudioPlayerNode()
    private let timePitch = AVAudioUnitTimePitch()
    private let eqA = AVAudioUnitEQ(numberOfBands: 16)
    private let eqB = AVAudioUnitEQ(numberOfBands: 15)
    private let distortion = AVAudioUnitDistortion()
    private let delay = AVAudioUnitDelay()
    private let reverb = AVAudioUnitReverb()
    private let limiter: AVAudioUnitEffect

    private var sourceBuffer: AVAudioPCMBuffer?
    private var cachedReversed: AVAudioPCMBuffer?
    private var format: AVAudioFormat?

    // Position tracking (source seconds)
    private var timer: Timer?
    private var lastTick: CFAbsoluteTime = 0
    private var segmentSourceStart: Double = 0 // where the current scheduled slice starts (source seconds)
    private var segmentSourceLength: Double = 0
    private var accumulated: Double = 0 // source seconds rendered since last schedule
    private var segmentLooping = false
    private var segmentReversed = false

    public init() {
        limiter = AudioEngine.makeDynamicsProcessor()
        configureEQBands(eqA, start: 0)
        configureEQBands(eqB, start: 16)
        timePitch.rate = 1
        // Max overlap = best phase-vocoder quality for pitch shifts.
        // Default is 8; 32 costs more CPU but audibly reduces warble,
        // metallic ringing, and transient smearing when pitched.
        timePitch.overlap = 32
        // Digital inserts, Audiotool-style: distortion → time effects → limiter.
        // All wet/dry at 0 when disabled so bypass is click-free and transparent.
        distortion.loadFactoryPreset(.multiDistortedCubed)
        distortion.preGain = 0
        distortion.wetDryMix = 0
        delay.delayTime = 0.35
        delay.feedback = 40
        delay.lowPassCutoff = 15000
        delay.wetDryMix = 0
        reverb.loadFactoryPreset(.mediumHall)
        reverb.wetDryMix = 0
        engine = AVAudioEngine()
    }

    /// Apple's AUDynamicsProcessor as an AVAudioUnitEffect (transparent peak limiter).
    private static func makeDynamicsProcessor() -> AVAudioUnitEffect {
        let desc = AudioComponentDescription(
            componentType: kAudioUnitType_Effect,
            componentSubType: kAudioUnitSubType_DynamicsProcessor,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0,
            componentFlagsMask: 0)
        return AVAudioUnitEffect(audioComponentDescription: desc)
    }

    private func configureEQBands(_ eq: AVAudioUnitEQ, start: Int) {
        for i in 0..<eq.bands.count {
            let band = eq.bands[i]
            band.filterType = .parametric
            band.frequency = EQBands.frequencies[start + i]
            band.bandwidth = 1.0 / 3.0 // ~1/3 octave
            band.gain = 0
            // A flat parametric still adds phase shift; bypass it so a flat
            // EQ is bit-transparent and doesn't stack artifacts on pitched audio.
            band.bypass = true
        }
    }

    private func applyEQ() {
        let gains = eqEnabled ? eqGains : Array(repeating: Float(0), count: EQBands.bandCount)
        for i in 0..<eqA.bands.count {
            eqA.bands[i].gain = gains[i]
            eqA.bands[i].bypass = abs(gains[i]) <= 0.001
        }
        for i in 0..<eqB.bands.count {
            eqB.bands[i].gain = gains[16 + i]
            eqB.bands[i].bypass = abs(gains[16 + i]) <= 0.001
        }
    }

    /// Wet/dry 0 when off = bit-transparent dry pass, no clicks from rewiring.
    private func applyReverb() {
        reverb.wetDryMix = reverbEnabled ? min(max(reverbMix, 0), 100) : 0
    }

    private func applyDelay() {
        delay.delayTime = min(max(delayTime, 0), 2)
        delay.feedback = min(max(delayFeedback, 0), 100)
        delay.wetDryMix = delayEnabled ? min(max(delayMix, 0), 100) : 0
    }

    private func applyDistortion() {
        distortion.preGain = min(max(distortionGain, -80), 20)
        distortion.wetDryMix = distortionEnabled ? min(max(distortionMix, 0), 100) : 0
    }

    public var effectsActive: Bool {
        (reverbEnabled && reverbMix > 0.001)
            || (delayEnabled && delayMix > 0.001)
            || (distortionEnabled && distortionMix > 0.001)
    }

    // MARK: Loading

    public func load(url: URL) throws {
        stop()
        let file = try AVAudioFile(forReading: url)
        guard file.length > 0 else {
            throw NSError(domain: "MSC", code: 3, userInfo: [NSLocalizedDescriptionKey:
                "Could not decode audio (empty file): \(url.lastPathComponent)"])
        }
        format = file.processingFormat
        let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat,
                                      frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: buffer)
        sourceBuffer = buffer
        cachedReversed = Self.reversedBuffer(buffer)
        duration = Double(buffer.frameLength) / buffer.format.sampleRate
        position = 0
        loopA = nil; loopB = nil; loopEnabled = false
        fileName = url.lastPathComponent
        fileURL = url
        isLoaded = true
        buildGraph()
        applyEQ()
        applyReverb()
        applyDelay()
        applyDistortion()
    }

    /// Rebuild the graph for the loaded file. Nodes are detached from the
    /// previous engine first — attaching a node that is still attached
    /// elsewhere raises an ObjC exception (instant crash, not a Swift error).
    private func buildGraph() {
        guard let format else { return }
        if let old = engine {
            old.stop()
            for node: AVAudioNode in [player, timePitch, eqA, eqB, distortion, delay, reverb, limiter] {
                if node.engine === old { old.detach(node) }
            }
        }
        let engine = AVAudioEngine()
        engine.attach(player)
        engine.attach(timePitch)
        engine.attach(eqA)
        engine.attach(eqB)
        engine.attach(distortion)
        engine.attach(delay)
        engine.attach(reverb)
        engine.attach(limiter)
        engine.connect(player, to: timePitch, format: format)
        engine.connect(timePitch, to: eqA, format: format)
        engine.connect(eqA, to: eqB, format: format)
        engine.connect(eqB, to: distortion, format: format)
        engine.connect(distortion, to: delay, format: format)
        engine.connect(delay, to: reverb, format: format)
        engine.connect(reverb, to: limiter, format: format)
        engine.connect(limiter, to: engine.mainMixerNode, format: format)
        engine.mainMixerNode.outputVolume = Float(volumePercent / 100)
        try? engine.start()
        self.engine = engine
    }

    // MARK: Transport

    public func play() {
        guard let format else { return }
        if reverseEnabled {
            if loopEnabled, loopIsValid, let a = loopA, let b = loopB {
                // AB loop in reverse: play B→A, then jump back to B.
                playReversedLoop(a: a, b: b)
            } else {
                playReversed()
            }
            return
        }
        if loopEnabled, loopIsValid, let a = loopA, let b = loopB {
            // AB loop on: jump to A and loop the section.
            startForwardSegment(from: a, length: b - a, looping: true)
            return
        }
        startForwardSegment(from: position, length: duration - position, looping: false)
    }

    private func startForwardSegment(from seconds: Double, length: Double, looping: Bool) {
        guard let buffer = sourceBuffer, let format else { return }
        let sampleRate = format.sampleRate
        let startFrame = AVAudioFrameCount(max(0, min(seconds, duration)) * sampleRate)
        let frames = AVAudioFrameCount(max(0, min(length, duration - seconds)) * sampleRate)
        guard frames > 0 else { return }
        let slice = Self.slice(buffer, from: startFrame, length: frames)
        player.stop()
        segmentSourceStart = Double(startFrame) / sampleRate
        segmentSourceLength = Double(frames) / sampleRate
        accumulated = 0
        segmentLooping = looping
        segmentReversed = false
        player.scheduleBuffer(slice, at: nil, options: looping ? .loops : [])
        ensureEngineRunning()
        player.play()
        isPlaying = true
        startTimer()
    }

    /// Loop the [a, b] section backwards: the reversed slice covering the
    /// section is scheduled with .loops, so audio runs B→A then jumps to B.
    private func playReversedLoop(a: Double, b: Double) {
        guard let buffer = cachedReversed, let format else { return }
        let sampleRate = format.sampleRate
        let startFrame = AVAudioFrameCount(max(0, duration - b) * sampleRate)
        let frames = AVAudioFrameCount(max(0, min(b - a, duration)) * sampleRate)
        guard frames > 0 else { return }
        let slice = Self.slice(buffer, from: startFrame, length: frames)
        player.stop()
        segmentSourceStart = a
        segmentSourceLength = Double(frames) / sampleRate
        accumulated = 0
        segmentLooping = true
        segmentReversed = true
        player.scheduleBuffer(slice, at: nil, options: .loops)
        ensureEngineRunning()
        player.play()
        isPlaying = true
        startTimer()
    }

    private func playReversed() {
        guard let buffer = cachedReversed, let format else { return }
        let sampleRate = format.sampleRate
        // Position in reversed stream that mirrors the current source position.
        let reversePos = max(0, duration - position)
        let startFrame = AVAudioFrameCount(min(reversePos, duration) * sampleRate)
        let frames = AVAudioFrameCount(max(0, duration - reversePos) * sampleRate)
        guard frames > 0 else { return }
        let slice = Self.slice(buffer, from: startFrame, length: frames)
        player.stop()
        segmentSourceStart = duration - reversePos
        segmentSourceLength = TimeInterval(frames) / sampleRate
        accumulated = 0
        segmentLooping = false
        segmentReversed = true
        player.scheduleBuffer(slice, at: nil, options: [])
        ensureEngineRunning()
        player.play()
        isPlaying = true
        startTimer()
    }

    /// `prepare()` does not restart a stopped engine — playback after Stop or
    /// end-of-track needs an explicit start or the player runs silently.
    private func ensureEngineRunning() {
        guard let engine, !engine.isRunning else { return }
        try? engine.start()
    }

    public func pause() {
        player.pause()
        isPlaying = false
        stopTimer()
    }

    public func stop() {
        player.stop()
        engine?.stop()
        isPlaying = false
        stopTimer()
        position = 0
        accumulated = 0
    }

    public func seek(to seconds: Double) {
        let target = max(0, min(seconds, duration))
        position = target
        guard isPlaying else { return }
        if segmentLooping, loopIsValid, let a = loopA, let b = loopB {
            if target >= a, target <= b {
                // Stay in the loop, restart at its head (B when reversed, A forward).
                if segmentReversed { playReversedLoop(a: a, b: b) }
                else { startForwardSegment(from: a, length: b - a, looping: true) }
            } else {
                // Left the loop: keep playing linearly from here.
                loopEnabled = false
                if segmentReversed { playReversed() }
                else { startForwardSegment(from: target, length: duration - target, looping: false) }
            }
        } else if segmentReversed {
            playReversed()
        } else {
            startForwardSegment(from: target, length: duration - target, looping: false)
        }
    }

    public func setLoopA() {
        loopA = position
        autoLoop()
    }

    public func setLoopB() {
        loopB = position
        autoLoop()
    }

    /// Setting a point completes the pair → the loop switches on by itself.
    /// A lone or unordered point just parks its marker (loop stays off).
    private func autoLoop() {
        guard loopIsValid else {
            loopEnabled = false
            return
        }
        loopEnabled = true
        loopToggled() // restart playback inside the loop when already playing
    }

    public func clearLoop() {
        loopA = nil; loopB = nil; loopEnabled = false
    }

    /// Called when the loop toggle changes.
    public func loopToggled() {
        if loopEnabled && loopIsValid {
            if isPlaying {
                play() // routes to forward or reversed loop as appropriate
            } else {
                position = loopA ?? position
            }
        } else if segmentLooping {
            // Turning loop off keeps playing the current section linearly,
            // preserving the current direction.
            if isPlaying {
                if segmentReversed { playReversed() }
                else {
                    let pos = position
                    startForwardSegment(from: pos, length: duration - pos, looping: false)
                }
            }
        }
    }

    // MARK: Position timer

    private func startTimer() {
        stopTimer()
        lastTick = CFAbsoluteTimeGetCurrent()
        timer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.tick() }
        }
    }

    private func stopTimer() {
        timer?.invalidate()
        timer = nil
    }

    private func tick() {
        let now = CFAbsoluteTimeGetCurrent()
        let dt = now - lastTick
        lastTick = now
        accumulated += dt * (tempoPercent / 100)

        if segmentLooping {
            let t = accumulated.truncatingRemainder(dividingBy: max(segmentSourceLength, 0.001))
            // Forward loops run A→A+len; reversed loops run B→B-len.
            position = segmentReversed
                ? segmentSourceStart + segmentSourceLength - t
                : segmentSourceStart + t
        } else if segmentReversed {
            position = max(0, segmentSourceStart - accumulated)
            if position <= 0.001 {
                trackEnded()
            }
        } else {
            position = segmentSourceStart + accumulated
            if position >= duration {
                trackEnded()
            }
        }
    }

    /// Natural end of a non-looping segment: stop, then let the UI decide
    /// (replay for repeat-one, advance for repeat-all, stay stopped).
    private func trackEnded() {
        stop()
        onTrackEnded?()
    }

    // MARK: Buffer helpers

    public static func slice(_ b: AVAudioPCMBuffer, from start: AVAudioFrameCount, length: AVAudioFrameCount) -> AVAudioPCMBuffer {
        let out = AVAudioPCMBuffer(pcmFormat: b.format, frameCapacity: length)!
        out.frameLength = length
        let channels = Int(b.format.channelCount)
        guard let src = b.floatChannelData, let dst = out.floatChannelData else { return out }
        for c in 0..<channels {
            dst[c].update(from: src[c].advanced(by: Int(start)), count: Int(length))
        }
        return out
    }

    public static func reversedBuffer(_ b: AVAudioPCMBuffer) -> AVAudioPCMBuffer {
        let out = AVAudioPCMBuffer(pcmFormat: b.format, frameCapacity: b.frameCapacity)!
        out.frameLength = b.frameLength
        let channels = Int(b.format.channelCount)
        let frames = Int(b.frameLength)
        guard let src = b.floatChannelData, let dst = out.floatChannelData else { return out }
        for c in 0..<channels {
            let s = src[c]
            let d = dst[c]
            for i in 0..<frames { d[i] = s[frames - 1 - i] }
        }
        return out
    }

    /// Build the export source: forward buffer, or loop region repeated N times.
    public func exportSource(loopOnly: Bool, loopRepeat: Int) -> AVAudioPCMBuffer? {
        guard let buffer = sourceBuffer else { return nil }
        if reverseEnabled {
            guard let r = cachedReversed else { return nil }
            return r
        }
        guard loopOnly, loopIsValid, let a = loopA, let b = loopB else { return buffer }
        let sr = buffer.format.sampleRate
        let seg = Self.slice(buffer, from: AVAudioFrameCount(a * sr),
                             length: AVAudioFrameCount((b - a) * sr))
        let repeatCount = max(1, loopRepeat)
        if repeatCount == 1 { return seg }
        let repeated = AVAudioPCMBuffer(pcmFormat: seg.format,
                                        frameCapacity: seg.frameLength * AVAudioFrameCount(repeatCount))!
        repeated.frameLength = seg.frameLength * AVAudioFrameCount(repeatCount)
        let channels = Int(seg.format.channelCount)
        for c in 0..<channels {
            let s = seg.floatChannelData![c]
            let d = repeated.floatChannelData![c]
            for i in 0..<repeatCount {
                d.advanced(by: i * Int(seg.frameLength)).update(from: s, count: Int(seg.frameLength))
            }
        }
        return repeated
    }

    // MARK: Offline export

    /// Cooperative export cancel, set from the progress sheet's Cancel button.
    public var exportCancelled = false

    public func exportAudio(to url: URL, format exportFormat: AudioExportFormat,
                            tempoPercent: Double, pitchSemitones: Double,
                            loopOnly: Bool, loopRepeat: Int,
                            progress: @escaping (Double) -> Void) async throws {
        exportCancelled = false
        guard tempoPercent.isFinite, tempoPercent > 0 else {
            throw NSError(domain: "MSC", code: 4, userInfo: [NSLocalizedDescriptionKey:
                "Tempo must be greater than zero"])
        }
        guard let src = exportSource(loopOnly: loopOnly, loopRepeat: loopRepeat) else {
            throw NSError(domain: "MSC", code: 1, userInfo: [NSLocalizedDescriptionKey: "No audio loaded"])
        }
        let format = src.format
        let renderEngine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        let tp = AVAudioUnitTimePitch()
        tp.rate = Float(tempoPercent / 100)
        tp.pitch = Float(pitchSemitones * 100)
        tp.overlap = 32
        let eA = AVAudioUnitEQ(numberOfBands: 16)
        let eB = AVAudioUnitEQ(numberOfBands: 15)
        configureEQInto(eA, eB, enabled: eqEnabled)
        let dist = AVAudioUnitDistortion()
        dist.loadFactoryPreset(.multiDistortedCubed)
        dist.preGain = distortionEnabled ? min(max(distortionGain, -80), 20) : 0
        dist.wetDryMix = distortionEnabled ? min(max(distortionMix, 0), 100) : 0
        let dly = AVAudioUnitDelay()
        dly.delayTime = min(max(delayTime, 0), 2)
        dly.feedback = min(max(delayFeedback, 0), 100)
        dly.lowPassCutoff = 15000
        dly.wetDryMix = delayEnabled ? min(max(delayMix, 0), 100) : 0
        let rev = AVAudioUnitReverb()
        rev.loadFactoryPreset(.mediumHall)
        rev.wetDryMix = reverbEnabled ? min(max(reverbMix, 0), 100) : 0
        let lim = AVAudioUnitEffect(audioComponentDescription: AudioComponentDescription(
            componentType: kAudioUnitType_Effect,
            componentSubType: kAudioUnitSubType_DynamicsProcessor,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0))
        renderEngine.attach(player)
        renderEngine.attach(tp)
        renderEngine.attach(eA)
        renderEngine.attach(eB)
        renderEngine.attach(dist)
        renderEngine.attach(dly)
        renderEngine.attach(rev)
        renderEngine.attach(lim)
        try renderEngine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
        renderEngine.connect(player, to: tp, format: format)
        renderEngine.connect(tp, to: eA, format: format)
        renderEngine.connect(eA, to: eB, format: format)
        renderEngine.connect(eB, to: dist, format: format)
        renderEngine.connect(dist, to: dly, format: format)
        renderEngine.connect(dly, to: rev, format: format)
        renderEngine.connect(rev, to: lim, format: format)
        renderEngine.connect(lim, to: renderEngine.mainMixerNode, format: format)
        try renderEngine.start()
        defer { renderEngine.stop() }

        var outputSettings: [String: Any] = [
            AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: format.channelCount,
        ]
        switch exportFormat {
        case .wav:
            outputSettings[AVFormatIDKey] = kAudioFormatLinearPCM
            outputSettings[AVLinearPCMBitDepthKey] = 16
            outputSettings[AVLinearPCMIsFloatKey] = false
            outputSettings[AVLinearPCMIsBigEndianKey] = false
            outputSettings[AVLinearPCMIsNonInterleaved] = false
        case .m4a:
            outputSettings[AVFormatIDKey] = kAudioFormatMPEG4AAC
            outputSettings[AVEncoderBitRateKey] = 192_000
        }
        let out = try AVAudioFile(forWriting: url, settings: outputSettings,
                                  commonFormat: format.commonFormat,
                                  interleaved: format.isInterleaved)
        // The file's client buffer format must match the renderer exactly.
        guard out.processingFormat == format else {
            throw NSError(domain: "MSC", code: 2, userInfo: [NSLocalizedDescriptionKey:
                "Unsupported audio format (expected \(format), file wants \(out.processingFormat))"])
        }
        let expectedFrames = Double(src.frameLength) / (tempoPercent / 100)
        var rendered: Double = 0

        player.scheduleBuffer(src, at: nil, options: []) {}
        player.play()

        var done = false
        var iterations = 0
        while !done {
            if exportCancelled {
                exportCancelled = false
                throw NSError(domain: "MSC", code: 999, userInfo: [NSLocalizedDescriptionKey: "Export cancelled"])
            }
            let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 4096)!
            var status: AVAudioEngineManualRenderingStatus = .success
            do {
                status = try renderEngine.renderOffline(4096, to: buffer)
            } catch {
                throw error
            }
            switch status {
            case .success:
                if buffer.frameLength > 0 { try out.write(from: buffer) }
                rendered += Double(buffer.frameLength)
                progress(min(1, rendered / max(expectedFrames, 1)))
                done = rendered >= expectedFrames
                // Let the UI repaint the progress bar during long loop exports.
                iterations += 1
                if iterations % 16 == 0 { await Task.yield() }
            case .insufficientDataFromInputNode:
                throw NSError(domain: "MSC", code: 5, userInfo: [NSLocalizedDescriptionKey:
                    "Audio rendering ended before the expected output was complete"])
            case .error:
                throw NSError(domain: "MSC", code: 6, userInfo: [NSLocalizedDescriptionKey:
                    "Audio rendering failed"])
            case .cannotDoInCurrentContext:
                await Task.yield()
            @unknown default:
                throw NSError(domain: "MSC", code: 7, userInfo: [NSLocalizedDescriptionKey:
                    "Audio rendering returned an unsupported status"])
            }
        }
        progress(1)
    }

    /// Apply EQ settings (bypassing when disabled) to any pair of EQ nodes.
    private func configureEQInto(_ eA: AVAudioUnitEQ, _ eB: AVAudioUnitEQ, enabled: Bool) {
        let gains = enabled ? eqGains : Array(repeating: Float(0), count: EQBands.bandCount)
        for i in 0..<eA.bands.count {
            eA.bands[i].filterType = .parametric
            eA.bands[i].frequency = EQBands.frequencies[i]
            eA.bands[i].bandwidth = 1.0 / 3.0
            eA.bands[i].gain = gains[i]
            eA.bands[i].bypass = abs(gains[i]) <= 0.001
        }
        for i in 0..<eB.bands.count {
            eB.bands[i].filterType = .parametric
            eB.bands[i].frequency = EQBands.frequencies[16 + i]
            eB.bands[i].bandwidth = 1.0 / 3.0
            eB.bands[i].gain = gains[16 + i]
            eB.bands[i].bypass = abs(gains[16 + i]) <= 0.001
        }
    }
}

/// Mutable flag set from the player's completion callback.
private final class FinishedFlag: @unchecked Sendable {
    private var _value = false
    var value: Bool { _value }
    func set() { _value = true }
}
