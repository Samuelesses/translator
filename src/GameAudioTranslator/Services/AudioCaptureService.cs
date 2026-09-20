using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Captures WASAPI loopback audio from an entire playback device (i.e.
/// everything audible through it) and slices it into speech segments.
/// </summary>
public class AudioCaptureService : IAudioSource, IDisposable
{
    private readonly SpeechSegmenter _segmenter = new();

    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _captureFormat;

    public double SilenceThresholdRms
    {
        get => _segmenter.SilenceThresholdRms;
        set => _segmenter.SilenceThresholdRms = value;
    }

    public int SilenceDurationMs
    {
        get => _segmenter.SilenceDurationMs;
        set => _segmenter.SilenceDurationMs = value;
    }

    public int MinSegmentMs
    {
        get => _segmenter.MinSegmentMs;
        set => _segmenter.MinSegmentMs = value;
    }

    public int MaxSegmentMs
    {
        get => _segmenter.MaxSegmentMs;
        set => _segmenter.MaxSegmentMs = value;
    }

    public bool IsCapturing => _capture != null;

    public event Action<byte[], WaveFormat>? SegmentReady;
    public event Action<string>? StatusChanged;

    public AudioCaptureService()
    {
        _segmenter.SegmentReady += data =>
        {
            if (_captureFormat != null)
            {
                SegmentReady?.Invoke(data, _captureFormat);
            }
        };
    }

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
        _segmenter.Reset();

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
        _segmenter.Flush(force: true);
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
        if (_captureFormat != null)
        {
            _segmenter.AddSamples(e.Buffer, e.BytesRecorded, _captureFormat);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
