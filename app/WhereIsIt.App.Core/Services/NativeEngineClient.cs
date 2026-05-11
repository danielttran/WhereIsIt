using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Subjects;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

/// <summary>
/// IEngineClient backed by WhereIsIt.Engine.Native.dll — the full legacy
/// IndexingEngine (USN journal, MFT scan, real NTFS indexing) exposed via P/Invoke.
/// Falls back gracefully when the DLL is absent.
/// </summary>
public sealed class NativeEngineClient : IEngineClient, IDisposable
{
    // ------------------------------------------------------------------ //
    // P/Invoke declarations                                                //
    // ------------------------------------------------------------------ //

    private const string DllName = "WhereIsIt.Engine.Native.dll";

    private static class Native
    {
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr engine_create();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_destroy(IntPtr h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_start(IntPtr h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_stop(IntPtr h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern void engine_search(IntPtr h, string query);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_sort(IntPtr h, int sortKey, int descending);

        // Returns 1=changed, 0=timeout, -1=stopped
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_wait_results_changed(IntPtr h, int timeoutMs, out int outCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_result_count(IntPtr h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_get_result_ids(IntPtr h, [Out] uint[] buf, int count);

        // Returns 0 on success, -1 on not found
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int engine_get_row(IntPtr h, uint recordId,
            StringBuilder name,       int nameMax,
            StringBuilder parentPath, int parentMax,
            out ulong sizeBytes,
            out ulong modifiedFileTime,
            out ushort attributes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int engine_get_status(IntPtr h, StringBuilder buf, int bufMax);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern void engine_set_scope_roots(IntPtr h,
            [In] string[]? paths, int count);
    }

    // ------------------------------------------------------------------ //
    // Fields                                                               //
    // ------------------------------------------------------------------ //

    private IntPtr _handle;
    private readonly Subject<string>                     _statusChanges  = new();
    private readonly Subject<int>                        _metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>>        _results        = new();
    private readonly CancellationTokenSource             _cts            = new();
    private readonly Task                                _watchTask;

    // ------------------------------------------------------------------ //
    // IEngineClient                                                        //
    // ------------------------------------------------------------------ //

    public IObservable<string>             StatusChanges  => _statusChanges;
    public IObservable<int>                MetricsChanges => _metricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => _results;

    // ------------------------------------------------------------------ //
    // Construction                                                         //
    // ------------------------------------------------------------------ //

    /// <param name="scopeRoots">Optional directory roots to restrict the index to.
    /// Pass before the engine starts so the initial scan respects the scope.</param>
    public NativeEngineClient(string[]? scopeRoots = null)
    {
        _handle = Native.engine_create();
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("engine_create() returned null.");

        if (scopeRoots?.Length > 0)
            Native.engine_set_scope_roots(_handle, scopeRoots, scopeRoots.Length);

        Native.engine_start(_handle);
        _watchTask = Task.Factory.StartNew(
            WatchLoop, _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ //
    // IEngineClient methods                                                //
    // ------------------------------------------------------------------ //

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (_handle != IntPtr.Zero)
            Native.engine_search(_handle, query);
        return Task.CompletedTask;
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        if (_handle == IntPtr.Zero) return Task.CompletedTask;
        int sortKey = key switch
        {
            "path"     => 1,
            "size"     => 2,
            "modified" => 3,
            _          => 0,
        };
        Native.engine_sort(_handle, sortKey, descending ? 1 : 0);
        return Task.CompletedTask;
    }

    public Task<ResultRowModel> GetRowAsync(uint recordId, CancellationToken cancellationToken)
    {
        if (_handle == IntPtr.Zero)
            return Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));

        var name       = new StringBuilder(512);
        var parentPath = new StringBuilder(4096);
        int rc = Native.engine_get_row(
            _handle, recordId,
            name, 512, parentPath, 4096,
            out ulong sizeBytes,
            out ulong modifiedFileTime,
            out ushort attributes);

        if (rc != 0)
            return Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));

        var modified = modifiedFileTime > 0
            ? DateTimeOffset.FromFileTime((long)modifiedFileTime)
            : DateTimeOffset.UtcNow;

        return Task.FromResult(new ResultRowModel(
            name.ToString(),
            parentPath.ToString(),
            sizeBytes,
            modified,
            FormatAttributes((System.IO.FileAttributes)attributes)));
    }

    // ------------------------------------------------------------------ //
    // Background result watcher                                            //
    // ------------------------------------------------------------------ //

    private void WatchLoop()
    {
        var statusBuf = new StringBuilder(1024);
        string lastStatus = string.Empty;

        while (!_cts.IsCancellationRequested)
        {
            if (_handle == IntPtr.Zero) break;

            int rc = Native.engine_wait_results_changed(_handle, 16, out int count);

            if (rc == -1) break; // engine stopped

            // Read current status on every tick
            statusBuf.Clear();
            string? currentStatus = null;
            if (Native.engine_get_status(_handle, statusBuf, 1024) == 0 && statusBuf.Length > 0)
                currentStatus = statusBuf.ToString();

            if (rc == 0)
            {
                // Timeout — emit status only if it changed (indexing progress updates)
                if (currentStatus != null && currentStatus != lastStatus)
                {
                    lastStatus = currentStatus;
                    _statusChanges.OnNext(currentStatus);
                }
                continue;
            }

            // Results changed — always emit status, metrics, and IDs
            if (currentStatus != null)
            {
                lastStatus = currentStatus;
                _statusChanges.OnNext(currentStatus);
            }

            // Re-read the count immediately before fetching IDs to minimise the race window
            // between engine_wait_results_changed (which snapshots the count) and
            // engine_get_result_ids (which reads the live results vector).  Both calls
            // are still not atomic, but the window is now two P/Invoke frames rather than
            // an arbitrary OS scheduling gap.
            int actualCount = Native.engine_result_count(_handle);
            _metricsChanges.OnNext(actualCount);

            var ids = new uint[actualCount];
            if (actualCount > 0)
                Native.engine_get_result_ids(_handle, ids, actualCount);

            _results.OnNext(ids);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers                                                              //
    // ------------------------------------------------------------------ //

    private static string FormatAttributes(System.IO.FileAttributes attr)
    {
        return string.Concat(
            attr.HasFlag(System.IO.FileAttributes.ReadOnly)  ? "R" : "",
            attr.HasFlag(System.IO.FileAttributes.Hidden)    ? "H" : "",
            attr.HasFlag(System.IO.FileAttributes.System)    ? "S" : "",
            attr.HasFlag(System.IO.FileAttributes.Archive)   ? "A" : "",
            attr.HasFlag(System.IO.FileAttributes.Directory) ? "D" : "");
    }

    // ------------------------------------------------------------------ //
    // Availability probe                                                   //
    // ------------------------------------------------------------------ //

    public string GetCurrentStatus()
    {
        if (_handle == IntPtr.Zero) return "";
        var buf = new StringBuilder(1024);
        return Native.engine_get_status(_handle, buf, 1024) == 0 ? buf.ToString() : "";
    }

    /// <summary>Returns true if WhereIsIt.Engine.Native.dll is present.</summary>
    public static bool IsAvailable()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, DllName);
            return File.Exists(path);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ //
    // Dispose                                                              //
    // ------------------------------------------------------------------ //

    public void Dispose()
    {
        _cts.Cancel();

        var h = _handle;
        if (h != IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            Native.engine_stop(h);
        }

        try { _watchTask.Wait(TimeSpan.FromSeconds(3)); } catch { }

        if (h != IntPtr.Zero) Native.engine_destroy(h);

        _statusChanges.Dispose();
        _metricsChanges.Dispose();
        _results.Dispose();
        _cts.Dispose();
    }
}
