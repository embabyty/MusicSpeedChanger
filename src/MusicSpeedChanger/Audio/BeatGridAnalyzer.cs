using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Which part of a track to analyze. AutoMix beat-sync cares about the start
/// of the incoming track (head: where the mix fades in) and the end of the
/// outgoing track (tail: where the mix fades out), so each side is analyzed
/// where it matters — extrapolating a whole-track grid across minutes of
/// tempo drift would land the phase audibly off at the mix point.
/// </summary>
public enum BeatGridWindow
{
    Head,
    Tail,
}

/// <summary>
/// A 4/4 beat grid anchored on downbeats (bar starts), in source seconds.
/// Beat positions are DownbeatOffset + n * BeatInterval; downbeats (bar
/// starts) every <see cref="BeatsPerBar"/> beats.
/// </summary>
public sealed class BeatGrid
{
    /// <summary>Native tempo in BPM, folded into the 90…180 DJ convention band.</summary>
    public double Bpm { get; }
    /// <summary>Seconds per beat in source time.</summary>
    public double BeatIntervalSeconds { get; }
    /// <summary>Source-time position of a downbeat near the analyzed audio.</summary>
    public double DownbeatOffsetSeconds { get; }
    /// <summary>Source-time position of first audible sound (for Smart Cue silence trimming).</summary>
    public double FirstSoundSeconds { get; }
    /// <summary>Detection confidence 0…1. Below ~0.3 the grid is guesswork.</summary>
    public double Confidence { get; }
    /// <summary>True when the grid is trustworthy enough to mix to.</summary>
    public bool IsReliable { get; }
    public int BeatsPerBar => 4;

    public BeatGrid(double bpm, double beatIntervalSeconds, double downbeatOffsetSeconds,
        double firstSoundSeconds, double confidence, bool isReliable)
    {
        Bpm = bpm;
        BeatIntervalSeconds = beatIntervalSeconds;
        DownbeatOffsetSeconds = downbeatOffsetSeconds;
        FirstSoundSeconds = firstSoundSeconds;
        Confidence = confidence;
        IsReliable = isReliable;
    }

    public static BeatGrid Unreliable(double confidence = 0) =>
        new(0, 0, 0, 0, Math.Clamp(confidence, 0, 1), false);

    /// <summary>Fractional beats since the downbeat grid, mod 4 (bar phase).</summary>
    public double BarPhaseAt(double sourceSeconds)
    {
        if (BeatIntervalSeconds <= 0) return 0;
        double beats = (sourceSeconds - DownbeatOffsetSeconds) / BeatIntervalSeconds;
        double mod = beats % BeatsPerBar;
        return mod < 0 ? mod + BeatsPerBar : mod;
    }
}

