using System;
using System.IO;
using NAudio.Wave;

namespace MusicSpeedChanger.Audio;

/// <summary>Downsampled peak data for drawing a waveform.</summary>
public sealed class WaveformData
{
    public float[] Peaks { get; }
    public TimeSpan Duration { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    private WaveformData(float[] peaks, TimeSpan duration, int sampleRate, int channels)
    {
        Peaks = peaks;
        Duration = duration;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public static WaveformData FromFile(string path, int peakCount = 1200)
    {
        peakCount = Math.Clamp(peakCount, 100, 8000);
        using var reader = CreateReader(path);
        var sample = reader.ToSampleProvider();
        int channels = sample.WaveFormat.Channels;

        long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
        long framesTotal = totalSamples / Math.Max(1, channels);
        long framesPerPeak = Math.Max(1, framesTotal / peakCount);

        float[] peaks = new float[peakCount];
        float[] buf = new float[4096 * channels];
        long frameIndex = 0;
        int peakIndex = 0;
        float peak = 0f;
        long framesInBucket = 0;

        int read;
        while ((read = sample.Read(buf, 0, buf.Length)) > 0 && peakIndex < peakCount)
        {
            int frames = read / channels;
            for (int f = 0; f < frames && peakIndex < peakCount; f++)
            {
                float v = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float s = Math.Abs(buf[f * channels + c]);
                    if (s > v) v = s;
                }
                if (v > peak) peak = v;
                framesInBucket++;
                frameIndex++;
                if (framesInBucket >= framesPerPeak)
                {
                    peaks[peakIndex++] = peak;
                    peak = 0f;
                    framesInBucket = 0;
                }
            }
        }
        while (peakIndex < peakCount) peaks[peakIndex++] = 0f;

        return new WaveformData(peaks, reader.TotalTime, sample.WaveFormat.SampleRate, channels);
    }

    private static WaveStream CreateReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".wav") return new WaveFileReader(path);
        return new MediaFoundationReader(path);
    }
}
