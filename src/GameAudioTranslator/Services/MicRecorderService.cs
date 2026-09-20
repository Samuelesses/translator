using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Records from the system's default real microphone (not loopback) while
/// active, for the "speak an English reply" feature. Unrelated to the game
/// audio capture services, which read the opposite direction (playback).
/// </summary>
public class MicRecorderService : IDisposable
{
    private readonly object _lock = new();
    private readonly List<byte> _buffer = new();

    private WasapiCapture? _capture;
    private WaveFormat? _format;

    public bool IsRecording => _capture != null;

    public void Start()
    {
        Stop();

        _capture = new WasapiCapture();
        _format = _capture.WaveFormat;

        lock (_lock)
        {
            _buffer.Clear();
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    /// <returns>The recorded audio and its format, or null if nothing was recorded.</returns>
    public (byte[] Data, WaveFormat Format)? StopAndTakeRecording()
    {
        var format = _format;
        Stop();

        byte[] data;
        lock (_lock)
        {
            data = _buffer.ToArray();
            _buffer.Clear();
        }

        return format != null && data.Length > 0 ? (data, format) : null;
    }

    private void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch
        {
            // Best-effort teardown.
        }

        _capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        lock (_lock)
        {
            _buffer.AddRange(e.Buffer.Take(e.BytesRecorded));
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
