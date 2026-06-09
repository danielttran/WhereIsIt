using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

/// <summary>
/// Single-instance gate + cross-launch forwarder. The first WhereIsIt process
/// owns a named pipe; subsequent launches connect, ship their CLI args over,
/// then exit so the original instance handles the request. This is what makes
/// re-launching cheap — the engine stays warm and the index never has to be
/// rebuilt for every new "open a search" gesture.
///
/// Wire shape: one JSON line per launch, sent over `WhereIsIt.Launch`. The
/// pipe is opened with the well-known PipeSecurity that allows the calling
/// user; cross-session forwarding is not supported (matches Everything's
/// "per-user" semantics and avoids needing admin to launch).
/// </summary>
internal static class SingleInstance
{
    private const string PipeName = LaunchForwarding.PipeName;
    private static readonly string MutexName = "Local\\WhereIsIt.SingleInstance." + Environment.UserName;

    private static Mutex? mutex;
    private static MainWindow? primaryWindow;
    private static CancellationTokenSource? listenCts;

    /// <summary>Try to acquire the single-instance mutex. When false, the
    /// caller already forwarded its args and should exit.</summary>
    public static bool TryForwardAndExit(CommandLineArgs cli)
    {
        // createdNew == true means we are the primary instance.
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return false;
        try { ForwardOverPipe(cli); }
        catch { /* primary is wedged — let the caller proceed anyway as a fallback */ }
        return true;
    }

    private static void ForwardOverPipe(CommandLineArgs cli)
    {
        var bytes = Encoding.UTF8.GetBytes(LaunchForwarding.Serialize(cli) + "\n");

        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        // 1500 ms — enough to wake a tray-docked primary, short enough that a
        // crashed primary doesn't hang us.
        client.Connect(LaunchForwarding.ConnectTimeoutMs);
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
    }

    /// <summary>Start the background pipe listener. Safe to call once at
    /// startup; subsequent calls are no-ops.</summary>
    public static void ListenForForwards()
    {
        if (listenCts is not null) return;
        listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoop(listenCts.Token));
    }

    public static void RegisterPrimaryWindow(MainWindow window)
    {
        primaryWindow = window;
        window.Closed += (_, _) =>
        {
            try { listenCts?.Cancel(); } catch { }
            listenCts = null;
            primaryWindow = null;
            try { mutex?.ReleaseMutex(); } catch { }
            try { mutex?.Dispose(); } catch { }
            mutex = null;
        };
    }

    private static async Task ListenLoop(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                int read = await server.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (read <= 0) continue;
                var text = Encoding.UTF8.GetString(buf, 0, read).Trim();
                if (text.Length == 0) continue;
                var payload = LaunchForwarding.Deserialize(text);
                if (payload is null) continue;
                Dispatch(payload);
            }
            catch (OperationCanceledException) { return; }
            catch { /* keep listening; one malformed forward shouldn't kill the loop */ }
            finally
            {
                try { server?.Dispose(); } catch { }
            }
        }
    }

    private static void Dispatch(LaunchForwarding.Payload payload)
    {
        // Sentinel BEFORE the null-window guard so a test harness can prove
        // the primary's ListenLoop received the forward, independent of
        // whether the main window has registered yet (it might still be
        // initialising during the indexing phase).
        LaunchForwarding.WriteSentinel(payload);
        var win = primaryWindow;
        if (win is null) return;
        win.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var raw = (payload.Query, payload.ScopeRoot) switch
                {
                    (null, null)        => null,
                    (string q, null)    => q,
                    (null, string path) => $"child:\"{path}\"",
                    (string q, string path) => $"{q} child:\"{path}\"",
                };
                if (raw is not null) win.ViewModel.SearchBox.SetQueryFromRaw(raw);
                if (payload.StartMinimised) win.HideToTray();
                else win.BringFromTray();
            }
            catch { /* never throw out of the dispatcher callback */ }
        });
    }
}
