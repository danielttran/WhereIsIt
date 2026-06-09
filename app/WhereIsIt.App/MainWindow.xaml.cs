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
    private TrayIconHost? trayIcon;
    private EverythingIpcServer? ipcServer;

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
        TrySetupTrayIcon();
        TrySetupEverythingIpc();
        AppWindow.Changed += OnAppWindowChanged;
        // Close-to-tray: when the user clicks the X (or hits Alt+F4), hide the
        // window to the tray icon instead of tearing down the engine, so the
        // index stays warm and re-opening is instant. The tray icon's "Exit"
        // command is the only thing that actually closes the process.
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosedReleaseHotkey;
        Closed += OnClosedPersistTabs;

        RefreshBookmarksMenu();
        ViewModel.ResultsList.PropertyChanged += OnResultsListPropertyChanged;
        try { ShellMenuToggle.IsChecked = settingsService?.Load().ShellContextMenu ?? false; } catch { }
        Activated += OnFirstActivatedShowRestorePrompt;
    }

    // ── Column resize (header grippers) ─────────────────────────────────

    private void OnColumnResize(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        ColumnSettings.Current.ResizeColumn(key, e.HorizontalChange);
        var c = ColumnSettings.Current;
        settingsService?.SaveColumnWidths(c.SizeColPx, c.ModifiedColPx, c.TypeColPx, c.AttrColPx);
    }

    // ── Custom property columns ─────────────────────────────────────────

    private void OnPropertyColumnToggleClick(object sender, RoutedEventArgs e)
    {
        var c = ColumnSettings.Current;
        settingsService?.SavePropertyColumns(c.ShowDimensionsColumn, c.ShowArtistColumn, c.ShowAlbumColumn, c.ShowAuthorColumn);
        // Refresh values for already-realized rows so a freshly-enabled column fills in.
        foreach (var row in ViewModel.ResultsList.Rows) LoadPropertyColumns(row);
    }

    private void LoadPropertyColumns(ViewModels.ResultRowViewModel row)
    {
        var c = ColumnSettings.Current;
        bool needAudio = c.ShowArtistColumn || c.ShowAlbumColumn;
        if (!c.ShowDimensionsColumn && !needAudio && !c.ShowAuthorColumn) return;

        var path = row.FullPath;
        if (string.IsNullOrEmpty(path)) return;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            string dim = string.Empty, artist = string.Empty, album = string.Empty, author = string.Empty;
            try
            {
                if (c.ShowDimensionsColumn &&
                    WhereIsIt.App.Services.ImageDimensions.TryRead(path, out int w, out int h))
                    dim = $"{w}×{h}";
                if (needAudio && WhereIsIt.App.Services.AudioTags.TryRead(path, out var tags))
                {
                    artist = tags.Artist ?? string.Empty;
                    album = tags.Album ?? string.Empty;
                }
                if (c.ShowAuthorColumn &&
                    WhereIsIt.App.Services.DocumentProps.TryRead(path, out var doc))
                    author = doc.Author ?? string.Empty;
            }
            catch { /* unreadable — leave blank */ }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (c.ShowDimensionsColumn) row.DimensionsText = dim;
                if (c.ShowArtistColumn) row.ArtistText = artist;
                if (c.ShowAlbumColumn) row.AlbumText = album;
                if (c.ShowAuthorColumn) row.AuthorText = author;
            });
        });
    }

    private void OnShellMenuToggleClick(object sender, RoutedEventArgs e)
    {
        bool on = (sender as ToggleMenuFlyoutItem)?.IsChecked ?? false;
        ShellMenuRegistration.Apply(on);
        if (settingsService is null) return;
        var s = settingsService.Load();
        s.ShellContextMenu = on;
        settingsService.Save(s);
    }

    // ── Preview pane ────────────────────────────────────────────────────

    private void OnResultsListPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.ResultsListViewModel.SelectedRow))
            _ = UpdatePreviewAsync();
    }

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
    {
        settingsService?.SavePreviewPane(ColumnSettings.Current.ShowPreviewPane);
        _ = UpdatePreviewAsync();
    }

    private static readonly System.Collections.Generic.HashSet<string> PreviewImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };
    private static readonly System.Collections.Generic.HashSet<string> PreviewTextExts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg",
            ".cs", ".c", ".cpp", ".h", ".hpp", ".py", ".js", ".ts", ".html", ".htm", ".css",
            ".sql", ".sh", ".ps1", ".bat", ".cmd", ".rs", ".go", ".java", ".rb", ".php",
        };

    private async System.Threading.Tasks.Task UpdatePreviewAsync()
    {
        try
        {
            if (!ColumnSettings.Current.ShowPreviewPane) return;
            var row = ViewModel.ResultsList.SelectedRow;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewText.Visibility = Visibility.Collapsed;
            if (row is null) { PreviewName.Text = string.Empty; PreviewInfo.Text = string.Empty; return; }

            await row.EnsureLoadedAsync(System.Threading.CancellationToken.None);
            var path = row.FullPath;
            PreviewName.Text = row.Name;

        try
        {
            var fi = new System.IO.FileInfo(path);
            PreviewInfo.Text = fi.Exists
                ? $"{ViewModels.ResultRowViewModel.FormatBytes((ulong)fi.Length)} · {fi.LastWriteTime:yyyy-MM-dd HH:mm}\n{path}"
                : path;
        }
        catch { PreviewInfo.Text = path; }

        var ext = System.IO.Path.GetExtension(path);
        if (PreviewImageExts.Contains(ext))
        {
            try
            {
                PreviewImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
                PreviewImage.Visibility = Visibility.Visible;
            }
            catch { /* unreadable image — leave hidden */ }
        }
        else if (PreviewTextExts.Contains(ext))
        {
            try
            {
                var text = await ReadHeadAsync(path, 16 * 1024);
                PreviewText.Text = text;
                PreviewText.Visibility = Visibility.Visible;
            }
            catch { /* unreadable text — leave hidden */ }
        }
        }
        catch (Exception ex) { LogHandlerCrash(nameof(UpdatePreviewAsync), ex); }
    }

    private static async System.Threading.Tasks.Task<string> ReadHeadAsync(string path, int maxChars)
    {
        using var reader = new System.IO.StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buf = new char[maxChars];
        int n = await reader.ReadAsync(buf, 0, maxChars);
        var head = new string(buf, 0, n);
        return n >= maxChars ? head + "\n…" : head;
    }

    private void OnFirstActivatedShowRestorePrompt(object sender, WindowActivatedEventArgs args)
    {
        try
        {
            // One-shot — ContentDialog needs XamlRoot which isn't reliably ready
            // in the constructor for unpackaged WinUI 3 windows.
            Activated -= OnFirstActivatedShowRestorePrompt;
            _ = SafeMaybePromptRestoreTabsAsync();
        }
        catch (Exception ex) { LogHandlerCrash(nameof(OnFirstActivatedShowRestorePrompt), ex); }
    }

    private async System.Threading.Tasks.Task SafeMaybePromptRestoreTabsAsync()
    {
        try { await MaybePromptRestoreTabsAsync(); }
        catch (Exception ex) { LogHandlerCrash(nameof(MaybePromptRestoreTabsAsync), ex); }
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
        // Restore the presenter too, so a window that was minimized-to-tray
        // comes back to a normal (not minimized) state.
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ── System tray / minimize-to-tray ──────────────────────────────────

    private void TrySetupTrayIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WhereIsIt.ico");
            trayIcon = new TrayIconHost("WhereIsIt", iconPath, BringToFront, Close);
        }
        catch { /* tray icon is optional — never block startup on it */ }
    }

    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        // Minimize-to-tray: only when the tray icon is live (otherwise the user
        // would have no way to bring the window back).
        if (trayIcon is null) return;
        if (sender.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized })
        {
            sender.Hide();
        }
    }

    // Title-bar X / Alt+F4 → divert to tray instead of process exit. When the
    // tray isn't live (icon failed to register), let the close proceed so the
    // user isn't stuck with an unhideable window.
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        try
        {
            if (trayIcon is null) return;
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                try { HideToTray(); } catch { }
            });
        }
        catch (Exception ex) { LogHandlerCrash(nameof(OnAppWindowClosing), ex); }
    }

    /// <summary>Hide the window so only the tray icon remains. Engine + index
    /// stay alive; <see cref="BringFromTray"/> restores from this state.</summary>
    public void HideToTray() => AppWindow.Hide();

    /// <summary>Reverse of <see cref="HideToTray"/>: show + foreground + focus
    /// the search box ready for input.</summary>
    public void BringFromTray() => BringToFront();

    private void TrySetupEverythingIpc()
    {
        try
        {
            if (settingsService?.Load().EnableEverythingIpc != true) return;
            if (services.GetService(typeof(WhereIsIt.App.Contracts.IEngineClient))
                is WhereIsIt.App.Contracts.IEngineClient engine)
                ipcServer = new EverythingIpcServer(engine);
        }
        catch { /* IPC is optional — never block startup on it */ }
    }

    private void OnClosedReleaseHotkey(object sender, WindowEventArgs args)
    {
        AppWindow.Changed -= OnAppWindowChanged;
        hotkeyHost?.Dispose();
        trayIcon?.Dispose();
        ipcServer?.Dispose();
    }

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

    // CRASH FIX: this is `async void`, fired by ListView every time a row's
    // container is realised. During indexing, `row.EnsureLoadedAsync` calls
    // the engine's GetRowAsync — if the engine throws (record ID transiently
    // out-of-range while the index grows, P/Invoke failure during USN drain,
    // etc.) the exception bubbles to the WinUI dispatcher and is rethrown as
    // a STATUS_STOWED_EXCEPTION (0xc000027b), which the user reported as
    // "app crashed while indexing." Wrap the entire body — an exception here
    // must never kill the process; the worst valid behaviour is "this row
    // shows blank until the next realisation pass."
    private async void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        try
        {
            if (args.Item is not ResultRowViewModel row) return;
            if (args.InRecycleQueue)
            {
                row.CancelThumbnail();
                row.ThumbnailSource = null;
                return;
            }

            await row.EnsureLoadedAsync(System.Threading.CancellationToken.None);
            if (runCountService is not null) row.RunCount = runCountService.Get(row.FullPath);
            LoadPropertyColumns(row);

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
                        try { if (!token.IsCancellationRequested) captured.ThumbnailSource = t.Result; }
                        catch { }
                    });
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
        catch (System.Exception ex)
        {
            // Best-effort log so a recurrence leaves a trail. The whole purpose
            // is to keep the dispatcher alive.
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whereisit-crashes.log"),
                    $"[{System.DateTimeOffset.Now:O}] OnContainerContentChanging: {ex}\n\n");
            }
            catch { }
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
    private void OnExportEfuClick(object sender, RoutedEventArgs e) => _ = ExportAsync(".efu");

    private async System.Threading.Tasks.Task ExportAsync(string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        if (extension == ".tsv")
            picker.FileTypeChoices.Add("TSV (tab-separated)", new System.Collections.Generic.List<string> { ".tsv" });
        else if (extension == ".efu")
            picker.FileTypeChoices.Add("Everything file list", new System.Collections.Generic.List<string> { ".efu" });
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

        var content = extension switch
        {
            ".tsv" => WhereIsIt.App.Services.ResultExporter.ToTsv(models),
            ".efu" => WhereIsIt.App.Services.ResultExporter.ToEfu(models),
            _ => WhereIsIt.App.Services.ResultExporter.ToCsv(models),
        };

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


    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = ViewModel.ResultsList.SelectedRow;
            if (row is null || string.IsNullOrEmpty(row.FullPath)) return;
            var newName = await PromptForTextAsync("Rename", "New name", row.Name, "Rename");
            if (string.IsNullOrWhiteSpace(newName) || newName == row.Name) return;
            if (!IsValidFileName(newName))
            {
                await ShowErrorAsync("Rename failed", "The new name contains characters that Windows does not allow in file names.");
                return;
            }

            try
            {
                var destination = System.IO.Path.Combine(row.ParentPath, newName);
                if (System.IO.Directory.Exists(row.FullPath)) System.IO.Directory.Move(row.FullPath, destination);
                else System.IO.File.Move(row.FullPath, destination);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Rename failed", ex.Message);
            }
        }
        catch (Exception ex) { LogHandlerCrash(nameof(OnRenameClick), ex); }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = ViewModel.ResultsList.SelectedRow;
            if (row is null || string.IsNullOrEmpty(row.FullPath)) return;
            var dialog = new ContentDialog
            {
                Title = "Move to Recycle Bin?",
                Content = $"Move “{row.Name}” to the Recycle Bin?",
                PrimaryButtonText = "Move to Recycle Bin",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            };
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); } catch { return; }
            if (result != ContentDialogResult.Primary) return;

            try
            {
                if (System.IO.Directory.Exists(row.FullPath))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(row.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(row.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Delete failed", ex.Message);
            }
        }
        catch (Exception ex) { LogHandlerCrash(nameof(OnDeleteClick), ex); }
    }

    private void OnPropertiesClick(object sender, RoutedEventArgs e)
    {
        var row = ViewModel.ResultsList.SelectedRow;
        if (row is null || string.IsNullOrEmpty(row.FullPath)) return;
        TryStart(row.FullPath, null, "properties");
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
        try
        {
            if (bookmarkService is null) return;

            var name = await PromptForTextAsync("Save bookmark", "Bookmark name", ViewModel.SearchBox.Query, "Save");
            if (string.IsNullOrWhiteSpace(name)) return;

            bookmarkService.Add(name, ViewModel.SearchBox.Query);
            settingsService?.SaveBookmarks(bookmarkService.Snapshot());
            RefreshBookmarksMenu();
        }
        catch (Exception ex) { LogHandlerCrash(nameof(OnSaveBookmarkClick), ex); }
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
        try
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
        catch (Exception ex) { LogHandlerCrash(nameof(OnAboutClick), ex); }
    }

    /// <summary>Any unhandled exception inside an `async void` UI handler is
    /// rethrown on the WinUI dispatcher and surfaces as STATUS_STOWED_EXCEPTION
    /// (0xc000027b) — process death. Every async-void handler in this class
    /// MUST funnel through this so an engine hiccup, a missing file, etc., is
    /// recorded instead of killing the app.</summary>
    private static void LogHandlerCrash(string source, Exception ex)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whereisit-crashes.log"),
                $"[{System.DateTimeOffset.Now:O}] {source}: {ex}\n\n");
        }
        catch { }
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    private async System.Threading.Tasks.Task<string?> PromptForTextAsync(
        string title, string placeholder, string defaultValue, string primaryButtonText)
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = defaultValue };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        var res = await dialog.ShowAsync();
        return res == ContentDialogResult.Primary ? input.Text : null;
    }

    private async System.Threading.Tasks.Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
        };
        try { await dialog.ShowAsync(); } catch { }
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
            settingsService?.SaveRunDates(runCountService.SnapshotRunDates());
        }
    }

    private static bool IsValidFileName(string name)
    {
        if (name is "." or ".." || name.TrimEnd(' ', '.') != name) return false;
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOf(System.IO.Path.DirectorySeparatorChar) >= 0 ||
            name.IndexOf(System.IO.Path.AltDirectorySeparatorChar) >= 0) return false;

        var stem = System.IO.Path.GetFileNameWithoutExtension(name).TrimEnd(' ', '.').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") return false;
        return !(stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9');
    }

    private static void TryStart(string fileName, string? args, string? verb = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? string.Empty,
                UseShellExecute = true,
                Verb = verb ?? string.Empty,
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
