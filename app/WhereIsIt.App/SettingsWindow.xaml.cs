using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public sealed partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(IServiceProvider services)
    {
        InitializeComponent();
        ViewModel = services.GetRequiredService<SettingsViewModel>();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
