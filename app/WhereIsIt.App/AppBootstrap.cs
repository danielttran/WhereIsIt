using Microsoft.Extensions.DependencyInjection;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public static class AppBootstrap
{
    public static ServiceProvider Build(IServiceCollection services, IAppDispatcher? dispatcher = null)
    {
        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();

        // Apply persisted column-visibility BEFORE any view model loads — the
        // DataTemplate ColumnDefinition.Width binds OneTime to these statics.
        WhereIsIt.App.ViewModels.ResultRowViewModel.ShowCreatedColumn  = settings.ShowCreatedColumn;
        WhereIsIt.App.ViewModels.ResultRowViewModel.ShowAccessedColumn = settings.ShowAccessedColumn;
        WhereIsIt.App.ViewModels.ResultRowViewModel.ShowRunCountColumn = settings.ShowRunCountColumn;

        var history = new SearchHistory();
        history.Load(settings.SearchHistory);

        var bookmarks = new BookmarkService();
        bookmarks.Load(settings.Bookmarks);

        var runCounts = new RunCountService();
        runCounts.Load(settings.RunCounts);

        services.AddSingleton<AppSettingsService>(_ => settingsService);
        services.AddSingleton<SearchHistory>(_ => history);
        services.AddSingleton<BookmarkService>(_ => bookmarks);
        services.AddSingleton<RunCountService>(_ => runCounts);
        services.AddSingleton<IEngineClient>(_ => EngineClientFactory.Create(
            scopeRoots: settings.ScopeRoots.Length > 0 ? settings.ScopeRoots : null));

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
        services.AddSingleton(dispatcher ?? (IAppDispatcher)new InlineDispatcher());
        services.AddTransient<MainViewModel>();
        services.AddTransient<SearchBoxViewModel>();
        services.AddTransient<ResultsListViewModel>();
        services.AddTransient<ResultRowViewModel>();
        services.AddTransient<StatusBarViewModel>();
        services.AddSingleton<SettingsViewModel>();
        return services.BuildServiceProvider();
    }
}
