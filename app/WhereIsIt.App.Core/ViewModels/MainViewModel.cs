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

    private string _engineStatus = "Starting...";

    public SearchBoxViewModel SearchBox { get; }
    public ResultsListViewModel ResultsList { get; }
    public StatusBarViewModel StatusBar { get; }
    public TabsViewModel Tabs { get; } = new();

    [ObservableProperty] private string emptyStateMessage = "Type to search...";

    private bool _syncingTab;

    public MainViewModel(IEngineClient engineClient, IAppDispatcher dispatcher)
        : this(engineClient, dispatcher, new SearchHistory()) { }

    public MainViewModel(IEngineClient engineClient, IAppDispatcher dispatcher, SearchHistory history)
    {
        SearchBox = new SearchBoxViewModel(history);
        ResultsList = new ResultsListViewModel(engineClient);
        StatusBar = new StatusBarViewModel();

        resultsSubscription = engineClient.ObserveResults
            .Subscribe(ids => dispatcher.Enqueue(() =>
            {
                ResultsList.BindResults(ids);
                StatusBar.RecordCount = ids.Count;

                var isActiveQuery = !string.IsNullOrEmpty(SearchBox.Query);

                if (isActiveQuery)
                {
                    StatusBar.StatusText = ids.Count == 1
                        ? "Found 1 item"
                        : $"Found {ids.Count:N0} items";
                }
                else
                {
                    StatusBar.StatusText = _engineStatus;
                }

                StatusBar.CountSummaryText = ids.Count > ResultsListViewModel.DisplayCap
                    ? $"Showing {ResultsListViewModel.DisplayCap:N0} of {ids.Count:N0}"
                    : $"{ids.Count:N0} results";

                EmptyStateMessage = ids.Count == 0
                    ? (string.IsNullOrEmpty(SearchBox.Query) ? "Type to search..." : $"No results for \"{SearchBox.Query}\"")
                    : string.Empty;
            }));

        statusSubscription = engineClient.StatusChanges
            .Subscribe(text => dispatcher.Enqueue(() =>
            {
                _engineStatus = text;
                // Only update status bar when no active search query
                if (string.IsNullOrEmpty(SearchBox.Query))
                    StatusBar.StatusText = text;
            }));

        metricsSubscription = engineClient.MetricsChanges
            .Subscribe(count => dispatcher.Enqueue(() => StatusBar.RecordCount = count));

        // The throttle collapses bursts of keystrokes (≤75ms apart) into one
        // SearchAsync call. 75ms keeps the result feel under the human
        // "instant" threshold while preventing the engine from being hit on
        // every individual keystroke during fast typing.
        querySubscription = Observable
            .FromEventPattern<System.ComponentModel.PropertyChangedEventHandler, System.ComponentModel.PropertyChangedEventArgs>(
                h => SearchBox.PropertyChanged += h,
                h => SearchBox.PropertyChanged -= h)
            .Where(e => e.EventArgs.PropertyName == nameof(SearchBoxViewModel.Query))
            .Select(_ => SearchBox.Query)
            .DistinctUntilChanged()
            .Throttle(TimeSpan.FromMilliseconds(75))
            .DistinctUntilChanged()
            .SelectMany(async query =>
            {
                // Mirror the live query into the current tab so the title updates.
                if (!_syncingTab && Tabs.CurrentTab is not null)
                    dispatcher.Enqueue(() => { if (Tabs.CurrentTab is not null) Tabs.CurrentTab.Query = query; });

                // When query cleared, revert status bar to last engine status
                if (string.IsNullOrEmpty(query))
                    dispatcher.Enqueue(() => StatusBar.StatusText = _engineStatus);

                await engineClient.SearchAsync(query, default).ConfigureAwait(false);
                return query;
            })
            .Subscribe();

        // When the user picks a different tab, seed the search box with that
        // tab's stored query (which re-triggers the search above).
        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TabsViewModel.CurrentTab)) return;
            var t = Tabs.CurrentTab;
            if (t is null) return;
            _syncingTab = true;
            try { SearchBox.Query = t.Query; }
            finally { _syncingTab = false; }
        };
    }

    public void Dispose()
    {
        querySubscription.Dispose();
        resultsSubscription.Dispose();
        statusSubscription.Dispose();
        metricsSubscription.Dispose();
    }
}
