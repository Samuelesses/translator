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
/// The continuous mic path is deliberately resampled only once (at the very
/// end, into whatever format the cable device wants) rather than through an
/// intermediate "common" format first - every extra resample stage on a
/// live stream is another place for timing jitter to turn into audible
/// dropouts. Only the reply clip (occasional, not continuous, so jitter
/// there doesn't matter) gets resampled to match before mixing.
///
/// Requires a virtual audio cable already installed - this app can't create
/// one (no app can, without a signed Windows audio driver). Everything else
/// in the app works without this; it's purely for getting your voice/reply
/// into another program's voice chat instead of just your own speakers.
/// </summary>
public class VirtualMicMixerService : IDisposable
{
    // NAudio's BufferedWaveProvider defaults to a 5-second internal buffer. Left uncapped,
    // clock drift between the mic's capture clock and the output device's playback clock
    // lets it slowly fill up, which shows up as ever-growing delay. Too tight, and any
    // brief timing hiccup (a slow callback, OS scheduling jitter) starves it, which is
    // audible as dropouts/cutting out. 300ms is a middle ground: enough slack to absorb
    // normal jitter without letting genuine drift build into seconds of lag.
    private static readonly TimeSpan MicBufferDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReplyBufferDuration = TimeSpan.FromSeconds(30);

    private const int CaptureBufferMs = 40;
    private const int OutputLatencyMs = 150;
    private const float DefaultMicGain = 2.0f;

    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _micBuffer;
    private VolumeSampleProvider? _micVolume;
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
    public bool Start(MMDevice? micDevice, MMDevice cableDevice, float micGain = DefaultMicGain)
    {
        Stop();

        try
        {
            var resolvedMic = ResolveMicDevice(micDevice);
            // Event-driven capture (matching WasapiOut below) with an explicit, tight
            // buffer period gives much more consistent callback timing than the default
            // polling-based capture, which directly reduces underrun-driven dropouts.
            _micCapture = new WasapiCapture(resolvedMic, true, CaptureBufferMs);

            var micFormat = _micCapture.WaveFormat;
            var micFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(micFormat.SampleRate, micFormat.Channels);

            _micBuffer = new BufferedWaveProvider(micFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = MicBufferDuration
            };
            _micCapture.DataAvailable += OnMicDataAvailable;

            // No resampling for the mic here - ToSampleProvider() only converts bit
            // depth/encoding to float, it doesn't touch the sample rate. The continuous
            // stream stays at the mic's native rate all the way to the final stage below.
            _micVolume = new VolumeSampleProvider(_micBuffer.ToSampleProvider()) { Volume = micGain };

            _replyBuffer = new BufferedWaveProvider(SpeechTranslationService.SpeechPcmFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = ReplyBufferDuration
            };
            _replyResampler = new MediaFoundationResampler(_replyBuffer, micFloatFormat);
            var replySampleProvider = _replyResampler.ToSampleProvider();

            var mixer = new MixingSampleProvider(micFloatFormat) { ReadFully = true };
            mixer.AddMixerInput(_micVolume);
            mixer.AddMixerInput(replySampleProvider);

            IWaveProvider outputProvider = mixer.ToWaveProvider();
            var deviceFormat = cableDevice.AudioClient.MixFormat;
            if (!FormatsMatch(outputProvider.WaveFormat, deviceFormat))
            {
                _finalResampler = new MediaFoundationResampler(outputProvider, deviceFormat);
                outputProvider = _finalResampler;
            }

            _output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, OutputLatencyMs);
            _output.Init(outputProvider);

            _micCapture.StartRecording();
            _output.Play();

            StatusChanged?.Invoke($"Voice passthrough active: {resolvedMic.FriendlyName} -> {cableDevice.FriendlyName}");
            return true;
        }
        catch (Exception ex)
        {
            Stop();
            StatusChanged?.Invoke($"Couldn't start voice passthrough: {ex.Message}");
            return false;
        }
    }

    /// <summary>Adjusts mic gain live while running. No-op if not running.</summary>
    public void SetMicGain(float gain)
    {
        if (_micVolume != null)
        {
            _micVolume.Volume = gain;
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
        _replyResampler?.Dispose();
        _finalResampler?.Dispose();

        _output = null;
        _micCapture = null;
        _micBuffer = null;
        _micVolume = null;
        _replyBuffer = null;
        _replyResampler = null;
        _finalResampler = null;
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private static MMDevice ResolveMicDevice(MMDevice? explicitDevice)
    {
        if (explicitDevice != null)
        {
            return explicitDevice;
        }

        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b) =>
        a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
        a.Encoding == b.Encoding && a.BitsPerSample == b.BitsPerSample;

    public void Dispose() => Stop();
}
