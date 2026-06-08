using System;
using System.Runtime.InteropServices;

namespace WhereIsIt.App;

/// <summary>
/// Dependency-free system-tray (notification-area) icon for the WinUI 3 shell.
///
/// WinUI 3 has no built-in NotifyIcon, so this hosts a Win32 message-only
/// window and drives <c>Shell_NotifyIcon</c> directly. It must be constructed
/// on the UI thread — its window procedure then runs inside the existing WinUI
/// message loop, so the <paramref name="onOpen"/>/<paramref name="onExit"/>
/// callbacks fire on the UI thread and can touch the window safely.
///
/// Left-click / double-click invokes <c>onOpen</c>; right-click shows a small
/// "Open WhereIsIt / Exit" menu.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const uint MF_STRING = 0x0;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private const uint ID_OPEN = 1;
    private const uint ID_EXIT = 2;

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    // Held so the GC can't collect the delegate while Win32 still holds the
    // function pointer registered with the window class.
    private readonly WndProcDelegate wndProc;
    private readonly string className;
    private readonly Action onOpen;
    private readonly Action onExit;
    private IntPtr hwnd;
    private IntPtr iconHandle;
    private bool added;
    private bool disposed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIconHost(string tooltip, string? iconPath, Action onOpen, Action onExit)
    {
        this.onOpen = onOpen;
        this.onExit = onExit;
        wndProc = WindowProc;
        className = "WhereIsItTrayHost_" + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClass(ref wc);

        hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;

        iconHandle = LoadIconHandle(iconPath);

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = iconHandle,
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip,
        };
        added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static IntPtr LoadIconHandle(string? iconPath)
    {
        if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
        {
            var h = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0,
                LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (h != IntPtr.Zero) return h;
        }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    SafeInvoke(onOpen);
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, ID_OPEN, "Open WhereIsIt");
            AppendMenu(menu, MF_STRING, ID_EXIT, "Exit");

            GetCursorPos(out POINT pt);
            // Required by TrackPopupMenu so the menu dismisses on outside click.
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenu(menu,
                TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                pt.x, pt.y, 0, hwnd, IntPtr.Zero);

            if (cmd == ID_OPEN) SafeInvoke(onOpen);
            else if (cmd == ID_EXIT) SafeInvoke(onExit);
        }
        finally { DestroyMenu(menu); }
    }

    private static void SafeInvoke(Action a)
    {
        try { a(); } catch { /* a UI callback must not crash the message loop */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (added && hwnd != IntPtr.Zero)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        if (hwnd != IntPtr.Zero) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        if (!string.IsNullOrEmpty(className)) UnregisterClass(className, GetModuleHandle(null));
    }

    // ── Win32 interop ───────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    // Full NOTIFYICONDATAW layout. Shell_NotifyIcon validates cbSize against
    // known struct versions, so the struct must carry every field (a truncated
    // layout produces an unrecognised size and the call silently fails).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName,
        string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType,
        int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
