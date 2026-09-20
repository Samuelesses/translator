using System;
using System.Runtime.InteropServices;

namespace GameAudioTranslator.Services.Interop;

/// <summary>
/// Raw Win32/COM declarations for the Process Loopback API, which lets a
/// WASAPI client capture the audio of a single process (and its child
/// processes) instead of an entire playback device. Introduced in the
/// Windows 10 2004 (build 19041) "May 2020 Update" — see the Windows-Classic-Samples
/// "ApplicationLoopback" sample this is a C# port of. Not available on
/// older Windows 10 builds or Windows 7/8.
///
/// NAudio doesn't wrap this API (it only knows how to activate an
/// <see cref="NAudio.CoreAudioApi.IAudioClient"/> via a real device), so this
/// class talks to Mmdevapi.dll / WASAPI directly.
/// </summary>
internal static class CoreAudioInterop
{
    /// <summary>The virtual "device" id that means "capture a specific process's audio".</summary>
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int AudioclientActivationTypeProcessLoopback = 1;
    public const int ProcessLoopbackModeIncludeTargetProcessTree = 0;

    public const ushort WaveFormatIeeeFloat = 0x0003;
    public const uint AudclntStreamflagsLoopback = 0x00020000;
    public const uint AudclntBufferflagsSilent = 0x2;

    public const int AudclntShareModeShared = 0;

    private const ushort VtBlob = 65;

    public static readonly Guid IidIAudioClient = typeof(IAudioClient).GUID;
    public static readonly Guid IidIAudioCaptureClient = typeof(IAudioCaptureClient).GUID;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] ref PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;

        public static WaveFormatEx CreateIeeeFloat(int sampleRate, int channels)
        {
            int blockAlign = channels * (32 / 8);
            return new WaveFormatEx
            {
                wFormatTag = WaveFormatIeeeFloat,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
                nBlockAlign = (ushort)blockAlign,
                wBitsPerSample = 32,
                cbSize = 0
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct AudioClientActivationParams
    {
        [FieldOffset(0)]
        public int ActivationType;

        [FieldOffset(4)]
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    /// <summary>
    /// A minimal PROPVARIANT laid out to carry a VT_BLOB payload (the shape
    /// ActivateAudioInterfaceAsync requires for its activation-params
    /// argument). Matches the real Win32 PROPVARIANT's 24-byte size on x64:
    /// an 8-byte header (vt + 3 reserved WORDs) followed by the blob union
    /// (4-byte cbSize, padded to 8, then an 8-byte pointer).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public uint blobCbSize;

        [FieldOffset(16)]
        public IntPtr blobData;

        public static PropVariant ForBlob(IntPtr data, int size) => new()
        {
            vt = VtBlob,
            blobCbSize = (uint)size,
            blobData = data
        };
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(
            int shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            [In] ref WaveFormatEx format,
            IntPtr audioSessionGuid);

        void GetBufferSize(out uint numBufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint numPaddingFrames);
        void IsFormatSupported(int shareMode, [In] ref WaveFormatEx format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(
            out IntPtr dataBuffer,
            out uint numFramesToRead,
            out uint bufferFlags,
            out long devicePosition,
            out long qpcPosition);

        void ReleaseBuffer(uint numFramesWritten);
        void GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
