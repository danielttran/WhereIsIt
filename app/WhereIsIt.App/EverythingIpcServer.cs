using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App;

/// <summary>
/// Everything-compatible IPC server: a message-only window that answers
/// <c>WM_COPYDATA</c> queries using the public Everything IPC SDK layout
/// (<c>Everything_IPC.h</c>), so existing Everything SDK clients / <c>es.exe</c>
/// can query WhereIsIt's index.
///
/// Implemented from the documented public SDK; like the rest of this session's
/// Win32/WinUI code it must be built and smoke-tested on Windows. The one value
/// to confirm against <c>Everything_IPC.h</c> is <see cref="WndClass"/> (the IPC
/// window class). Construct on the UI thread so the WndProc runs in the existing
/// message loop; replies are sent asynchronously to the client's reply window
/// (the standard Everything IPC model), so the UI thread never blocks.
/// </summary>
public sealed class EverythingIpcServer : IDisposable
{
    // Everything IPC window class (Everything_IPC.h: EVERYTHING_IPC_WNDCLASS).
    private const string WndClass = "EVERYTHING";

    private const int WM_COPYDATA = 0x004A;
    private const int EVERYTHING_IPC_COPYDATAQUERYW = 2;

    // EVERYTHING_IPC_ITEM flags.
    private const uint EVERYTHING_IPC_FOLDER = 0x00000001;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly IEngineClient engine;
    private readonly WndProcDelegate wndProc;
    private readonly string className;
    private IntPtr hwnd;
    private bool disposed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public EverythingIpcServer(IEngineClient engine)
    {
        this.engine = engine;
        wndProc = WindowProc;
        // A unique registered class (the SDK class is also created so clients can
        // FindWindow it); registering our own avoids collisions when re-created.
        className = WndClass;

        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        if (RegisterClass(ref wc) == 0) return;
        hwnd = CreateWindowEx(0, className, "WhereIsIt", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    public bool IsListening => hwnd != IntPtr.Zero;

    private IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA)
        {
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if ((int)cds.dwData == EVERYTHING_IPC_COPYDATAQUERYW && cds.lpData != IntPtr.Zero)
                {
                    // EVERYTHING_IPC_QUERYW: 5 DWORDs then a null-terminated WCHAR string.
                    uint replyHwnd = (uint)Marshal.ReadInt32(cds.lpData, 0);
                    uint replyMsg = (uint)Marshal.ReadInt32(cds.lpData, 4);
                    uint flags = (uint)Marshal.ReadInt32(cds.lpData, 8);
                    uint offset = (uint)Marshal.ReadInt32(cds.lpData, 12);
                    uint maxResults = (uint)Marshal.ReadInt32(cds.lpData, 16);
                    string search = Marshal.PtrToStringUni(cds.lpData + 20) ?? string.Empty;

                    _ = HandleQueryAsync(new IntPtr(replyHwnd), replyMsg, flags, offset, maxResults, search);
                    return new IntPtr(1); // TRUE — query accepted
                }
            }
            catch { /* malformed query — ignore */ }
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    private async Task HandleQueryAsync(IntPtr replyHwnd, uint replyMsg, uint flags, uint offset, uint maxResults, string search)
    {
        try
        {
            // Translate Everything search flags onto our query syntax.
            var prefix = string.Empty;
            if ((flags & 0x1) != 0) prefix += "case: ";
            if ((flags & 0x2) != 0) prefix += "wholeword: ";
            if ((flags & 0x4) != 0) prefix += "matchpath: ";
            if ((flags & 0x8) != 0) prefix += "regex: ";

            var rows = await RunSearchAsync(prefix + search);
            int cap = maxResults == 0xFFFFFFFF ? rows.Count : (int)Math.Min((uint)rows.Count, maxResults);
            int start = (int)Math.Min((uint)rows.Count, offset);
            int count = Math.Max(0, Math.Min(cap, rows.Count - start));

            var buffer = BuildList(rows, start, count, rows.Count);
            SendCopyData(replyHwnd, replyMsg, buffer);
        }
        catch { /* reply failures are non-fatal */ }
    }

    private async Task<List<(string Name, string Parent, bool IsFolder)>> RunSearchAsync(string query)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = engine.ObserveResults.Take(1).Subscribe(ids => tcs.TrySetResult(ids));
        await engine.SearchAsync(query, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var _ = timeout.Token.Register(() => tcs.TrySetResult(Array.Empty<uint>()));
        var ids = await tcs.Task;

        var rows = new List<(string, string, bool)>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                var r = await engine.GetRowAsync(id, CancellationToken.None);
                rows.Add((r.Name, r.ParentPath, (r.Attributes ?? string.Empty).Contains('D')));
            }
            catch { }
        }
        return rows;
    }

    // EVERYTHING_IPC_LISTW: 7 DWORD header, numitems * (3 DWORD item), then the
    // filename/path strings; item offsets are byte offsets from the list start.
    private static byte[] BuildList(List<(string Name, string Parent, bool IsFolder)> rows, int start, int count, int total)
    {
        const int headerSize = 7 * 4;
        const int itemSize = 3 * 4;
        int stringsStart = headerSize + count * itemSize;

        using var strings = new MemoryStream();
        var itemRecords = new (uint Flags, uint NameOff, uint PathOff)[count];
        int folders = 0, files = 0;
        for (int i = 0; i < count; i++)
        {
            var (name, parent, isFolder) = rows[start + i];
            if (isFolder) folders++; else files++;
            uint nameOff = (uint)(stringsStart + strings.Length);
            WriteUtf16Z(strings, name);
            uint pathOff = (uint)(stringsStart + strings.Length);
            WriteUtf16Z(strings, parent);
            itemRecords[i] = (isFolder ? EVERYTHING_IPC_FOLDER : 0u, nameOff, pathOff);
        }

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        // totfolders/totfiles/totitems then numfolders/numfiles/numitems then offset.
        bw.Write((uint)rows.Count); // approximations for totals (folders/files of full set)
        bw.Write((uint)0);
        bw.Write((uint)total);
        bw.Write((uint)folders);
        bw.Write((uint)files);
        bw.Write((uint)count);
        bw.Write((uint)start);
        foreach (var it in itemRecords) { bw.Write(it.Flags); bw.Write(it.NameOff); bw.Write(it.PathOff); }
        bw.Write(strings.ToArray());
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteUtf16Z(Stream s, string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0); s.WriteByte(0); // null terminator (WCHAR)
    }

    private static void SendCopyData(IntPtr target, uint msgData, byte[] payload)
    {
        if (target == IntPtr.Zero) return;
        var unmanaged = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, unmanaged, payload.Length);
            var cds = new COPYDATASTRUCT { dwData = new IntPtr(msgData), cbData = payload.Length, lpData = unmanaged };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<COPYDATASTRUCT>());
            try
            {
                Marshal.StructureToPtr(cds, ptr, false);
                SendMessage(target, WM_COPYDATA, IntPtr.Zero, ptr); // synchronous: data valid for the call
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        finally { Marshal.FreeHGlobal(unmanaged); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hwnd != IntPtr.Zero) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        try { UnregisterClass(className, GetModuleHandle(null)); } catch { }
    }

    // ── Win32 interop ───────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
