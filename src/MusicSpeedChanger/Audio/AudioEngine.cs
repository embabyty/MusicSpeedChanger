using System;
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
    private EqualizerSampleProvider? _eq;
    private ISampleProvider? _volumeProvider;
    private bool _disposed;

    /// <summary>When true, playback runs backwards from the current position.</summary>
    public bool Reverse { get; private set; }

    // EQ settings persist across file loads.
    private readonly float[] _eqGains = new float[GraphicEqualizer.BandCount];

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

    // ----- 31-band equalizer -----

    /// <summary>When false, the EQ stage is bypassed (playback and export).</summary>
    public bool EqEnabled
    {
        get => _eqEnabled;
        set { _eqEnabled = value; if (_eq != null) _eq.Enabled = value; }
    }
    private bool _eqEnabled = true;

    public float GetEqGain(int band)
    {
        CheckEqBand(band);
        return _eqGains[band];
    }

    public void SetEqGain(int band, float gainDb)
    {
        CheckEqBand(band);
        gainDb = Math.Clamp(gainDb, GraphicEqualizer.MinGainDb, GraphicEqualizer.MaxGainDb);
        _eqGains[band] = gainDb;
        _eq?.Equalizer.SetGain(band, gainDb);
    }

    public float[] GetEqGains()
    {
        var copy = new float[GraphicEqualizer.BandCount];
        Array.Copy(_eqGains, copy, copy.Length);
        return copy;
    }

    public void SetEqGains(float[] gains)
    {
        if (gains == null) throw new ArgumentNullException(nameof(gains));
        if (gains.Length != GraphicEqualizer.BandCount)
            throw new ArgumentException($"Need {GraphicEqualizer.BandCount} gains.", nameof(gains));
        for (int i = 0; i < gains.Length; i++)
            _eqGains[i] = Math.Clamp(gains[i], GraphicEqualizer.MinGainDb, GraphicEqualizer.MaxGainDb);
        _eq?.Equalizer.SetGains(_eqGains);
    }

    public void ResetEq()
    {
        Array.Clear(_eqGains, 0, _eqGains.Length);
        _eq?.Equalizer.Reset();
    }

    public bool EqIsFlat
    {
        get
        {
            for (int i = 0; i < _eqGains.Length; i++)
                if (Math.Abs(_eqGains[i]) > 0.001f) return false;
            return true;
        }
    }

    private static void CheckEqBand(int band)
    {
        if (band < 0 || band >= GraphicEqualizer.BandCount)
            throw new ArgumentOutOfRangeException(nameof(band));
    }

    /// <summary>Source (input) duration, unaffected by tempo.</summary>
    public TimeSpan SourceDuration => _reader?.TotalTime ?? TimeSpan.Zero;

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
    /// (Re)builds the processing chain (SoundTouch tempo/pitch + EQ + volume + output)
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
        _eq = new EqualizerSampleProvider(_processor);
        _eq.Equalizer.SetGains(_eqGains);
        _eq.Enabled = EqEnabled;
        _volumeProvider = new VolumeSampleProvider(_eq) { Volume = 1f };

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
        _eq = null;
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
    /// Runs on a background thread.
    /// </summary>
    public Task ExportWavAsync(string destination, double tempo, double pitchSemitones,
        TimeSpan? from = null, TimeSpan? to = null, IProgress<double>? progress = null)
    {
        if (!IsLoaded || FilePath == null) throw new InvalidOperationException("No file loaded.");
        string src = FilePath;
        float[] eqGains = GetEqGains();
        bool eqOn = EqEnabled;
        return Task.Run(() =>
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
                Tempo = Math.Clamp(tempo, 0.25, 3.0),
                PitchSemiTones = Math.Clamp(pitchSemitones, -12.0, 12.0)
            };
            int channels = sample.WaveFormat.Channels;

            // Mirror of the realtime EQ stage (bypassed when flat/disabled).
            var eq = new GraphicEqualizer(sample.WaveFormat.SampleRate, channels);
            eq.SetGains(eqGains);
            bool useEq = eqOn && !eq.IsFlat;

            var outFormat = new WaveFormat(sample.WaveFormat.SampleRate, sample.WaveFormat.Channels);
            using var writer = new WaveFileWriter(destination, outFormat);

            float[] readBuf = new float[8192 * sample.WaveFormat.Channels];
            float[] outBuf = new float[8192 * sample.WaveFormat.Channels];
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
                        if (useEq) eq.Process(outBuf, 0, received * channels);
                        writer.WriteSamples(outBuf, 0, received * sample.WaveFormat.Channels);
                    }
                } while (received > 0);

                if (totalBytes > startPos && endPos > startPos)
                    progress?.Report((double)(reader.Position - startPos) / (endPos - startPos));
            }

            st.Flush();
            int tail;
            do
            {
                tail = st.ReceiveSamples(new Span<float>(outBuf), outBuf.Length / sample.WaveFormat.Channels);
                if (tail > 0)
                {
                    if (useEq) eq.Process(outBuf, 0, tail * channels);
                    writer.WriteSamples(outBuf, 0, tail * sample.WaveFormat.Channels);
                }
            } while (tail > 0);
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
