using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Simple energy-based voice activity detector that slices a stream of raw
/// PCM chunks into discrete speech segments. Shared by every capture source
/// (whole-device loopback, per-application loopback) so they all behave
/// identically for a given sensitivity setting.
/// </summary>
public class SpeechSegmenter
{
    private readonly object _lock = new();
    private readonly List<byte> _buffer = new();

    private bool _hasSpeech;
    private int _speechMs;
    private int _silenceMs;

    public double SilenceThresholdRms { get; set; } = 0.02;
    public int SilenceDurationMs { get; set; } = 700;
    public int MinSegmentMs { get; set; } = 500;
    public int MaxSegmentMs { get; set; } = 15000;

    public event Action<byte[]>? SegmentReady;

    public void Reset()
    {
        lock (_lock)
        {
            _buffer.Clear();
            _hasSpeech = false;
            _speechMs = 0;
            _silenceMs = 0;
        }
    }

    public void AddSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded == 0)
        {
            return;
        }

        double rms = ComputeRms(buffer, bytesRecorded, format);
        int chunkMs = (int)(bytesRecorded / (double)format.AverageBytesPerSecond * 1000);
        bool isSpeech = rms >= SilenceThresholdRms;

        lock (_lock)
        {
            if (isSpeech || _hasSpeech)
            {
                _buffer.AddRange(buffer.Take(bytesRecorded));
            }

            if (isSpeech)
            {
                _hasSpeech = true;
                _speechMs += chunkMs;
                _silenceMs = 0;
            }
            else if (_hasSpeech)
            {
                _silenceMs += chunkMs;
            }

            bool reachedTrailingSilence = _hasSpeech && _silenceMs >= SilenceDurationMs && _speechMs >= MinSegmentMs;
            bool reachedMaxLength = _hasSpeech && _speechMs >= MaxSegmentMs;

            if (reachedTrailingSilence || reachedMaxLength)
            {
                FlushLocked(force: false);
            }
        }
    }

    public void Flush(bool force)
    {
        lock (_lock)
        {
            FlushLocked(force);
        }
    }

    /// <summary>Must be called with <see cref="_lock"/> held.</summary>
    private void FlushLocked(bool force)
    {
        if (!_hasSpeech || _buffer.Count == 0 || (force && _speechMs < MinSegmentMs))
        {
            // Either nothing captured, or (when force-flushing on stop) too short to be worth transcribing.
            _buffer.Clear();
            _hasSpeech = false;
            _speechMs = 0;
            _silenceMs = 0;
            return;
        }

        var data = _buffer.ToArray();

        _buffer.Clear();
        _hasSpeech = false;
        _speechMs = 0;
        _silenceMs = 0;

        SegmentReady?.Invoke(data);
    }

    private static double ComputeRms(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int sampleCount = bytesRecorded / 4;
            if (sampleCount == 0)
            {
                return 0;
            }

            double sum = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(buffer, i * 4);
                sum += sample * sample;
            }

            return Math.Sqrt(sum / sampleCount);
        }

        if (format.BitsPerSample == 16)
        {
            int sampleCount = bytesRecorded / 2;
            if (sampleCount == 0)
            {
                return 0;
            }

            double sum = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                double f = sample / 32768.0;
                sum += f * f;
            }

            return Math.Sqrt(sum / sampleCount);
        }

        return 0;
    }
}
