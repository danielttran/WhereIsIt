using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

public partial class App : Application
{
    public static ServiceProvider? Services { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = new DispatcherQueueAppDispatcher(DispatcherQueue.GetForCurrentThread());
        var cli = CommandLineArgs.Parse(System.Environment.GetCommandLineArgs()[1..]);

        Services = AppBootstrap.Build(new ServiceCollection(), dispatcher);
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
        if (cli.Query is not null)
            window.ViewModel.SearchBox.SetQueryFromRaw(cli.Query);
        window.Activate();
    }
}
