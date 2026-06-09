using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

public partial class App : Application
{
    public static ServiceProvider? Services { get; private set; }

    public App()
    {
        // Wire unhandled-exception sinks BEFORE InitializeComponent so an XAML
        // parse / generated-code failure during component init still leaves a
        // trace. The previous order (handlers AFTER init) meant a constructor
        // throw never reached the log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        try { InitializeComponent(); }
        catch (Exception ex) { WriteCrashLog("App.ctor InitializeComponent", ex); throw; }
        // Application.UnhandledException must be subscribed after the base
        // Application is constructed.
        this.UnhandledException += (_, e) =>
        {
            WriteCrashLog("Application.UnhandledException", e.Exception);
            e.Handled = true; // keep the app alive — the user wants no re-index per crash
        };
        WriteCrashLog("App.ctor", null);  // sentinel: process reached managed code.
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whereisit-crashes.log");
            var line = ex is null
                ? $"[{System.DateTimeOffset.Now:O}] {source} PID={System.Environment.ProcessId}{System.Environment.NewLine}"
                : $"[{System.DateTimeOffset.Now:O}] {source}: {ex}{System.Environment.NewLine}{System.Environment.NewLine}";
            System.IO.File.AppendAllText(path, line);
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WriteCrashLog("App.OnLaunched start", null);
        try { OnLaunchedCore(args); }
        catch (Exception ex)
        {
            WriteCrashLog("App.OnLaunched", ex);
            throw;
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        var dispatcher = new DispatcherQueueAppDispatcher(DispatcherQueue.GetForCurrentThread());
        var cli = CommandLineArgs.Parse(System.Environment.GetCommandLineArgs()[1..]);

        // --headless: run a one-shot query, print results to stdout, exit. The
        // app builds no window so it's safe to run from CI/tests/scripts and
        // doesn't fight the WinUI dispatcher.
        if (cli.Headless)
        {
            _ = RunHeadlessAsync(cli);
            return;
        }

        // Single-instance: if WhereIsIt is already running, forward the launch
        // arguments to that instance (it pops to front + seeds the new query)
        // and exit. Avoids re-indexing every time the user launches.
        if (SingleInstance.TryForwardAndExit(cli))
        {
            Exit();
            return;
        }
        SingleInstance.ListenForForwards();

        Services = AppBootstrap.Build(new ServiceCollection(), dispatcher,
            forceEnableHttp: cli.EnableHttp);
        var window = new MainWindow(Services);
        // Dispose the DI container when the main window closes so the
        // NativeEngineClient's Dispose runs — that's what triggers the C++
        // SaveIndex-on-Stop flush. Without this, the process exits abruptly
        // and every USN delta since the last 60-second incremental save is lost.
        window.Closed += (_, _) =>
        {
            // Cancel any in-flight thumbnail fetches on rows that were still
            // realized at close — otherwise their CancellationTokenSources
            // never get disposed and async continuations may hit a disposed
            // ServiceProvider below.
            try
            {
                foreach (var row in window.ViewModel.ResultsList.Rows)
                    row.CancelThumbnail();
            }
            catch { }

            try { Services?.Dispose(); } catch { }
            Services = null;
        };
        // -p <path> seeds a child:<path> scope filter so the launched window
        // immediately shows results limited to that folder — matching the
        // shell-context-menu verb ("Search with WhereIsIt" on a folder). When
        // both -s and -p are given, the user query is combined with the scope.
        var initialQuery = (cli.Query, cli.ScopeRoot) switch
        {
            (null,        null)        => null,
            (string q,    null)        => q,
            (null,        string path) => $"child:\"{path}\"",
            (string q,    string path) => $"{q} child:\"{path}\"",
        };
        if (initialQuery is not null)
            window.ViewModel.SearchBox.SetQueryFromRaw(initialQuery);

        // Make THIS the instance other launches forward to, AND keep the
        // window reference alive so tray-restore from a forwarded launch has
        // somewhere to send focus.
        SingleInstance.RegisterPrimaryWindow(window);

        if (cli.StartMinimised)
            window.HideToTray();
        else
            window.Activate();
    }

    // ── Headless mode ───────────────────────────────────────────────────

    // WinUI 3 apps are built as WinExe (Windows subsystem) so by default they
    // have no console — Console.WriteLine goes to a null sink. AttachConsole
    // hooks the parent terminal's console (cmd / PowerShell) so the headless
    // path can actually print results back to the caller. Returns false when
    // launched without a parent console (e.g., double-clicked); we then write
    // to a fallback log path so output isn't lost.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private async System.Threading.Tasks.Task RunHeadlessAsync(CommandLineArgs cli)
    {
        // --output PATH always wins: write the result list there and ignore the
        // console plumbing entirely. Otherwise try to attach to the parent
        // console (works when launched from cmd / a console-host PowerShell)
        // and fall back to a temp log file when neither is available.
        System.IO.StreamWriter? fallback = null;
        if (!string.IsNullOrEmpty(cli.OutputPath))
        {
            try
            {
                fallback = new System.IO.StreamWriter(cli.OutputPath) { AutoFlush = true };
                System.Console.SetOut(fallback);
                System.Console.SetError(fallback);
            }
            catch (System.Exception ex)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                System.Console.Error.WriteLine($"--output failed: {ex.Message}");
            }
        }
        else
        {
            bool hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!hasConsole)
            {
                try
                {
                    var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        $"whereisit-headless-{System.Diagnostics.Process.GetCurrentProcess().Id}.log");
                    fallback = new System.IO.StreamWriter(logPath) { AutoFlush = true };
                    System.Console.SetOut(fallback);
                    System.Console.SetError(fallback);
                }
                catch { /* if even the fallback fails, output silently disappears */ }
            }
        }

        // Headless never creates a XAML window, so it must NOT request the
        // WinUI dispatcher. We give the engine a no-op dispatcher and let it
        // run on the thread pool.
        var services = AppBootstrap.Build(new ServiceCollection(), dispatcher: null);
        IEngineClient engine;
        try { engine = services.GetRequiredService<IEngineClient>(); }
        catch (System.Exception ex)
        {
            System.Console.Error.WriteLine($"engine init failed: {ex.Message}");
            services.Dispose();
            fallback?.Dispose();
            Exit();
            return;
        }

        var rawQuery = (cli.Query, cli.ScopeRoot) switch
        {
            (null,     null)        => "*",
            (string q, null)        => q,
            (null,     string path) => $"child:\"{path}\"",
            (string q, string path) => $"{q} child:\"{path}\"",
        };

        System.Collections.Generic.IReadOnlyList<uint> last = System.Array.Empty<uint>();
        long lastEmit = 0;
        string lastStatus = string.Empty;

        using var statusSub = engine.StatusChanges.Subscribe(s =>
            System.Threading.Volatile.Write(ref lastStatus, s ?? string.Empty));
        using var sub = engine.ObserveResults.Subscribe(ids =>
        {
            System.Threading.Volatile.Write(ref lastEmit, System.Environment.TickCount64);
            last = ids;
        });

        try
        {
            // The native engine starts in "Indexing... N items" and only flips to
            // "Ready - N items" once the first full scan (or USN catch-up from a
            // persisted index.dat) completes. Issuing a search before then races
            // the index and routinely returns 0. Wait, bounded by --timeout/2,
            // then issue the search and wait for results to stabilise.
            var halfDeadline = System.Environment.TickCount64 + (cli.TimeoutSeconds * 1000L / 2);
            while (System.Environment.TickCount64 < halfDeadline)
            {
                var s = System.Threading.Volatile.Read(ref lastStatus);
                if (s.StartsWith("Ready", System.StringComparison.OrdinalIgnoreCase)) break;
                await System.Threading.Tasks.Task.Delay(150);
            }
            System.Console.Error.WriteLine($"[whereisit] status={System.Threading.Volatile.Read(ref lastStatus)}");

            // Reset emission tracker BEFORE the search — otherwise a stale
            // emission from the indexing phase (which set lastEmit minutes ago)
            // immediately satisfies the "quiet duration" check and the wait
            // exits with 0 results before our search even returned.
            System.Threading.Volatile.Write(ref lastEmit, 0);
            last = System.Array.Empty<uint>();

            await engine.SearchAsync(rawQuery, System.Threading.CancellationToken.None);

            // Wait for results to settle: any emission, then 500 ms of quiet
            // (no new emission) before printing. Bounded by --timeout.
            var deadline = System.Environment.TickCount64 + cli.TimeoutSeconds * 1000L;
            while (System.Environment.TickCount64 < deadline)
            {
                await System.Threading.Tasks.Task.Delay(100);
                var last1 = System.Threading.Volatile.Read(ref lastEmit);
                if (last1 != 0 && System.Environment.TickCount64 - last1 >= 500) break;
            }

            int cap = cli.MaxResults > 0 ? System.Math.Min(cli.MaxResults, last.Count) : last.Count;
            int hit = 0;
            for (int i = 0; i < cap; i++)
            {
                try
                {
                    var row = await engine.GetRowAsync(last[i], System.Threading.CancellationToken.None);
                    var line = cli.NameOnly
                        ? row.Name
                        : (string.IsNullOrEmpty(row.ParentPath) ? row.Name
                            : System.IO.Path.Combine(row.ParentPath, row.Name));
                    System.Console.Out.WriteLine(line);
                    hit++;
                }
                catch { /* skip unreadable */ }
            }
            // Pierce one level into the decorator so a slow/never-engaged
            // post-filter is visible from the headless log without needing a
            // debugger attached.
            int postFilterPasses = 0;
            string? simplified = null;
            if (engine is WhereIsIt.App.Services.FilteringEngineClient dec)
            {
                postFilterPasses = dec.PostFilterPassCount;
                simplified = dec.LastSimplifiedQuery;
            }
            System.Console.Error.WriteLine($"[whereisit] query={rawQuery!.Replace('\n',' ')} simplified={simplified} returned={last.Count} printed={hit} postfilterPasses={postFilterPasses}");
        }
        finally
        {
            try { services.Dispose(); } catch { }
            try { System.Console.Out.Flush(); } catch { }
            try { System.Console.Error.Flush(); } catch { }
            try { fallback?.Dispose(); } catch { }
            Exit();
        }
    }
}
