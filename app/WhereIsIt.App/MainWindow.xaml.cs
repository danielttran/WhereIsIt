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
        TryRegisterGlobalHotkey();
        Closed += OnClosedReleaseHotkey;

        InitColumnVisibilityMenu();
        RefreshBookmarksMenu();
    }

    // ── Bootstrapping ───────────────────────────────────────────────────

    private void TrySetMicaBackdrop()
    {
        try { SystemBackdrop = new MicaBackdrop(); }
        catch { /* unsupported on older Windows builds */ }
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

    private void InitColumnVisibilityMenu()
    {
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (settingsService is null) return;
        var s = settingsService.Load();
        ColCreatedItem.IsChecked  = s.ShowCreatedColumn;
        ColAccessedItem.IsChecked = s.ShowAccessedColumn;
        ColRunsItem.IsChecked     = s.ShowRunCountColumn;
    }

    private void RefreshBookmarksMenu()
    {
        var bm = services.GetService(typeof(WhereIsIt.App.Services.BookmarkService))
            as WhereIsIt.App.Services.BookmarkService;
        if (bm is null) return;

        // Items 0 and 1 are static (Save current search, separator). Strip
        // anything after and rebuild from the current bookmark list.
        while (BookmarksMenu.Items.Count > 2)
            BookmarksMenu.Items.RemoveAt(BookmarksMenu.Items.Count - 1);

        if (bm.Items.Count == 0) return;

        foreach (var entry in bm.Items)
        {
            var captured = entry;
            var item = new MenuFlyoutItem
            {
                Text = $"{captured.Name}   —   {captured.Query}",
            };
            item.Click += (_, __) => ViewModel.SearchBox.Query = captured.Query;
            BookmarksMenu.Items.Add(item);
        }

        BookmarksMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutSubItem { Text = "Delete bookmark" };
        foreach (var entry in bm.Items)
        {
            var captured = entry;
            var del = new MenuFlyoutItem { Text = captured.Name };
            del.Click += (_, __) =>
            {
                bm.Remove(captured.Name);
                var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
                    as WhereIsIt.App.Services.AppSettingsService;
                settingsService?.SaveBookmarks(bm.Snapshot());
                RefreshBookmarksMenu();
            };
            manage.Items.Add(del);
        }
        BookmarksMenu.Items.Add(manage);
    }

    // ── Result list events ──────────────────────────────────────────────

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

    private void OnAddTabClick(Microsoft.UI.Xaml.Controls.TabView sender, object args)
        => ViewModel.Tabs.AddTab();

    private void OnTabCloseRequested(Microsoft.UI.Xaml.Controls.TabView sender,
                                     Microsoft.UI.Xaml.Controls.TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is ViewModels.TabRecord rec)
            ViewModel.Tabs.CloseTab(rec);
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

    // ── File menu ───────────────────────────────────────────────────────

    private void OnExportCsvClick(object sender, RoutedEventArgs e) => _ = ExportAsync(".csv");
    private void OnExportTsvClick(object sender, RoutedEventArgs e) => _ = ExportAsync(".tsv");

    private async System.Threading.Tasks.Task ExportAsync(string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        if (extension == ".tsv")
            picker.FileTypeChoices.Add("TSV (tab-separated)", new System.Collections.Generic.List<string> { ".tsv" });
        else
            picker.FileTypeChoices.Add("CSV (comma-separated)", new System.Collections.Generic.List<string> { ".csv" });
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

        var content = extension == ".tsv"
            ? WhereIsIt.App.Services.ResultExporter.ToTsv(models)
            : WhereIsIt.App.Services.ResultExporter.ToCsv(models);

        await Windows.Storage.FileIO.WriteTextAsync(file, content);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // ── Edit menu ───────────────────────────────────────────────────────

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

    // ── Search menu ─────────────────────────────────────────────────────

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        ViewModel.SearchBox.Query =
            WhereIsIt.App.Services.QueryComposer.ApplyFilter(ViewModel.SearchBox.Query, tag);
    }

    private void OnFocusSearchClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
        => ViewModel.SearchBox.Query = string.Empty;

    private void OnNewTabClick(object sender, RoutedEventArgs e)
        => ViewModel.Tabs.AddTab();

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        var current = ViewModel.Tabs.CurrentTab;
        if (current is not null) ViewModel.Tabs.CloseTab(current);
    }

    // ── Bookmarks menu ──────────────────────────────────────────────────

    private async void OnSaveBookmarkClick(object sender, RoutedEventArgs e)
    {
        var bm = services.GetService(typeof(WhereIsIt.App.Services.BookmarkService))
            as WhereIsIt.App.Services.BookmarkService;
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (bm is null) return;

        var name = await PromptForNameAsync(ViewModel.SearchBox.Query);
        if (string.IsNullOrWhiteSpace(name)) return;

        bm.Add(name, ViewModel.SearchBox.Query);
        settingsService?.SaveBookmarks(bm.Snapshot());
        RefreshBookmarksMenu();
    }

    // ── View menu ───────────────────────────────────────────────────────

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
    {
        var settingsService = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService))
            as WhereIsIt.App.Services.AppSettingsService;
        if (settingsService is null) return;
        settingsService.SaveColumnVisibility(
            ColCreatedItem.IsChecked,
            ColAccessedItem.IsChecked,
            ColRunsItem.IsChecked);
        // Column widths are bound OneTime in XAML — column changes take effect
        // on next launch. Tell the user.
        _ = ShowRestartHintAsync();
    }

    private async System.Threading.Tasks.Task ShowRestartHintAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Restart to apply",
            Content = "Column visibility changes take effect the next time WhereIsIt starts.",
            CloseButtonText = "OK",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        try { await dialog.ShowAsync(); } catch { /* ignore double-open */ }
    }

    // ── Tools menu ──────────────────────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(services);
        settingsWindow.Activate();
    }

    // ── Help menu ───────────────────────────────────────────────────────

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "About WhereIsIt",
            Content = "WhereIsIt — a fast Windows file search tool.\n\n" +
                      "Modern WinUI 3 shell over a native C++ NTFS indexer.\n" +
                      "Familiar Everything-style query syntax: ext: size: dm: " +
                      "attrib: child: parent: dupe: content: and more.",
            CloseButtonText = "Close",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        try { await dialog.ShowAsync(); } catch { }
    }

    // ── Shared helpers ──────────────────────────────────────────────────

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
