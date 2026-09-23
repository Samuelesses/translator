using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GameAudioTranslator.Services;

/// <summary>
/// Continuously passes your real microphone through to a virtual audio
/// device (e.g. VB-Audio Virtual Cable's input), so a game/voice app that
/// has its mic set to that virtual device's output hears you talking
/// normally - and lets <see cref="InjectReply"/> mix a translated reply
/// into that same stream on demand, so other players hear it too.
///
/// Requires a virtual audio cable already installed - this app can't create
/// one (no app can, without a signed Windows audio driver). Everything else
/// in the app works without this; it's purely for getting your voice/reply
/// into another program's voice chat instead of just your own speakers.
/// </summary>
public class VirtualMicMixerService : IDisposable
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _micBuffer;
    private MediaFoundationResampler? _micResampler;
    private BufferedWaveProvider? _replyBuffer;
    private MediaFoundationResampler? _replyResampler;
    private MediaFoundationResampler? _finalResampler;
    private WasapiOut? _output;

    public bool IsRunning => _output != null;

    public event Action<string>? StatusChanged;

    public static List<MMDevice> GetMicrophoneDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
    }

    /// <returns>true if passthrough started successfully.</returns>
    public bool Start(MMDevice? micDevice, MMDevice cableDevice)
    {
        Stop();

        try
        {
            _micCapture = micDevice != null ? new WasapiCapture(micDevice) : new WasapiCapture();
            _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat) { DiscardOnBufferOverflow = true };
            _micCapture.DataAvailable += OnMicDataAvailable;

            _micResampler = new MediaFoundationResampler(_micBuffer, MixFormat);
            var micSampleProvider = _micResampler.ToSampleProvider();

            _replyBuffer = new BufferedWaveProvider(SpeechTranslationService.SpeechPcmFormat) { DiscardOnBufferOverflow = true };
            _replyResampler = new MediaFoundationResampler(_replyBuffer, MixFormat);
            var replySampleProvider = _replyResampler.ToSampleProvider();

            var mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            mixer.AddMixerInput(micSampleProvider);
            mixer.AddMixerInput(replySampleProvider);

            IWaveProvider outputProvider = mixer.ToWaveProvider();
            var deviceFormat = cableDevice.AudioClient.MixFormat;
            if (!FormatsMatch(outputProvider.WaveFormat, deviceFormat))
            {
                _finalResampler = new MediaFoundationResampler(outputProvider, deviceFormat);
                outputProvider = _finalResampler;
            }

            _output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, 100);
            _output.Init(outputProvider);

            _micCapture.StartRecording();
            _output.Play();

            StatusChanged?.Invoke($"Voice passthrough active: {micDevice?.FriendlyName ?? "default microphone"} -> {cableDevice.FriendlyName}");
            return true;
        }
        catch (Exception ex)
        {
            Stop();
            StatusChanged?.Invoke($"Couldn't start voice passthrough: {ex.Message}");
            return false;
        }
    }

    /// <summary>Mixes a translated reply into the passthrough stream so other players hear it too. No-op if not running.</summary>
    public void InjectReply(byte[] pcmBytes)
    {
        if (_replyBuffer == null)
        {
            return;
        }

        _replyBuffer.ClearBuffer();
        _replyBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
    }

    public void Stop()
    {
        try
        {
            _micCapture?.StopRecording();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            _output?.Stop();
        }
        catch
        {
            // Best-effort.
        }

        if (_micCapture != null)
        {
            _micCapture.DataAvailable -= OnMicDataAvailable;
        }

        _output?.Dispose();
        _micCapture?.Dispose();
        _micResampler?.Dispose();
        _replyResampler?.Dispose();
        _finalResampler?.Dispose();

        _output = null;
        _micCapture = null;
        _micBuffer = null;
        _micResampler = null;
        _replyBuffer = null;
        _replyResampler = null;
        _finalResampler = null;
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
        a.Encoding == b.Encoding && a.BitsPerSample == b.BitsPerSample;

    public void Dispose() => Stop();
}
