using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider services;

    public MainViewModel ViewModel { get; }

    public MainWindow(IServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
        ViewModel = services.GetRequiredService<MainViewModel>();

        TrySetMicaBackdrop();
        WireGlobalShortcuts();
    }

    private void TrySetMicaBackdrop()
    {
        try { SystemBackdrop = new MicaBackdrop(); }
        catch { /* unsupported on older Windows builds */ }
    }

    private void WireGlobalShortcuts()
    {
        if (Content is not FrameworkElement root) return;

        var focusSearch = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.F };
        focusSearch.Invoked += (_, e) => { SearchTextBox.Focus(FocusState.Programmatic); SearchTextBox.SelectAll(); e.Handled = true; };
        root.KeyboardAccelerators.Add(focusSearch);

        var clear = new KeyboardAccelerator { Key = VirtualKey.Escape };
        clear.Invoked += (_, e) =>
        {
            if (!string.IsNullOrEmpty(ViewModel.SearchBox.Query))
            {
                ViewModel.SearchBox.Query = string.Empty;
                e.Handled = true;
            }
        };
        root.KeyboardAccelerators.Add(clear);

        var settings = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = VirtualKey.OemComma };
        settings.Invoked += (_, e) => { OnSettingsClick(this, new RoutedEventArgs()); e.Handled = true; };
        root.KeyboardAccelerators.Add(settings);
    }

    private void OnResultsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void OnRowDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => OpenSelected();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelected();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null || string.IsNullOrEmpty(row.ParentPath)) return;
        TryStart("explorer.exe", $"\"{row.ParentPath}\"");
    }

    private void OnCopyNameClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null) return;
        SetClipboardText(row.Name);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null) return;
        SetClipboardText(row.FullPath);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(services);
        settingsWindow.Activate();
    }

    private void OpenSelected()
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null) return;
        var path = row.FullPath;
        if (string.IsNullOrEmpty(path)) return;
        TryStart(path, null);
    }

    private static void TryStart(string fileName, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? string.Empty,
                UseShellExecute = true,
            });
        }
        catch { /* swallow — UI must not crash on bad path */ }
    }

    private static void SetClipboardText(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
    }
}
