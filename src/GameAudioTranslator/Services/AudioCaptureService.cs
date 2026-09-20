using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Captures WASAPI loopback audio from a playback device and slices it into
/// speech segments using simple energy-based voice activity detection.
/// </summary>
public class AudioCaptureService : IDisposable
{
    private readonly object _lock = new();
    private readonly List<byte> _segmentBuffer = new();

    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _captureFormat;
    private int _silenceMs;
    private int _speechMs;
    private bool _hasSpeech;

    public double SilenceThresholdRms { get; set; } = 0.02;
    public int SilenceDurationMs { get; set; } = 700;
    public int MinSegmentMs { get; set; } = 500;
    public int MaxSegmentMs { get; set; } = 15000;

    public bool IsCapturing => _capture != null;

    public event Action<byte[], WaveFormat>? SegmentReady;
    public event Action<string>? StatusChanged;

    public static List<MMDevice> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public void Start(MMDevice? device)
    {
        Stop();

        _capture = device != null ? new WasapiLoopbackCapture(device) : new WasapiLoopbackCapture();
        _captureFormat = _capture.WaveFormat;

        lock (_lock)
        {
            _segmentBuffer.Clear();
            _hasSpeech = false;
            _speechMs = 0;
            _silenceMs = 0;
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();

        StatusChanged?.Invoke($"Listening on: {device?.FriendlyName ?? "Default playback device"}");
    }

    public void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch
        {
            // Best-effort teardown; the capture device may already be gone (e.g. unplugged).
        }

        _capture = null;
        FlushSegment(force: true);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            StatusChanged?.Invoke($"Capture stopped unexpectedly: {e.Exception.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = _captureFormat;
        if (format == null || e.BytesRecorded == 0)
        {
            return;
        }

        double rms = ComputeRms(e.Buffer, e.BytesRecorded, format);
        int chunkMs = (int)(e.BytesRecorded / (double)format.AverageBytesPerSecond * 1000);
        bool isSpeech = rms >= SilenceThresholdRms;

        lock (_lock)
        {
            if (isSpeech || _hasSpeech)
            {
                _segmentBuffer.AddRange(e.Buffer.Take(e.BytesRecorded));
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
                FlushSegmentLocked();
            }
        }
    }

    private void FlushSegment(bool force)
    {
        lock (_lock)
        {
            if (force && _hasSpeech && _speechMs < MinSegmentMs)
            {
                // Too short to be worth transcribing; discard rather than force it through.
                _segmentBuffer.Clear();
                _hasSpeech = false;
                _speechMs = 0;
                _silenceMs = 0;
                return;
            }

            FlushSegmentLocked();
        }
    }

    /// <summary>Must be called with <see cref="_lock"/> held.</summary>
    private void FlushSegmentLocked()
    {
        if (!_hasSpeech || _segmentBuffer.Count == 0)
        {
            _segmentBuffer.Clear();
            _hasSpeech = false;
            _speechMs = 0;
            _silenceMs = 0;
            return;
        }

        var data = _segmentBuffer.ToArray();
        var format = _captureFormat;

        _segmentBuffer.Clear();
        _hasSpeech = false;
        _speechMs = 0;
        _silenceMs = 0;

        if (format != null)
        {
            SegmentReady?.Invoke(data, format);
        }
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

    public void Dispose()
    {
        Stop();
    }
}
