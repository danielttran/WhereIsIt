using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider services;
    private GlobalHotkeyHost? hotkeyHost;

    public MainViewModel ViewModel { get; }

    public MainWindow(IServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
        ViewModel = services.GetRequiredService<MainViewModel>();

        TrySetMicaBackdrop();
        WireGlobalShortcuts();
        TryRegisterGlobalHotkey();
        Closed += OnClosedReleaseHotkey;
    }

    private void TryRegisterGlobalHotkey()
    {
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        var spec = settingsService?.Load().GlobalHotkey;
        var binding = WhereIsIt.App.Services.HotkeyBinding.Parse(spec ?? "Ctrl+Alt+W");
        if (binding is null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        hotkeyHost = new GlobalHotkeyHost(hwnd, binding, BringToFront);
    }

    private void BringToFront()
    {
        AppWindow.Show();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void OnClosedReleaseHotkey(object sender, WindowEventArgs args)
        => hotkeyHost?.Dispose();

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

        var settings = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)188 };
        settings.Invoked += (_, e) => { OnSettingsClick(this, new RoutedEventArgs()); e.Handled = true; };
        root.KeyboardAccelerators.Add(settings);
    }

    private async void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is ResultRowViewModel row)
        {
            await row.EnsureLoadedAsync(System.Threading.CancellationToken.None);
            var counts = services.GetService(typeof(WhereIsIt.App.Services.RunCountService))
                as WhereIsIt.App.Services.RunCountService;
            if (counts is not null) row.RunCount = counts.Get(row.FullPath);
        }
    }

    private void OnResultsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up:
                ViewModel.SearchBox.RecallPrev();
                SearchTextBox.SelectionStart = ViewModel.SearchBox.Query.Length;
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ViewModel.SearchBox.RecallNext();
                SearchTextBox.SelectionStart = ViewModel.SearchBox.Query.Length;
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ViewModel.SearchBox.Submit();
                var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
                    as WhereIsIt.App.Services.AppSettingsService;
                settingsService?.SaveSearchHistory(ViewModel.SearchBox.History.Snapshot());
                e.Handled = true;
                break;
        }
    }

    private void OnRowDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => OpenSelected();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelected();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null || string.IsNullOrEmpty(row.FullPath)) return;
        TryStart("explorer.exe", $"/select,\"{row.FullPath}\"");
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

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (settingsService is null) return;

        var current = settingsService.Load();
        var menu = new MenuFlyout();

        var createdItem = new ToggleMenuFlyoutItem
        {
            Text = "Show Created column",
            IsChecked = current.ShowCreatedColumn,
        };
        var accessedItem = new ToggleMenuFlyoutItem
        {
            Text = "Show Accessed column",
            IsChecked = current.ShowAccessedColumn,
        };
        var runCountItem = new ToggleMenuFlyoutItem
        {
            Text = "Show Runs column",
            IsChecked = current.ShowRunCountColumn,
        };
        Action persist = () => settingsService.SaveColumnVisibility(
            createdItem.IsChecked, accessedItem.IsChecked, runCountItem.IsChecked);
        createdItem.Click  += (_, __) => persist();
        accessedItem.Click += (_, __) => persist();
        runCountItem.Click += (_, __) => persist();

        menu.Items.Add(createdItem);
        menu.Items.Add(accessedItem);
        menu.Items.Add(runCountItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "(Restart the app to apply changes)",
            IsEnabled = false,
        });

        menu.ShowAt(ColumnsButton);
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        ViewModel.SearchBox.Query =
            WhereIsIt.App.Services.QueryComposer.ApplyFilter(ViewModel.SearchBox.Query, tag);
    }

    private void OnAddTabClick(Microsoft.UI.Xaml.Controls.TabView sender, object args)
    {
        ViewModel.Tabs.AddTab();
    }

    private void OnTabCloseRequested(Microsoft.UI.Xaml.Controls.TabView sender,
                                     Microsoft.UI.Xaml.Controls.TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is ViewModels.TabRecord rec)
            ViewModel.Tabs.CloseTab(rec);
    }

    private void OnModifierToggleClick(object sender, RoutedEventArgs e)
    {
        var mods = new WhereIsIt.App.Services.SearchModifiers(
            CaseSensitive: CaseToggle.IsChecked  == true,
            Regex:         RegexToggle.IsChecked == true,
            WholeWord:     WordToggle.IsChecked  == true,
            MatchPath:     PathToggle.IsChecked  == true);
        ViewModel.SearchBox.Query =
            WhereIsIt.App.Services.SearchModifiersComposer.Apply(ViewModel.SearchBox.Query, mods);
    }

    private void OnBookmarksClick(object sender, RoutedEventArgs e)
    {
        var bm = services.GetService(typeof(WhereIsIt.App.Services.BookmarkService))
            as WhereIsIt.App.Services.BookmarkService;
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (bm is null) return;

        var menu = new MenuFlyout();

        var saveItem = new MenuFlyoutItem { Text = "Save current query…" };
        saveItem.Click += async (_, __) =>
        {
            var name = await PromptForNameAsync(ViewModel.SearchBox.Query);
            if (!string.IsNullOrWhiteSpace(name))
            {
                bm.Add(name, ViewModel.SearchBox.Query);
                settingsService?.SaveBookmarks(bm.Snapshot());
            }
        };
        menu.Items.Add(saveItem);

        if (bm.Items.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var entry in bm.Items)
            {
                var captured = entry;
                var item = new MenuFlyoutItem
                {
                    Text = $"{captured.Name}   —   {captured.Query}",
                };
                item.Click += (_, __) => ViewModel.SearchBox.Query = captured.Query;
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            var manage = new MenuFlyoutSubItem { Text = "Delete…" };
            foreach (var entry in bm.Items)
            {
                var captured = entry;
                var del = new MenuFlyoutItem { Text = captured.Name };
                del.Click += (_, __) =>
                {
                    bm.Remove(captured.Name);
                    settingsService?.SaveBookmarks(bm.Snapshot());
                };
                manage.Items.Add(del);
            }
            menu.Items.Add(manage);
        }

        menu.ShowAt(BookmarksButton);
    }

    private void OnDragItemsStarting(object sender, Microsoft.UI.Xaml.Controls.DragItemsStartingEventArgs e)
    {
        var paths = new System.Collections.Generic.List<string>();
        foreach (var item in e.Items)
        {
            if (item is not ViewModels.ResultRowViewModel row) continue;
            var path = row.FullPath;
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }
        if (paths.Count == 0) { e.Cancel = true; return; }

        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.Data.SetText(string.Join(Environment.NewLine, paths));

        // Lazily resolve to StorageItems when a drop target asks for them
        // (Explorer, file dialogs, etc.). Synchronous gather above lets the
        // text payload work for editors that ask for plain text.
        e.Data.SetDataProvider(
            Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems,
            async req =>
            {
                var deferral = req.GetDeferral();
                try
                {
                    var items = new System.Collections.Generic.List<Windows.Storage.IStorageItem>();
                    foreach (var path in paths)
                    {
                        try
                        {
                            if (System.IO.Directory.Exists(path))
                                items.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path));
                            else if (System.IO.File.Exists(path))
                                items.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                        }
                        catch { /* skip */ }
                    }
                    req.SetData(items);
                }
                finally { deferral.Complete(); }
            });
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add("CSV (comma-separated)", new System.Collections.Generic.List<string> { ".csv" });
        picker.FileTypeChoices.Add("TSV (tab-separated)",   new System.Collections.Generic.List<string> { ".tsv" });
        picker.SuggestedFileName = string.IsNullOrEmpty(ViewModel.SearchBox.Query)
            ? "whereisit-results"
            : $"whereisit-{System.Text.RegularExpressions.Regex.Replace(ViewModel.SearchBox.Query, "[^A-Za-z0-9_-]", "_")}";

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var rows = ViewModel.ResultsList.Rows;
        var models = new System.Collections.Generic.List<WhereIsIt.App.Contracts.ResultRowModel>(rows.Count);
        foreach (var row in rows)
        {
            await row.EnsureLoadedAsync(System.Threading.CancellationToken.None);
            models.Add(row.ToModel());
        }

        var content = file.FileType.Equals(".tsv", System.StringComparison.OrdinalIgnoreCase)
            ? WhereIsIt.App.Services.ResultExporter.ToTsv(models)
            : WhereIsIt.App.Services.ResultExporter.ToCsv(models);

        await Windows.Storage.FileIO.WriteTextAsync(file, content);
    }

    private async System.Threading.Tasks.Task<string?> PromptForNameAsync(string defaultName)
    {
        var input = new TextBox { PlaceholderText = "Bookmark name", Text = defaultName };
        var dialog = new ContentDialog
        {
            Title = "Save bookmark",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        var res = await dialog.ShowAsync();
        return res == ContentDialogResult.Primary ? input.Text : null;
    }

    private void OpenSelected()
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null) return;
        var path = row.FullPath;
        if (string.IsNullOrEmpty(path)) return;
        TryStart(path, null);

        // Tally + persist the run count for the opened path.
        var counts = services.GetService(typeof(WhereIsIt.App.Services.RunCountService))
            as WhereIsIt.App.Services.RunCountService;
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (counts is not null)
        {
            counts.Increment(path);
            row.RunCount = counts.Get(path);
            settingsService?.SaveRunCounts(counts.Snapshot());
        }
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
