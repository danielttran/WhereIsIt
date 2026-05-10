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

        services.AddSingleton<AppSettingsService>(_ => settingsService);
        services.AddSingleton<IEngineClient>(_ => EngineClientFactory.Create(
            scopeRoots: settings.ScopeRoots.Length > 0 ? settings.ScopeRoots : null));
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
