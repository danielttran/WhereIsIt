using System;
using System.Runtime.InteropServices;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

/// <summary>
/// Win32 RegisterHotKey wrapper that delivers WM_HOTKEY to a callback by
/// subclassing the host window. Disposing unregisters and unsubclasses.
/// </summary>
public sealed class GlobalHotkeyHost : IDisposable
{
    private const int HotkeyId = 0xB00B;
    private const uint WM_HOTKEY = 0x0312;
    private const ushort SubclassId = 0x4711;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    private readonly IntPtr hwnd;
    private readonly Action onPressed;
    private readonly SubclassProc subclassDelegate;   // held for GC
    private bool registered;
    private bool disposed;

    public GlobalHotkeyHost(IntPtr hwnd, HotkeyBinding binding, Action onPressed)
    {
        this.hwnd = hwnd;
        this.onPressed = onPressed;
        subclassDelegate = WndProc;
        SetWindowSubclass(hwnd, subclassDelegate, SubclassId, IntPtr.Zero);
        registered = RegisterHotKey(hwnd, HotkeyId, binding.Modifiers, binding.VirtualKey);
    }

    public bool IsRegistered => registered;

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            try { onPressed(); } catch { /* never let an exception out of the window proc */ }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered) UnregisterHotKey(hwnd, HotkeyId);
        RemoveWindowSubclass(hwnd, subclassDelegate, SubclassId);
    }
}
