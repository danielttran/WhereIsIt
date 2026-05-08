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
        Services = AppBootstrap.Build(new ServiceCollection(), dispatcher);
        var window = new MainWindow(Services);
        window.Activate();
    }
}
