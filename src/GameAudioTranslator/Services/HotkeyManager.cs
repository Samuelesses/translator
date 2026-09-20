using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameAudioTranslator.Services;

/// <summary>
/// Registers global hotkeys (active even while the game has focus) via the
/// Win32 RegisterHotKey API, routed through the given window's message loop.
/// </summary>
public class HotkeyManager : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, Action> _handlers = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _nextId = 1;

    public void Register(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <returns>false if the combination is already taken by another application.</returns>
    public bool RegisterHotkey(uint modifiers, uint virtualKey, Action callback)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers, virtualKey))
        {
            return false;
        }

        _handlers[id] = callback;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }

        _handlers.Clear();
        _source?.RemoveHook(WndProc);
    }
}