/// <summary>
/// DJ-style beat-sync plan: tempo ratio + phase-matched start + bar-snapped duration.
/// </summary>
/// <param name="TempoRatio">Multiply the incoming engine tempo by this (BpmOut / BpmIn).</param>
/// <param name="IncomingStartSeconds">Source position to start the incoming track at.</param>
/// <param name="DurationSeconds">Wall-clock crossfade length (whole bars of the outgoing grid).</param>
/// <param name="Bars">Number of bars the crossfade spans.</param>
/// <param name="Info">Short human-readable summary for the status line.</param>
public sealed record BeatSyncPlan(
    double TempoRatio,
    double IncomingStartSeconds,
    double DurationSeconds,
    int Bars,
    string Info)
{
    /// <summary>
    /// Tries to build a beat-synced mix plan. Returns null when either grid is
    /// unreliable, the tempos are too far apart to stretch cleanly, or either
    /// track is too short — callers fall back to a plain equal-power crossfade.
    /// </summary>
    public static BeatSyncPlan? TryPlan(
        BeatGrid outGrid, BeatGrid inGrid,
        double outPosSourceSeconds, double globalTempo,
        double requestedSeconds, double remainingWallSeconds, double inDurationSeconds)
    {
        if (outGrid == null || inGrid == null) return null;
        if (!outGrid.IsReliable || !inGrid.IsReliable) return null;
        if (globalTempo <= 0 || inDurationSeconds < 5) return null;

        double ratio = outGrid.Bpm / inGrid.Bpm;
        // ±25 % keeps SoundTouch clean; wider gaps get the legacy plain mix.
        if (ratio < 0.80 || ratio > 1.25) return null;
        double incomingTempo = globalTempo * ratio;
        if (incomingTempo < 0.30 || incomingTempo > 2.50) return null;

        // Phase-match: start the incoming track at the same bar phase the
        // outgoing track has right now, on a downbeat-anchored grid, so beats
        // — and bar lines — coincide for the whole crossfade.
        double phase = outGrid.BarPhaseAt(outPosSourceSeconds);
        double startIn = inGrid.DownbeatOffsetSeconds + phase * inGrid.BeatIntervalSeconds;
        // Keep startIn near the start of the audible music, aligned to the same bar phase
        double barDurationIn = inGrid.BeatsPerBar * inGrid.BeatIntervalSeconds;
        if (barDurationIn > 0.1)
        {
            double minStart = Math.Max(0, inGrid.FirstSoundSeconds - 0.05);
            while (startIn - barDurationIn >= minStart)
                startIn -= barDurationIn;
        }
        if (startIn < 0) startIn = Math.Max(0, inGrid.FirstSoundSeconds);
        if (startIn > inDurationSeconds - 1.5) return null;

        // Snap the crossfade length to whole bars of the outgoing grid
        // (wall-clock: source beats run faster/slower with the tempo slider).
        double barWall = outGrid.BeatsPerBar * outGrid.BeatIntervalSeconds / globalTempo;
        if (barWall <= 0.2) return null;
        int bars = Math.Clamp((int)Math.Round(requestedSeconds / barWall), 1, 8);
        double duration = Math.Clamp(bars * barWall, 0.8, 12);
        duration = Math.Min(duration, Math.Max(0.8, remainingWallSeconds));

        string info = $"{outGrid.Bpm:0}→{inGrid.Bpm:0} BPM · {bars} bar{(bars == 1 ? "" : "s")}";
        return new BeatSyncPlan(ratio, startIn, duration, bars, info);
    }
}

/// <summary>
/// Lightweight BPM + downbeat detector (no third-party DSP deps).
///
/// Pipeline: decode a ≤90 s window → mono 11 025 Hz → RMS envelope
/// (~86 fps) → log-energy onset flux → autocorrelation tempo (60…200 BPM,
/// folded to the 90…180 DJ band) with parabolic refinement → beat phase by
/// comb alignment → downbeat by picking the loudest beat rotation mod 4.
///
/// Beta heuristics, tuned for 4/4 beat-driven music: ambient, classical,
/// rubato jazz, and odd meters score low confidence and the mixer falls back
/// to a plain crossfade. Folding means a half-time trap track (70 BPM) maps
/// to 140 — beats still lock, bar lines may sit half a bar off.
/// </summary>
public static class BeatGridAnalyzer
{
    private const int AnalysisRate = 11025;
    private const int FrameSize = 512;
    private const int HopSize = 64;
    private const double WindowSeconds = 90;

    private static double EnvelopeRate => (double)AnalysisRate / HopSize;

    public static BeatGrid Analyze(string path, BeatGridWindow window = BeatGridWindow.Head)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return BeatGrid.Unreliable();
            using WaveStream reader = CreateReader(path);
            double totalSeconds = reader.TotalTime.TotalSeconds;
            if (totalSeconds < 5) return BeatGrid.Unreliable();

            double windowLen = Math.Min(WindowSeconds, totalSeconds);
            double windowStart = window == BeatGridWindow.Tail
                ? Math.Max(0, totalSeconds - windowLen)
                : 0;

            float[] mono = DecodeMono(reader, windowStart, windowLen);
            if (mono.Length < AnalysisRate * 5) return BeatGrid.Unreliable();

