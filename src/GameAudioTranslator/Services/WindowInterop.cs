using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameAudioTranslator.Services;

/// <summary>
/// Win32 helpers for making a WPF window click-through, so the overlay never
/// steals mouse input from the game underneath it while it's locked in place.
/// </summary>
internal static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);

        style = clickThrough
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;

        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }
}
