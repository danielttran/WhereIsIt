using Microsoft.Extensions.DependencyInjection;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App;

public static class AppBootstrap
{
    public static ServiceProvider Build(IServiceCollection services, IAppDispatcher? dispatcher = null)
    {
        services.AddSingleton<IEngineClient>(_ => EngineClientFactory.Create());
        services.AddSingleton(dispatcher ?? (IAppDispatcher)new InlineDispatcher());
        services.AddTransient<MainViewModel>();
        services.AddTransient<SearchBoxViewModel>();
        services.AddTransient<ResultsListViewModel>();
        services.AddTransient<ResultRowViewModel>();
        services.AddTransient<StatusBarViewModel>();
        services.AddTransient<SettingsViewModel>();
        return services.BuildServiceProvider();
    }
}
