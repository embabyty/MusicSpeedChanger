using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;

namespace MusicSpeedChanger.Audio;

/// <summary>
/// Playback engine: file decoding + SoundTouch tempo/pitch + WaveOut.
/// Position is tracked on the *source* stream; effective output duration = source / tempo.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;              // MediaFoundationReader or WaveFileReader
    private ISampleProvider? _sampleSource;   // active source: forward reader adapter or reverse buffer
    private ReverseSampleProvider? _reverse;  // active only in reverse mode
    private float[]? _reverseData;            // decoded file cache (per loaded file)
    private WaveFormat? _reverseFormat;
    private SoundTouchSampleProvider? _processor;
    private EffectsChainProvider? _chain;
    private ISampleProvider? _volumeProvider;
    private bool _disposed;

    /// <summary>When true, playback runs backwards from the current position.</summary>
    public bool Reverse { get; private set; }

    // Effects chain persists across file loads.
    private readonly List<EffectBlock> _effects = new();
    private readonly object _fxLock = new();

    public string? FilePath { get; private set; }
    public string? FileName => FilePath == null ? null : Path.GetFileName(FilePath);

    public bool IsLoaded => _reader != null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public float Volume
    {
        get => _output?.Volume ?? 1f;
        set { if (_output != null) _output.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public double Tempo
    {
        get => _processor?.Tempo ?? 1.0;
        set { if (_processor != null) _processor.Tempo = value; }
    }

    public double PitchSemitones
    {
        get => _processor?.PitchSemitones ?? 0.0;
        set { if (_processor != null) _processor.PitchSemitones = value; }
    }

    // ----- Effects chain (Equalizer APO style) -----

    /// <summary>Deep-copy snapshot of the blocks (safe to hand to UI/settings).</summary>
    public List<EffectBlock> SnapshotEffects()
    {
        lock (_fxLock)
        {
            var copy = new List<EffectBlock>(_effects.Count);
            foreach (var b in _effects) copy.Add(b.Clone());
            return copy;
        }
    }

    /// <summary>Replace the whole chain (startup restore). Unknown kinds are dropped.</summary>
    public void ReplaceEffects(IEnumerable<EffectBlock>? blocks)
    {
        lock (_fxLock)
        {
            _effects.Clear();
            if (blocks != null)
                foreach (var b in blocks)
                {
                    var clean = SanitizeBlock(b);
                    if (clean != null) _effects.Add(clean);
                }
        }
        PushEffectsToChain();
    }

    /// <summary>True when any enabled block audibly does something (playback and export).</summary>
    public bool EffectsActive
    {
        get
        {
            lock (_fxLock)
            {
                foreach (var b in _effects)
                    if (b.IsActiveContent) return true;
                return false;
            }
        }
    }

    public int AddEffect(EffectBlock block)
    {
        var clean = SanitizeBlock(block) ?? EffectBlock.NewEq();
        int index;
        lock (_fxLock) { _effects.Add(clean); index = _effects.Count - 1; }
        PushEffectsToChain();
        return index;
    }

    public void RemoveEffectAt(int index)
    {
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            _effects.RemoveAt(index);
        }
        // Indices shifted — full rebuild.
        PushEffectsToChain();
    }

    public void MoveEffect(int from, int to)
    {
        lock (_fxLock)
        {
            if (from < 0 || from >= _effects.Count || to < 0 || to >= _effects.Count || from == to) return;
            var b = _effects[from];
            _effects.RemoveAt(from);
            _effects.Insert(to, b);
        }
        PushEffectsToChain();
    }

    public void SetEffectEnabled(int index, bool enabled)
    {
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            _effects[index].Enabled = enabled;
        }
        PushEffectsToChain();
    }

    public void SetEffectBands(int index, int bands)
    {
        bands = bands == GraphicEqualizer.BandCount15 ? bands : GraphicEqualizer.BandCount;
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            var b = _effects[index];
            if (b.IsPreamp || b.Bands == bands) return;
            var dstFreqs = GraphicEqualizer.CentersForBands(bands);
            var srcFreqs = GraphicEqualizer.CentersForBands(b.Bands);
            b.Gains = GraphicEqualizer.ResampleGains(srcFreqs, PadGains(b.Gains, b.Bands), dstFreqs);
            b.Bands = bands;
            b.Preset = "";
        }
        PushEffectsToChain();
    }

    public void SetEffectGain(int index, int band, float gainDb)
    {
        gainDb = Math.Clamp(gainDb, GraphicEqualizer.MinGainDb, GraphicEqualizer.MaxGainDb);
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            var b = _effects[index];
            if (b.IsPreamp || b.Gains == null || band < 0 || band >= b.Bands) return;
            b.Gains = PadGains(b.Gains, b.Bands);
            b.Gains[band] = gainDb;
            b.Preset = "";
        }
        _chain?.SetBlockGain(index, band, gainDb);
    }

    public void SetEffectGains(int index, float[] gains, string preset = "")
    {
        if (gains == null) return;
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            var b = _effects[index];
            if (b.IsPreamp) return;
            var dst = new float[b.Bands];
            if (gains.Length == b.Bands)
                Array.Copy(gains, dst, b.Bands);
            else
                dst = GraphicEqualizer.ResampleGains(
                    GraphicEqualizer.CentersForBands(gains.Length == GraphicEqualizer.BandCount15
                        ? GraphicEqualizer.BandCount15 : GraphicEqualizer.BandCount),
                    PadGains(gains, gains.Length),
                    GraphicEqualizer.CentersForBands(b.Bands));
            for (int i = 0; i < dst.Length; i++)
                dst[i] = Math.Clamp(dst[i], GraphicEqualizer.MinGainDb, GraphicEqualizer.MaxGainDb);
            b.Gains = dst;
            b.Preset = preset ?? "";
        }
        PushEffectsToChain();
    }

    public void ResetEffect(int index)
    {
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            var b = _effects[index];
            if (b.IsPreamp) b.PreampDb = 0;
            else { b.Gains = new float[b.Bands]; b.Preset = "Flat"; }
        }
        PushEffectsToChain();
    }

    public void SetPreampDb(int index, float gainDb)
    {
        gainDb = Math.Clamp(gainDb, -20f, 20f);
        lock (_fxLock)
        {
            if (index < 0 || index >= _effects.Count) return;
            var b = _effects[index];
            if (!b.IsPreamp) return;
            b.PreampDb = gainDb;
        }
        _chain?.SetPreampDb(index, gainDb);
    }

    private void PushEffectsToChain()
    {
        EffectsChainProvider? chain = _chain;
        if (chain == null) return;
        chain.Rebuild(SnapshotEffects());
    }

    private static float[] PadGains(float[]? gains, int bands)
    {
        var dst = new float[bands];
        if (gains != null)
            Array.Copy(gains, dst, Math.Min(gains.Length, bands));
        return dst;
    }

    private static EffectBlock? SanitizeBlock(EffectBlock? b)
    {
        if (b == null) return null;
        if (b.IsPreamp)
        {
            var pre = EffectBlock.NewPreamp(b.PreampDb);
            pre.Enabled = b.Enabled;
            return pre;
        }
        int bands = b.Bands == GraphicEqualizer.BandCount15
            ? GraphicEqualizer.BandCount15 : GraphicEqualizer.BandCount;
        return new EffectBlock
        {
            Kind = "eq",
            Enabled = b.Enabled,
            Bands = bands,
            Gains = PadGains(b.Gains, bands),
            Preset = b.Preset ?? "",
        };
    }

    /// <summary>Source (input) duration, unaffected by tempo.</summary>
    public TimeSpan SourceDuration => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>Source sample rate for display math (48 kHz fallback when unloaded).</summary>
    public int SourceSampleRate => _reader?.WaveFormat.SampleRate ?? 48000;

    /// <summary>Effective output duration at current tempo.</summary>
    public TimeSpan OutputDuration =>
        Tempo <= 0 ? SourceDuration : TimeSpan.FromTicks((long)(SourceDuration.Ticks / Tempo));

    /// <summary>Current source position.</summary>
    public TimeSpan SourcePosition
    {
        get
        {
            if (Reverse && _reverse != null)
                return TimeSpan.FromSeconds((double)_reverse.PositionFrames / _reverse.WaveFormat.SampleRate);
            return _reader?.CurrentTime ?? TimeSpan.Zero;
        }
        set
        {
            value = Clamp(value, TimeSpan.Zero, SourceDuration);
            if (Reverse)
            {
                if (_reverse == null) return;
                _reverse.PositionFrames = (long)(value.TotalSeconds * _reverse.WaveFormat.SampleRate);
                _processor?.ClearBuffer();
                return;
            }
            if (_reader == null) return;
            _reader.CurrentTime = value;
            _processor?.ClearBuffer();
        }
    }

    /// <summary>Progress 0..1 in source time (matches waveform).</summary>
    public double Progress
    {
        get
        {
            var dur = SourceDuration.TotalSeconds;
            return dur <= 0 ? 0 : Math.Clamp(SourcePosition.TotalSeconds / dur, 0, 1);
        }
        set
        {
            if (_reader == null) return;
            SourcePosition = TimeSpan.FromSeconds(SourceDuration.TotalSeconds * Math.Clamp(value, 0, 1));
        }
    }

    // Loop points in source time
    public TimeSpan? LoopA { get; set; }
    public TimeSpan? LoopB { get; set; }
    public bool LoopEnabled { get; set; }

    public event EventHandler? PlaybackEnded;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty path.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);

        Unload();

        _reader = CreateReader(path);
        BuildOutputChain(_reader.ToSampleProvider());

        FilePath = path;
        LoopA = LoopB = null;
        LoopEnabled = false;
    }

    /// <summary>
    /// (Re)builds the processing chain (SoundTouch tempo/pitch + EQ + limiter + volume + output)
    /// around the given source, preserving tempo, pitch, EQ, and device volume.
    /// </summary>
    private void BuildOutputChain(ISampleProvider source)
    {
        double tempo = Tempo;
        double pitch = PitchSemitones;
        float deviceVolume = 1f;

        if (_output != null)
        {
            try { deviceVolume = _output.Volume; } catch { /* ignore */ }
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { /* ignore */ }
            _output.Dispose();
            _output = null;
        }

        _sampleSource = source;
        _processor = new SoundTouchSampleProvider(source)
        {
            Tempo = tempo,
            PitchSemitones = pitch
        };
        _chain = new EffectsChainProvider(_processor);
        _chain.Rebuild(SnapshotEffects());
        // Limiter sits post-effects so stretch overshoot + EQ boosts never clip the DAC.
        var limiter = new LimiterSampleProvider(_chain!);
        _volumeProvider = new VolumeSampleProvider(limiter) { Volume = 1f };

        _output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
        try { _output.Volume = deviceVolume; } catch { /* ignore */ }
        _output.Init(_volumeProvider);
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// Toggles backwards playback. Position is preserved (direction flips at the same
    /// point in the song); tempo, pitch, EQ, and volume carry over. Decoding the file
    /// into memory happens once per loaded file.
    /// </summary>
    /// <exception cref="InvalidOperationException">No file loaded, or file too long to reverse.</exception>
    public void SetReverse(bool reverse)
    {
        if (!IsLoaded || FilePath == null) throw new InvalidOperationException("No file loaded.");
        if (reverse == Reverse) return;

        TimeSpan pos = SourcePosition;
        bool wasPlaying = IsPlaying;

        if (reverse)
        {
            EnsureReverseData(FilePath);
            _reverse = new ReverseSampleProvider(_reverseData!, _reverseFormat!);
            Reverse = true;
            BuildOutputChain(_reverse);
        }
        else
        {
            Reverse = false;
            _reverse = null;
            if (_reader == null) throw new InvalidOperationException("No file loaded.");
            BuildOutputChain(_reader.ToSampleProvider());
        }

        SourcePosition = pos;
        if (wasPlaying) Play();
    }

    /// <summary>Decodes the whole file to 32-bit float (cached per loaded file).</summary>
    private void EnsureReverseData(string path)
    {
        if (_reverseData != null && _reverseFormat != null) return;

        using var reader = CreateReader(path);
        var sample = reader.ToSampleProvider();
        int channels = sample.WaveFormat.Channels;
        const long maxFloats = 134_217_728; // 512 MB cap (~24 min of CD-quality stereo)

        var data = new List<float>();
        float[] buf = new float[8192 * channels];
        long total = 0;
        int read;
        while ((read = sample.Read(buf, 0, buf.Length)) > 0)
        {
            if (total + read > maxFloats)
                throw new InvalidOperationException(
                    "This file is too long to play in reverse (over ~24 min at CD quality).");
            float[] chunk = new float[read];
            Array.Copy(buf, chunk, read);
            data.AddRange(chunk);
            total += read;
        }

        _reverseData = data.ToArray();
        _reverseFormat = sample.WaveFormat;
    }

    public void Unload()
    {
        try { _output?.Stop(); } catch { /* ignore */ }
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }
        _processor = null;
        _volumeProvider = null;
        _chain = null;
        _sampleSource = null;
        _reverse = null;
        _reverseData = null;
        _reverseFormat = null;
        Reverse = false;
        _reader?.Dispose();
        _reader = null;
        FilePath = null;
        LoopA = LoopB = null;
        LoopEnabled = false;
    }

    public void Play()
    {
        if (_output == null) return;
        // In reverse, position 0 means "already played to the start" — restart from the end.
        if (Reverse && SourcePosition <= TimeSpan.Zero && SourceDuration > TimeSpan.Zero)
            SourcePosition = SourceDuration;
        _output.Play();
    }

    public void Pause()
    {
        if (_output?.PlaybackState == PlaybackState.Playing)
            _output.Pause();
    }

    public void Stop()
    {
        _output?.Stop();
        if (_reader != null)
            SourcePosition = TimeSpan.Zero;
    }

    /// <summary>Call on a UI timer; enforces AB loop and reports natural end.</summary>
    public void Update()
    {
        if (_reader == null || _output == null) return;
        if (_output.PlaybackState != PlaybackState.Playing) return;

        if (LoopEnabled && LoopA.HasValue && LoopB.HasValue && LoopB > LoopA)
        {
            if (!Reverse && SourcePosition >= LoopB.Value)
                SourcePosition = LoopA.Value;
            else if (Reverse && SourcePosition <= LoopA.Value)
                SourcePosition = LoopB.Value;
        }
    }

    /// <summary>
    /// Render current tempo/pitch settings to a WAV file (16-bit PCM, same rate/channels).
    /// Runs on a background thread. <paramref name="repeatCount"/> repeats the
    /// rendered segment N times (loop-repeat savings).
    /// </summary>
    public Task ExportWavAsync(string destination, double tempo, double pitchSemitones,
        TimeSpan? from = null, TimeSpan? to = null, IProgress<double>? progress = null, int repeatCount = 1)
    {
        if (!IsLoaded || FilePath == null) throw new InvalidOperationException("No file loaded.");
        string src = FilePath;
        List<EffectBlock> fx = SnapshotEffects();
        int repeats = Math.Clamp(repeatCount, 1, 1000);
        return Task.Run(() =>
        {
            // Probe once for the output format (rate/channels).
            int outChannels, outRate;
            using (var probe = CreateReader(src))
            {
                var pf = probe.ToSampleProvider().WaveFormat;
                outChannels = pf.Channels;
                outRate = pf.SampleRate;
            }

            double exportTempo = Math.Clamp(tempo, 0.25, 3.0);
            double exportPitch = Math.Clamp(pitchSemitones, -12.0, 12.0);

            var outFormat = new WaveFormat(outRate, outChannels);
            using var writer = new WaveFileWriter(destination, outFormat);

            float[] readBuf = new float[8192 * outChannels];
            float[] outBuf = new float[8192 * outChannels];

            for (int iter = 0; iter < repeats; iter++)
            {
                using var reader = CreateReader(src);
                long totalBytes = reader.Length;
                if (from.HasValue)
                    reader.CurrentTime = Clamp(from.Value, TimeSpan.Zero, reader.TotalTime);
                long endPos = to.HasValue
                    ? reader.WaveFormat.AverageBytesPerSecond >= 0
                        ? Math.Min(totalBytes, reader.Position + (long)((to.Value - reader.CurrentTime).TotalSeconds * reader.WaveFormat.AverageBytesPerSecond))
                        : totalBytes
                    : totalBytes;
                // Simpler: compute end time then compare CurrentTime each loop.
                TimeSpan endTime = to ?? reader.TotalTime;

                var sample = reader.ToSampleProvider();
                var st = new SoundTouchProcessor
                {
                    Channels = sample.WaveFormat.Channels,
                    SampleRate = sample.WaveFormat.SampleRate,
                    Tempo = exportTempo,
                    PitchSemiTones = exportPitch
                };
                SoundTouchSampleProvider.ConfigureQuality(st);
                if (exportTempo < 0.65)
                    SoundTouchSampleProvider.ConfigureSlowStretch(st);
                int channels = sample.WaveFormat.Channels;

                // Mirror of the realtime effects stage (same order, same settings).
                // The provider wraps the live sample stream; here we only use
                // Rebuild + ProcessBuffer on render buffers.
                var fxChain = new EffectsChainProvider(sample);
                fxChain.Rebuild(fx);

                // Mirror of the realtime limiter stage.
                var limiter = new LimiterSampleProvider(sample.WaveFormat);

                long startPos = reader.Position;

                while (true)
                {
                    if (reader.CurrentTime >= endTime) break;
                    int read = sample.Read(readBuf, 0, readBuf.Length);
                    if (read == 0) break;
                    st.PutSamples(new ReadOnlySpan<float>(readBuf, 0, read), read / sample.WaveFormat.Channels);

                    int received;
                    do
                    {
                    received = st.ReceiveSamples(new Span<float>(outBuf), outBuf.Length / sample.WaveFormat.Channels);
                    if (received > 0)
                    {
                        fxChain.ProcessBuffer(outBuf, 0, received * channels);
                        limiter.Process(outBuf, 0, received * sample.WaveFormat.Channels);
                        writer.WriteSamples(outBuf, 0, received * sample.WaveFormat.Channels);
                    }
                    } while (received > 0);

                    if (totalBytes > startPos && endPos > startPos)
                        progress?.Report((iter + (double)(reader.Position - startPos) / (endPos - startPos)) / repeats);
                }

                st.Flush();
                int tail;
                do
                {
                tail = st.ReceiveSamples(new Span<float>(outBuf), outBuf.Length / sample.WaveFormat.Channels);
                if (tail > 0)
                {
                    fxChain.ProcessBuffer(outBuf, 0, tail * channels);
                    limiter.Process(outBuf, 0, tail * sample.WaveFormat.Channels);
                    writer.WriteSamples(outBuf, 0, tail * sample.WaveFormat.Channels);
                }
                } while (tail > 0);
            }
        });
    }

    private static WaveStream CreateReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // WAV handled natively (incl. extensible); everything else via Media Foundation (mp3/m4a/wma/aiff).
        if (ext == ".wav")
            return new WaveFileReader(path);
        return new MediaFoundationReader(path);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Natural end: WaveOut stops when provider returns 0.
        PlaybackStopped?.Invoke(sender, e);
        // If source exhausted (not a user pause), notify.
        try
        {
            bool ended = Reverse
                ? SourcePosition <= TimeSpan.FromMilliseconds(250)
                : _reader != null && _processor != null && _reader.Position >= _reader.Length - 1;
            if (ended)
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }
        catch { /* ignore */ }
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) =>
        v < min ? min : v > max ? max : v;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
    }
}
