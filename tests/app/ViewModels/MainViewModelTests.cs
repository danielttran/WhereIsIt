using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class MainViewModelTests
{
    [Fact]
    public async Task QueryChange_TriggersSearchAfterDebounce()
    {
        var fake = new FakeEngineClient();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "alpha";
        await Task.Delay(180);
        Assert.True(fake.SearchCount >= 1);
        vm.Dispose();
    }

    [Fact]
    public void EmptyStateMessage_IsTypeToSearch_Initially()
    {
        var vm = new MainViewModel(new FakeEngineClient(), new InlineDispatcher());
        Assert.Equal("Type to search...", vm.EmptyStateMessage);
        vm.Dispose();
    }

    [Fact]
    public void EmptyStateMessage_ClearsWhenResultsArriveWithItems()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        fake.PushResults(new uint[] { 1, 2, 3 });
        Assert.Equal(string.Empty, vm.EmptyStateMessage);
        vm.Dispose();
    }

    [Fact]
    public void EmptyStateMessage_ShowsNoResults_WhenQueryReturnsEmpty()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "xyz";
        fake.PushResults(Array.Empty<uint>());
        Assert.Contains("xyz", vm.EmptyStateMessage);
        vm.Dispose();
    }

    [Fact]
    public void CountSummaryText_ShowsTotal_WhenUnderCap()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        fake.PushResults(new uint[] { 1, 2, 3 });
        Assert.Contains("3", vm.StatusBar.CountSummaryText);
        vm.Dispose();
    }

    [Fact]
    public void CountSummaryText_ShowsShowingOf_WhenOverCap()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        var ids = new uint[ResultsListViewModel.DisplayCap + 100];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)(i + 1);
        fake.PushResults(ids);
        Assert.StartsWith("Showing", vm.StatusBar.CountSummaryText);
        vm.Dispose();
    }

    private sealed class FakeEngineClient : IEngineClient
    {
        public int SearchCount { get; private set; }
        public IObservable<string> StatusChanges => System.Reactive.Linq.Observable.Empty<string>();
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => System.Reactive.Linq.Observable.Empty<IReadOnlyList<uint>>();
        public Task SearchAsync(string query, CancellationToken cancellationToken) { SearchCount++; return Task.CompletedTask; }
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken) => Task.FromResult(new ResultRowModel("n", "p", 1, DateTimeOffset.UtcNow, "A"));
    }

    private sealed class FakeEngineClientWithResults : IEngineClient
    {
        private readonly Subject<IReadOnlyList<uint>> resultsSubject = new();
        public IObservable<string> StatusChanges => System.Reactive.Linq.Observable.Empty<string>();
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => resultsSubject;
        public Task SearchAsync(string query, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken) => Task.FromResult(new ResultRowModel("n", "p", 1, DateTimeOffset.UtcNow, "A"));
        public void PushResults(IReadOnlyList<uint> ids) => resultsSubject.OnNext(ids);
    }
}
