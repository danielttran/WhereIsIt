using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        ViewModel = services.GetRequiredService<MainViewModel>();
    }
}