            return AnalyzeResampled(mono, windowStart, anchorAtEnd: window == BeatGridWindow.Tail);
        }
        catch
        {
            return BeatGrid.Unreliable();
        }
    }

    /// <param name="anchorAtEnd">When true (tail windows), the beat phase is
    /// fitted to the last ~15 s instead of the first: the global tempo needs
    /// the whole window for resolution, but phase fitted over minutes drifts
    /// (sub-frame period error × dozens of beats), so each side fits phase
    /// where its mix point is — head at the start, tail at the end.</param>
    internal static BeatGrid AnalyzeResampled(float[] mono, double windowStartSeconds, bool anchorAtEnd = false)
    {
        try
        {
            // Trim long silence: it carries no tempo info and dilutes the
            // autocorrelation peak (a 20 s groove in 90 s of room tone would
            // score unreliable). Keep 1 s of context; recurse once.
            mono = TrimSilence(mono, windowStartSeconds, out windowStartSeconds);
            if (mono.Length == 0) return BeatGrid.Unreliable();

            int nFrames = (mono.Length - FrameSize) / HopSize;
            if (nFrames < 400) return BeatGrid.Unreliable();

            // RMS envelope.
            float[] rms = new float[nFrames];
            float maxRms = 0;
            for (int f = 0; f < nFrames; f++)
            {
                double sum = 0;
                int baseIdx = f * HopSize;
                for (int i = 0; i < FrameSize; i++)
                {
                    float s = mono[baseIdx + i];
                    sum += s * s;
                }
                float r = (float)Math.Sqrt(sum / FrameSize);
                rms[f] = r;
                if (r > maxRms) maxRms = r;
            }
            if (maxRms < 1e-4f) return BeatGrid.Unreliable(); // digital silence

            double firstSoundSec = FindFirstSound(rms, maxRms);

            // Log-energy onset flux (half-wave rectified diff).
            float[] nov = new float[nFrames];
            float maxNov = 0;
            double prev = Math.Log(1 + 40 * rms[0]);
            for (int f = 1; f < nFrames; f++)
            {
                double cur = Math.Log(1 + 40 * rms[f]);
                float d = (float)Math.Max(0, cur - prev);
                nov[f] = d;
                if (d > maxNov) maxNov = d;
                prev = cur;
            }
            if (maxNov <= 0) return BeatGrid.Unreliable();

            // Autocorrelation of mean-centered flux over 55…210 BPM lags.
            double envRate = EnvelopeRate;
            int lagMin = Math.Max(4, (int)Math.Floor(envRate * 60.0 / 210.0));
            int lagMax = (int)Math.Ceiling(envRate * 60.0 / 55.0);
            lagMax = Math.Min(lagMax, nFrames / 4);
            if (lagMax <= lagMin) return BeatGrid.Unreliable();

            double mean = 0;
            for (int f = 0; f < nFrames; f++) mean += nov[f];
            mean /= nFrames;

            double ac0 = 0;
            for (int f = 0; f < nFrames; f++)
            {
                double v = nov[f] - mean;
                ac0 += v * v;
            }
            if (ac0 <= 0) return BeatGrid.Unreliable();

            int bestLag = -1;
            double bestScore = double.NegativeInfinity;
            double[] scores = new double[lagMax + 1];
            for (int lag = lagMin; lag <= lagMax; lag++)
            {
                double ac = 0;
                for (int f = lag; f < nFrames; f++)
                    ac += (nov[f] - mean) * (nov[f - lag] - mean);
                double s = ac / ac0;
                scores[lag] = s;
                if (s > bestScore) { bestScore = s; bestLag = lag; }
            }

            // Parabolic refinement around the peak.
            double period = bestLag;
            if (bestLag > lagMin && bestLag < lagMax)
            {
                double y0 = scores[bestLag - 1], y1 = scores[bestLag], y2 = scores[bestLag + 1];
                double denom = y0 - 2 * y1 + y2;
                if (denom < 0)
                {
                    double shift = 0.5 * (y0 - y2) / denom;
                    period = bestLag + Math.Clamp(shift, -1, 1);
                }
            }

            double bpm = 60.0 * envRate / period;

            // Fold into the 90…180 DJ band.
            while (bpm < 90 && bpm * 2 <= 205) { bpm *= 2; period /= 2; }
            while (bpm > 180) { bpm /= 2; period *= 2; }
            if (bpm < 60 || bpm > 200) return BeatGrid.Unreliable(0.1);

            double beatSec = 60.0 / bpm;

            // Joint period+phase refinement over the ~15 s around the mix
            // point (start for head, end for tail). The global autocorrelation
            // above resolves tempo only to ~±0.2 BPM (one envelope frame is
            // ~12 ms); refitting locally absorbs that bias so the grid lines
            // sit on the transients where the crossfade actually happens.
            int regionLen = Math.Min(nFrames, (int)(15 * EnvelopeRate));
            int regionStart = anchorAtEnd ? Math.Max(0, nFrames - regionLen) : 0;
            int regionEnd = regionStart + regionLen;
            double bestPeriod = period;
            int bestOff = 0;
            double bestFit = double.NegativeInfinity;
            for (int step = -15; step <= 15; step++)
            {
                double pf = period * (1 + step * 0.001); // ±1.5 % in 0.1 % steps
                int pInt = Math.Max(1, (int)Math.Round(pf));
                for (int o = 0; o < pInt; o++)
                {
                    double s = 0;
                    // First tooth at/after the region start for this offset.
                    double k0 = o + Math.Max(0, Math.Ceiling((regionStart - o) / pf)) * pf;
                    for (double k = k0; k < regionEnd; k += pf)
                    {
                        int idx = (int)Math.Round(k);
                        if (idx < 0 || idx >= nFrames) break;
                        s += nov[idx];
                    }
                    if (s > bestFit) { bestFit = s; bestOff = o; bestPeriod = pf; }
                }
            }
            period = bestPeriod;
            bpm = 60.0 * EnvelopeRate / period;
            if (bpm < 60 || bpm > 200) return BeatGrid.Unreliable(0.1);
            beatSec = 60.0 / bpm;
            double beatOffSec = (bestOff * HopSize + FrameSize / 2.0) / AnalysisRate;

            // Downbeat: loudest beat rotation mod 4 (kicks/bass land on bar starts).
            double[] rotSum = new double[4];
            int[] rotCount = new int[4];
            for (int k = 0; ; k++)
            {
                int idx = (int)Math.Round(bestOff + k * period);
                if (idx >= nFrames) break;
                if (idx < regionStart || idx >= regionEnd) continue;
                rotSum[k % 4] += rms[idx];
                rotCount[k % 4]++;
            }
            int rot = 0;
            double rotBest = double.NegativeInfinity;
            for (int r = 0; r < 4; r++)
            {
                double avg = rotCount[r] > 0 ? rotSum[r] / rotCount[r] : 0;
                if (avg > rotBest) { rotBest = avg; rot = r; }
            }
            double downRel = beatOffSec + rot * beatSec;
            double barSec = 4 * beatSec;
            double floor = Math.Max(0, firstSoundSec - beatSec);
            // Earliest bar line at/after the music start, so a mix-in parked
            // on this grid never fades in over leading silence.
            while (downRel - barSec >= floor) downRel -= barSec;
            while (downRel < floor) downRel += barSec;

            // Confidence from peak height + onset density.
            double peakNorm = bestScore; // already normalized by ac0
            double density = 0;
            float densityThresh = maxNov * 0.2f;
            for (int f = 0; f < nFrames; f++)
                if (nov[f] > densityThresh) density++;
            density /= nFrames;
            double conf = Math.Clamp((peakNorm - 0.15) / 0.45, 0, 1);
            if (density < 0.02) conf *= Math.Max(0, density / 0.02);
            bool reliable = conf >= 0.30;

            double downAbs = windowStartSeconds + Math.Max(0, downRel);
            double firstSoundAbs = windowStartSeconds + Math.Max(0, firstSoundSec);
            return new BeatGrid(bpm, beatSec, downAbs, firstSoundAbs, Math.Clamp(conf, 0, 1), reliable);
        }
        catch
        {
            return BeatGrid.Unreliable();
        }
    }

    private static double FindFirstSound(float[] rms, float maxRms)
    {
        float thresh = maxRms * 0.05f;
        for (int f = 0; f < rms.Length; f++)
            if (rms[f] > thresh)
                return (f * HopSize) / (double)AnalysisRate;
        return 0;
    }

    /// <summary>
    /// Cuts leading/trailing silence longer than 5 s (keeping 1 s of context)
    /// and reports the new window start. Returns an empty array when less
    /// than 5 s of active audio remains.
    /// </summary>
    private static float[] TrimSilence(float[] mono, double windowStart, out double newStart)
    {
        newStart = windowStart;
        if (mono.Length < AnalysisRate * 6) return mono;
        float peak = 0;
        int stride = AnalysisRate / 20; // 50 ms probes
        int count = mono.Length / stride;
        float[] env = new float[count];
        for (int i = 0; i < count; i++)
        {
            float m = 0;
            int baseIdx = i * stride;
            int len = Math.Min(stride, mono.Length - baseIdx);
            for (int j = 0; j < len; j += 4)
            {
                float s = Math.Abs(mono[baseIdx + j]);
                if (s > m) m = s;
            }
            env[i] = m;
            if (m > peak) peak = m;
        }
        if (peak < 1e-4f) return Array.Empty<float>();
        float thresh = peak * 0.05f;
        int first = 0;
        while (first < count && env[first] <= thresh) first++;
        int last = count - 1;
        while (last > first && env[last] <= thresh) last--;
        double firstSec = first * stride / (double)AnalysisRate;
        double totalSec = mono.Length / (double)AnalysisRate;
        double lastSec = (last + 1) * stride / (double)AnalysisRate;
        int from = firstSec > 5 ? (int)((firstSec - 1.0) * AnalysisRate) : 0;
        int to = totalSec - lastSec > 5 ? Math.Min(mono.Length, (int)((lastSec + 1.0) * AnalysisRate)) : mono.Length;
        if (to - from < AnalysisRate * 5) return Array.Empty<float>();
        if (from == 0 && to == mono.Length) return mono;
        newStart = windowStart + from / (double)AnalysisRate;
        float[] sliced = new float[to - from];
        Array.Copy(mono, from, sliced, 0, sliced.Length);
        return sliced;
    }

    private static float[] DecodeMono(WaveStream reader, double startSeconds, double lengthSeconds)
    {
        int channels = reader.WaveFormat.Channels;
        int nativeRate = reader.WaveFormat.SampleRate;
        reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, startSeconds));

        long maxMono = (long)(lengthSeconds * nativeRate) + nativeRate;
        maxMono = Math.Min(maxMono, 8_000_000); // ~3 min @44.1k safety cap
        var mono = new List<float>((int)Math.Min(maxMono, 4_000_000));

        var sample = reader.ToSampleProvider();
        float[] buf = new float[8192 * Math.Max(1, channels)];
        long wantFrames = (long)(lengthSeconds * nativeRate);
        long gotFrames = 0;
        int read;
        while (gotFrames < wantFrames && (read = sample.Read(buf, 0, buf.Length)) > 0)
        {
            int frames = read / Math.Max(1, channels);
            for (int f = 0; f < frames && gotFrames < wantFrames; f++)
            {
                double m = 0;
                for (int c = 0; c < channels; c++) m += buf[f * channels + c];
                m /= Math.Max(1, channels);
                mono.Add((float)m);
                gotFrames++;
            }
            if (mono.Count >= maxMono) break;
        }
        float[] native = mono.ToArray();
        return Resample(native, nativeRate, AnalysisRate);
    }

    internal static float[] Resample(float[] src, int srcRate, int dstRate)
    {
        if (src.Length == 0) return Array.Empty<float>();
        if (srcRate == dstRate) return (float[])src.Clone();
        double step = (double)srcRate / dstRate;
        int dstLen = Math.Max(1, (int)((src.Length - 1) / step));
        var dst = new float[dstLen];
        for (int i = 0; i < dstLen; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = pos - i0;
            dst[i] = (float)(src[i0] * (1 - frac) + src[i1] * frac);
        }
        return dst;
    }

    private static WaveStream CreateReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav") return new WaveFileReader(path);
        return new MediaFoundationReader(path);
    }
}

