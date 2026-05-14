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
    private readonly WhereIsIt.App.Services.AppSettingsService? settingsService;
    private readonly WhereIsIt.App.Services.BookmarkService?    bookmarkService;
    private readonly WhereIsIt.App.Services.RunCountService?    runCountService;
    private readonly ThumbnailService?                          thumbnailService;
    private GlobalHotkeyHost? hotkeyHost;

    public MainViewModel ViewModel { get; }

    public MainWindow(IServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
        ViewModel        = services.GetRequiredService<MainViewModel>();
        // Cache singleton services so async hot paths (OnContainerContentChanging,
        // OnOpenSelected) don't re-enter the DI container — and so a late
        // continuation that fires after ServiceProvider.Dispose doesn't
        // throw ObjectDisposedException on the UI thread.
        settingsService  = services.GetService(typeof(WhereIsIt.App.Services.AppSettingsService)) as WhereIsIt.App.Services.AppSettingsService;
        bookmarkService  = services.GetService(typeof(WhereIsIt.App.Services.BookmarkService))    as WhereIsIt.App.Services.BookmarkService;
        runCountService  = services.GetService(typeof(WhereIsIt.App.Services.RunCountService))    as WhereIsIt.App.Services.RunCountService;
        thumbnailService = services.GetService(typeof(ThumbnailService))                          as ThumbnailService;

        TrySetMicaBackdrop();
        TrySetWindowIcon();
        TryRegisterGlobalHotkey();
        Closed += OnClosedReleaseHotkey;
        Closed += OnClosedPersistTabs;

        RefreshBookmarksMenu();
        Activated += OnFirstActivatedShowRestorePrompt;
    }

    private void OnFirstActivatedShowRestorePrompt(object sender, WindowActivatedEventArgs args)
    {
        // One-shot — ContentDialog needs XamlRoot which isn't reliably ready
        // in the constructor for unpackaged WinUI 3 windows.
        Activated -= OnFirstActivatedShowRestorePrompt;
        _ = MaybePromptRestoreTabsAsync();
    }

    // ── Bootstrapping ───────────────────────────────────────────────────

    private void TrySetMicaBackdrop()
    {
        try { SystemBackdrop = new MicaBackdrop(); }
        catch { /* unsupported on older Windows builds */ }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WhereIsIt.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* ignore — icon is cosmetic */ }
    }

    private void TryRegisterGlobalHotkey()
    {
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

    private void OnClosedPersistTabs(object sender, WindowEventArgs args)
    {
        if (settingsService is null) return;
        try
        {
            var snap = WhereIsIt.App.Services.TabRestoreService
                .SnapshotForPersistence(ViewModel.Tabs.Tabs);
            settingsService.SaveLastSessionTabs(snap);
        }
        catch { /* settings I/O must never crash the shutdown path */ }
    }

    private async System.Threading.Tasks.Task MaybePromptRestoreTabsAsync()
    {
        if (settingsService is null) return;
        var last = settingsService.Load().LastSessionTabs;
        if (!WhereIsIt.App.Services.TabRestoreService.WorthRestoring(last)) return;

        var dialog = new ContentDialog
        {
            Title = "Restore previous tabs?",
            Content = $"WhereIsIt had {last!.Length} tab{(last.Length == 1 ? "" : "s")} open last time.",
            PrimaryButtonText   = "Restore",
            CloseButtonText     = "Start fresh",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        ContentDialogResult result;
        // Leave LastSessionTabs intact across dialog failures — a botched
        // prompt should NOT silently wipe the snapshot; user gets one more chance.
        try { result = await dialog.ShowAsync(); }
        catch { return; }

        if (result == ContentDialogResult.Primary)
        {
            bool first = true;
            foreach (var raw in last)
            {
                if (first) { ViewModel.Tabs.Tabs[0].Query = raw; first = false; }
                else { ViewModel.Tabs.AddTab(raw); }
            }
            ViewModel.Tabs.CurrentTab = ViewModel.Tabs.Tabs[0];
            ViewModel.SearchBox.SetQueryFromRaw(ViewModel.Tabs.Tabs[0].Query);
        }

        // User made an explicit decision — clear the snapshot so we don't
        // re-prompt on the next launch.
        try { settingsService.SaveLastSessionTabs(System.Array.Empty<string>()); } catch { }
    }

    private void RefreshBookmarksMenu()
    {
        var bm = bookmarkService;
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
            item.Click += (_, __) => ViewModel.SearchBox.SetQueryFromRaw(captured.Query);
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
        // ListView recycles containers during fast scroll. When that fires we
        // CANCEL the row's in-flight thumbnail fetch — without this the user
        // can pile up hundreds of stale StorageFile.GetThumbnailAsync tasks
        // by flick-scrolling, and the UI thread stalls draining them later.
        if (args.Item is not ResultRowViewModel row) return;
        if (args.InRecycleQueue)
        {
            row.CancelThumbnail();
            row.ThumbnailSource = null;
            return;
        }

        await row.EnsureLoadedAsync(System.Threading.CancellationToken.None);
        if (runCountService is not null) row.RunCount = runCountService.Get(row.FullPath);

        var thumbs = thumbnailService;
        var size = thumbs?.CurrentSize ?? WhereIsIt.App.Services.ThumbnailSize.Off;
        if (thumbs is null || size == WhereIsIt.App.Services.ThumbnailSize.Off) return;

        // Cache hit → set synchronously to avoid the async dispatch flicker.
        if (thumbs.TryGetCached(row.FullPath, size, out var cached))
        {
            row.ThumbnailSource = cached;
            return;
        }

        var token = row.BeginThumbnailLoad();
        var captured = row;
        _ = thumbs.GetAsync(captured.FullPath, size, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested) captured.ThumbnailSource = t.Result;
                });
            }
        }, System.Threading.Tasks.TaskScheduler.Default);
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
        ViewModel.SearchBox.ActiveFilter = tag;
    }

    private void OnFocusSearchClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
        => ViewModel.SearchBox.SetQueryFromRaw(string.Empty);

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
        if (bookmarkService is null) return;

        var name = await PromptForNameAsync(ViewModel.SearchBox.Query);
        if (string.IsNullOrWhiteSpace(name)) return;

        bookmarkService.Add(name, ViewModel.SearchBox.Query);
        settingsService?.SaveBookmarks(bookmarkService.Snapshot());
        RefreshBookmarksMenu();
    }

    // ── View menu ───────────────────────────────────────────────────────
    // Column/thumbnail toggles are bound TwoWay (or OneWay for radios) to
    // ColumnSettings.Current — the XAML reflows live. Click handlers only
    // need to persist the new state to settings.

    private void OnThumbnailSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tagStr } || !int.TryParse(tagStr, out var px)) return;
        ColumnSettings.Current.ThumbnailSizePx = px;
        if (thumbnailService is not null) thumbnailService.CurrentSize = (WhereIsIt.App.Services.ThumbnailSize)px;
        if (settingsService is null) return;
        var current = settingsService.Load();
        current.ThumbnailSizePx = px;
        settingsService.Save(current);
    }

    private void OnColumnToggleClick(object sender, RoutedEventArgs e)
    {
        if (settingsService is null) return;
        settingsService.SaveColumnVisibility(
            ColumnSettings.Current.ShowCreatedColumn,
            ColumnSettings.Current.ShowAccessedColumn,
            ColumnSettings.Current.ShowRunCountColumn);
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

        if (runCountService is not null)
        {
            runCountService.Increment(path);
            row.RunCount = runCountService.Get(path);
            settingsService?.SaveRunCounts(runCountService.Snapshot());
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
