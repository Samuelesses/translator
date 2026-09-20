using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GameAudioTranslator.Services.Interop;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Captures only the audio produced by one process (and its child process
/// tree) via the Windows Process Loopback API, instead of an entire
/// playback device. Requires Windows 10 2004+ (build 19041) or Windows 11.
/// </summary>
public class ProcessLoopbackCaptureService : IAudioSource, IDisposable
{
    private const long BufferDurationHns = 10_000_000; // 1 second, in 100ns units

    private readonly SpeechSegmenter _segmenter = new();

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private CoreAudioInterop.IAudioClient? _audioClient;
    private CoreAudioInterop.IAudioCaptureClient? _captureClient;
    private WaveFormat? _waveFormat;

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

    public bool IsCapturing => _cts != null;

    public event Action<byte[], WaveFormat>? SegmentReady;
    public event Action<string>? StatusChanged;

    public ProcessLoopbackCaptureService()
    {
        _segmenter.SegmentReady += data =>
        {
            if (_waveFormat != null)
            {
                SegmentReady?.Invoke(data, _waveFormat);
            }
        };
    }

    /// <param name="processId">The target process. Its full child-process tree is captured too
    /// (e.g. a launcher plus the game process it spawns), so picking either usually works.</param>
    public void Start(int processId, string displayName)
    {
        Stop();

        _cts = new CancellationTokenSource();
        _segmenter.Reset();
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        var token = _cts.Token;
        _captureTask = Task.Run(() => RunAsync(processId, displayName, token), token);
    }

    public void Stop()
    {
        if (_cts == null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
            _captureTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort: the loop may already be unwinding on its own.
        }

        try
        {
            _audioClient?.Stop();
        }
        catch
        {
            // Ignore - the client may already be in a stopped/torn-down state.
        }

        ReleaseComObject(ref _captureClient);
        ReleaseComObject(ref _audioClient);

        _cts.Dispose();
        _cts = null;
        _captureTask = null;

        _segmenter.Flush(force: true);
    }

    private async Task RunAsync(int processId, string displayName, CancellationToken ct)
    {
        try
        {
            var audioClient = await ActivateAsync(processId, ct).ConfigureAwait(false);
            _audioClient = audioClient;

            var format = CoreAudioInterop.WaveFormatEx.CreateIeeeFloat(48000, 2);
            audioClient.Initialize(
                CoreAudioInterop.AudclntShareModeShared,
                CoreAudioInterop.AudclntStreamflagsLoopback,
                BufferDurationHns,
                0,
                ref format,
                IntPtr.Zero);

            audioClient.GetService(CoreAudioInterop.IidIAudioCaptureClient, out var captureClientObj);
            _captureClient = (CoreAudioInterop.IAudioCaptureClient)captureClientObj;

            audioClient.Start();
            StatusChanged?.Invoke($"Listening to: {displayName} (and its child processes)");

            await CaptureLoopAsync(_captureClient, _waveFormat!, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop().
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Couldn't capture that application: {FriendlyError(ex)}");
        }
    }

    private static async Task<CoreAudioInterop.IAudioClient> ActivateAsync(int processId, CancellationToken ct)
    {
        var activationParams = new CoreAudioInterop.AudioClientActivationParams
        {
            ActivationType = CoreAudioInterop.AudioclientActivationTypeProcessLoopback,
            ProcessLoopbackParams = new CoreAudioInterop.AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = CoreAudioInterop.ProcessLoopbackModeIncludeTargetProcessTree
            }
        };

        int size = Marshal.SizeOf<CoreAudioInterop.AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        var handler = new ActivateAudioInterfaceCompletionHandler();

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = CoreAudioInterop.PropVariant.ForBlob(paramsPtr, size);
            var riid = CoreAudioInterop.IidIAudioClient;

            CoreAudioInterop.ActivateAudioInterfaceAsync(
                CoreAudioInterop.VirtualAudioDeviceProcessLoopback,
                ref riid,
                ref propvariant,
                handler,
                out _);
        }
        finally
        {
            // The OS copies what it needs out of the blob during the synchronous
            // call above; it's safe to free as soon as ActivateAudioInterfaceAsync returns.
            Marshal.FreeHGlobal(paramsPtr);
        }

        var activated = await handler.Result.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (CoreAudioInterop.IAudioClient)activated;
    }

    private async Task CaptureLoopAsync(CoreAudioInterop.IAudioCaptureClient captureClient, WaveFormat format, CancellationToken ct)
    {
        int blockAlign = format.BlockAlign;

        while (!ct.IsCancellationRequested)
        {
            captureClient.GetNextPacketSize(out uint packetLength);

            while (packetLength != 0 && !ct.IsCancellationRequested)
            {
                captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);

                int bytesAvailable = checked((int)numFrames * blockAlign);
                if (bytesAvailable > 0)
                {
                    var buffer = new byte[bytesAvailable];
                    bool silent = (flags & CoreAudioInterop.AudclntBufferflagsSilent) != 0;
                    if (!silent)
                    {
                        Marshal.Copy(dataPtr, buffer, 0, bytesAvailable);
                    }
                    // else: leave the buffer zeroed, which SpeechSegmenter reads as silence.

                    _segmenter.AddSamples(buffer, bytesAvailable, format);
                }

                captureClient.ReleaseBuffer(numFrames);
                captureClient.GetNextPacketSize(out packetLength);
            }

            try
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static void ReleaseComObject<T>(ref T? comObject) where T : class
    {
        if (comObject != null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }

        comObject = null;
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is COMException or NotSupportedException)
        {
            return $"{ex.Message} (per-application capture needs Windows 10 2004+ or Windows 11 - try 'This device's audio' instead)";
        }

        return ex.Message;
    }

    public void Dispose()
    {
        Stop();
    }
}
