using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GameAudioTranslator.Services.Interop;

/// <summary>
/// Receives the OS callback fired when an async audio-interface activation
/// (started via <see cref="CoreAudioInterop.ActivateAudioInterfaceAsync"/>)
/// completes, and surfaces the result as an awaitable Task. The callback can
/// arrive on an arbitrary thread pool thread, so this only ever touches the
/// TaskCompletionSource - no UI/dispatcher work happens here.
/// </summary>
internal sealed class ActivateAudioInterfaceCompletionHandler : CoreAudioInterop.IActivateAudioInterfaceCompletionHandler
{
    private readonly TaskCompletionSource<object> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<object> Result => _tcs.Task;

    public void ActivateCompleted(CoreAudioInterop.IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            activateOperation.GetActivateResult(out int hr, out object activatedInterface);
            if (hr != 0)
            {
                _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("Audio interface activation failed.", hr));
                return;
            }

            _tcs.TrySetResult(activatedInterface);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
}