/// <summary>
/// Small file-backed cache so the tail grid of the playing track is usually
/// ready before the crossfade starts (warmed on every track load) and the
/// head grid of the next track is only decoded once per session.
/// Unreliable results are cached too — no point re-decoding silence.
/// </summary>
public static class BeatGridCache
{
    private static readonly ConcurrentDictionary<string, BeatGrid> _cache = new();
    private const int MaxEntries = 64;

    public static BeatGrid GetOrAnalyze(string? path, BeatGridWindow window)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return BeatGrid.Unreliable();
        string key = KeyFor(path, window);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var grid = BeatGridAnalyzer.Analyze(path, window);
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[key] = grid;
        return grid;
    }

    /// <summary>Fire-and-forget warmup for the next mix; never throws.</summary>
    public static void Precompute(string? path, BeatGridWindow window)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string key = KeyFor(path, window);
        if (_cache.ContainsKey(key)) return;
        try
        {
            var grid = BeatGridAnalyzer.Analyze(path, window);
            if (_cache.Count >= MaxEntries) _cache.Clear();
            _cache[key] = grid;
        }
        catch { /* warmup is best-effort */ }
    }

    private static string KeyFor(string path, BeatGridWindow window)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{window}|{path.ToLowerInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            return $"{window}|{path.ToLowerInvariant()}";
        }
    }
}
