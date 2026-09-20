using System;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Common surface shared by every capture source (whole-device loopback,
/// per-application loopback) so the UI can wire up and stop whichever one
/// is active without caring which concrete implementation it is.
/// </summary>
public interface IAudioSource
{
    bool IsCapturing { get; }
    double SilenceThresholdRms { get; set; }

    event Action<byte[], WaveFormat>? SegmentReady;
    event Action<string>? StatusChanged;

    void Stop();
}
