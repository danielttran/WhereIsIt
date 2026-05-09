using System;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable querySubscription;
    private readonly IDisposable resultsSubscription;
    private readonly IDisposable statusSubscription;
    private readonly IDisposable metricsSubscription;

    public SearchBoxViewModel SearchBox { get; }
    public ResultsListViewModel ResultsList { get; }
    public StatusBarViewModel StatusBar { get; }

    public MainViewModel(IEngineClient engineClient, IAppDispatcher dispatcher)
    {
        SearchBox = new SearchBoxViewModel();
        ResultsList = new ResultsListViewModel(engineClient);
        StatusBar = new StatusBarViewModel();

        resultsSubscription = engineClient.ObserveResults
            .Subscribe(ids => dispatcher.Enqueue(() =>
            {
                ResultsList.BindResults(ids);
                StatusBar.RecordCount = ids.Count;
            }));

        statusSubscription = engineClient.StatusChanges
            .Subscribe(text => dispatcher.Enqueue(() => StatusBar.StatusText = text));

        metricsSubscription = engineClient.MetricsChanges
            .Subscribe(count => dispatcher.Enqueue(() => StatusBar.RecordCount = count));

        querySubscription = Observable
            .FromEventPattern<System.ComponentModel.PropertyChangedEventHandler, System.ComponentModel.PropertyChangedEventArgs>(
                h => SearchBox.PropertyChanged += h,
                h => SearchBox.PropertyChanged -= h)
            .Where(e => e.EventArgs.PropertyName == nameof(SearchBoxViewModel.Query))
            .Select(_ => SearchBox.Query)
            .Throttle(TimeSpan.FromMilliseconds(120))
            .DistinctUntilChanged()
            .SelectMany(async query =>
            {
                await engineClient.SearchAsync(query, default).ConfigureAwait(false);
                return query;
            })
            .Subscribe(_ => dispatcher.Enqueue(() => StatusBar.StatusText = "Search complete"));
    }

    public void Dispose()
    {
        querySubscription.Dispose();
        resultsSubscription.Dispose();
        statusSubscription.Dispose();
        metricsSubscription.Dispose();
    }
}
