using Microsoft.Extensions.DependencyInjection;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public static class AppBootstrap
{
    public static ServiceProvider Build(IServiceCollection services, IAppDispatcher? dispatcher = null,
        bool forceEnableHttp = false)
    {
        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();
        if (forceEnableHttp) settings.EnableHttpServer = true;
        StartupRegistration.Apply(settings.StartWithWindows);
        ShellMenuRegistration.Apply(settings.ShellContextMenu);

        // Apply persisted column-visibility BEFORE any view model loads — the
        // DataTemplate ColumnDefinition.Width binds OneTime to these statics.
        ColumnSettings.Current.ShowCreatedColumn  = settings.ShowCreatedColumn;
        ColumnSettings.Current.ShowAccessedColumn = settings.ShowAccessedColumn;
        ColumnSettings.Current.ShowRunCountColumn = settings.ShowRunCountColumn;
        ColumnSettings.Current.ShowPreviewPane    = settings.ShowPreviewPane;
        ColumnSettings.Current.ShowDimensionsColumn = settings.ShowDimensionsColumn;
        ColumnSettings.Current.ShowArtistColumn   = settings.ShowArtistColumn;
        ColumnSettings.Current.ShowAlbumColumn    = settings.ShowAlbumColumn;
        ColumnSettings.Current.ShowAuthorColumn   = settings.ShowAuthorColumn;
        ColumnSettings.Current.SizeColPx          = settings.SizeColumnPx;
        ColumnSettings.Current.ModifiedColPx      = settings.ModifiedColumnPx;
        ColumnSettings.Current.TypeColPx          = settings.TypeColumnPx;
        ColumnSettings.Current.AttrColPx          = settings.AttrColumnPx;
        ColumnSettings.Current.ThumbnailSizePx    = settings.ThumbnailSizePx;

        var history = new SearchHistory();
        history.Load(settings.SearchHistory);

        var bookmarks = new BookmarkService();
        bookmarks.Load(settings.Bookmarks);

        var runCounts = new RunCountService();
        runCounts.Load(settings.RunCounts);
        runCounts.LoadRunDates(settings.RunDates);

        services.AddSingleton<AppSettingsService>(_ => settingsService);
        services.AddSingleton<SearchHistory>(_ => history);
        services.AddSingleton<BookmarkService>(_ => bookmarks);
        services.AddSingleton<RunCountService>(_ => runCounts);
        services.AddSingleton(_ => new ThumbnailService { CurrentSize = (ThumbnailSize)settings.ThumbnailSizePx });
        services.AddSingleton<IEngineClient>(_ => EngineClientFactory.Create(
            scopeRoots: settings.ScopeRoots.Length > 0 ? settings.ScopeRoots : null,
            runCounts: runCounts));

        // Optional HTTP frontend — bound to 127.0.0.1 only.
        if (settings.EnableHttpServer)
        {
            services.AddSingleton(provider =>
            {
                var engine = provider.GetRequiredService<IEngineClient>();
                var srv = new HttpSearchServer(engine, settings.HttpServerPort);
                try { srv.Start(); } catch { /* port may be in use; swallow */ }
                return srv;
            });
        }
        // Optional FTP frontend — bound to 127.0.0.1 only, read-only, opt-in.
        if (settings.EnableFtpServer)
        {
            services.AddSingleton(provider =>
            {
                var rootDir = settings.ScopeRoots.Length > 0
                    ? settings.ScopeRoots[0]
                    : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                // Passing the engine turns the FTP server into an ETP server too.
                var ftp = new FtpServer(rootDir, settings.FtpServerPort, provider.GetRequiredService<IEngineClient>());
                try { ftp.Start(); } catch { /* port may be in use; swallow */ }
                return ftp;
            });
        }
        services.AddSingleton(dispatcher ?? (IAppDispatcher)new InlineDispatcher());
        services.AddTransient<MainViewModel>();
        services.AddTransient<SearchBoxViewModel>();
        services.AddTransient<ResultsListViewModel>();
        services.AddTransient<ResultRowViewModel>();
        services.AddTransient<StatusBarViewModel>();
        services.AddSingleton(_ => new SettingsViewModel(settingsService, StartupRegistration.Apply));
        var provider = services.BuildServiceProvider();
        // Singleton factories are lazy. Resolve the optional server eagerly or
        // merely registering it leaves the configured endpoint permanently off.
        if (settings.EnableHttpServer) provider.GetRequiredService<HttpSearchServer>();
        if (settings.EnableFtpServer) provider.GetRequiredService<FtpServer>();
        return provider;
    }
}
