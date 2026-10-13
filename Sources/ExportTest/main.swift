import Foundation
import AVFoundation
import MSCCore
import Darwin

setvbuf(stdout, nil, 2, 0) // _IONBF

// Headless check: synthesize a sine, run it through AudioEngine's offline
// export (time pitch + EQ + limiter) and confirm a non-trivial WAV is produced.

let inputURL = URL(fileURLWithPath: "/tmp/msc_input.wav")
let sampleRate = 44100.0
let seconds = 3.0
let frames = Int(sampleRate * seconds)
let fmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate,
                        channels: 2, interleaved: false)!
let buffer = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(frames))!
buffer.frameLength = AVAudioFrameCount(frames)
for c in 0..<2 {
    let ch = buffer.floatChannelData![c]
    for i in 0..<frames {
        let phase = 2 * Double.pi * 220 * Double(i) / sampleRate
        ch[i] = Float(0.5 * sin(phase))
    }
}
do {
    let inFile = try AVAudioFile(forWriting: inputURL, settings: [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVSampleRateKey: sampleRate,
        AVNumberOfChannelsKey: 2,
        AVLinearPCMBitDepthKey: 16,
        AVLinearPCMIsFloatKey: false,
        AVLinearPCMIsBigEndianKey: false,
        AVLinearPCMIsNonInterleaved: false,
    ])
    try inFile.write(from: buffer)
}
print("wrote input")

let engine = await MainActor.run { AudioEngine() }
try await MainActor.run { try engine.load(url: inputURL) }
print("loaded duration:", await MainActor.run { engine.duration })
await MainActor.run {
    engine.tempoPercent = 80
    engine.pitchSemitones = 2
    engine.eqGains = Array(repeating: 3, count: 31)
    engine.loopA = 0.5
    engine.loopB = 1.5
}
for outputFormat in AudioExportFormat.allCases {
    let outputURL = URL(fileURLWithPath: "/tmp/msc_output.\(outputFormat.fileExtension)")
    try? FileManager.default.removeItem(at: outputURL)
    try await engine.exportAudio(to: outputURL, format: outputFormat,
                                 tempoPercent: 80, pitchSemitones: 2,
                                 loopOnly: true, loopRepeat: 3) { p in
        print("\(outputFormat.displayName): \(Int((p * 100).rounded()))%", terminator: "\r")
    }
    guard let out = try? AVAudioFile(forReading: outputURL), out.length > 0 else {
        print("\nFailed to read \(outputFormat.displayName) output!")
        exit(1)
    }
    print("\nExported \(outputFormat.displayName) frames:", out.length,
          "duration:", Double(out.length) / out.processingFormat.sampleRate)
}
print("OK")
