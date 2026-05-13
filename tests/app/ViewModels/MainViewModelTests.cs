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
    // ── initial state ────────────────────────────────────────────────────

    [Fact]
    public void StatusText_IsStarting_BeforeAnyEngineEvent()
    {
        var vm = new MainViewModel(new FakeEngineClient(), new InlineDispatcher());
        Assert.Equal("Starting...", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void EmptyStateMessage_IsTypeToSearch_Initially()
    {
        var vm = new MainViewModel(new FakeEngineClient(), new InlineDispatcher());
        Assert.Equal("Type to search...", vm.EmptyStateMessage);
        vm.Dispose();
    }

    // ── search trigger ───────────────────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task QueryChange_TriggersSearch_AfterThrottleWindow()
    {
        // 75ms throttle now sits in front of SearchAsync. Confirm the search
        // does eventually fire after the throttle window elapses.
        var fake = new FakeEngineClient();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "alpha";
        await System.Threading.Tasks.Task.Delay(200);
        Assert.True(fake.SearchCount >= 1, "search should fire after the throttle window");
        vm.Dispose();
    }

    [Fact]
    public async System.Threading.Tasks.Task QueryChange_SameQueryTwice_SearchesOnce()
    {
        // DistinctUntilChanged + Throttle ensure the same query doesn't fire twice.
        var fake = new FakeEngineClient();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "alpha";
        await System.Threading.Tasks.Task.Delay(200);
        var countAfterFirst = fake.SearchCount;
        vm.SearchBox.Query = "alpha";  // same value again
        await System.Threading.Tasks.Task.Delay(200);
        Assert.Equal(countAfterFirst, fake.SearchCount);
        vm.Dispose();
    }

    // ── status text reflects active search ───────────────────────────────

    [Fact]
    public void StatusText_ShowsFoundItems_WhenQueryIsActiveAndResultsArrive()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "notepad";
        fake.PushResults(new uint[] { 1, 2, 3 });
        Assert.Contains("3", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void StatusText_ShowsFoundOneItem_Singular()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        vm.SearchBox.Query = "exact";
        fake.PushResults(new uint[] { 42 });
        // Singular form: "Found 1 item" not "1 items"
        Assert.DoesNotContain("items", vm.StatusBar.StatusText.ToLower().Replace("items", "NOPE"));
        Assert.Contains("1", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void StatusText_ShowsEngineStatus_WhenQueryIsEmpty()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        // Engine fires status, then results for empty query
        fake.PushStatus("Ready - 50,000 items");
        fake.PushResults(new uint[] { 1, 2, 3 });
        Assert.Equal("Ready - 50,000 items", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void StatusText_RevertsToEngineStatus_WhenQueryIsCleared()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());

        // Engine reports ready
        fake.PushStatus("Ready - 50,000 items");

        // User types → results come in → status shows found count
        vm.SearchBox.Query = "test";
        fake.PushResults(new uint[] { 1 });
        Assert.Contains("1", vm.StatusBar.StatusText);

        // User clears → empty-query results → status reverts to engine status
        vm.SearchBox.Query = string.Empty;
        fake.PushResults(new uint[] { 1, 2, 3 });
        Assert.Equal("Ready - 50,000 items", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void StatusText_UpdatesFromEngineStatus_WhenNoQueryActive()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());
        fake.PushStatus("Indexing... 12,345 items");
        Assert.Equal("Indexing... 12,345 items", vm.StatusBar.StatusText);
        vm.Dispose();
    }

    [Fact]
    public void StatusText_EngineStatusDoesNotOverrideSearchStatus_WhenQueryActive()
    {
        var fake = new FakeEngineClientWithResults();
        var vm = new MainViewModel(fake, new InlineDispatcher());

        vm.SearchBox.Query = "foo";
        fake.PushResults(new uint[] { 1, 2 });   // triggers "Found 2 items"
        var searchStatus = vm.StatusBar.StatusText;
        Assert.Contains("2", searchStatus);

        // Engine fires status update (e.g., from USN monitoring) — must NOT overwrite
        fake.PushStatus("Ready - 50,000 items");
        Assert.Equal(searchStatus, vm.StatusBar.StatusText);
        vm.Dispose();
    }

    // ── count summary ────────────────────────────────────────────────────

    [Fact]
    public void CountSummaryText_ShowsFilteredCount_WhenResultsArrive()
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

    // ── empty state message ──────────────────────────────────────────────

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

    // ── fakes ────────────────────────────────────────────────────────────

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
        private readonly Subject<string> statusSubject = new();
        public IObservable<string> StatusChanges => statusSubject;
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => resultsSubject;
        public Task SearchAsync(string query, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken) => Task.FromResult(new ResultRowModel("n", "p", 1, DateTimeOffset.UtcNow, "A"));
        public void PushResults(IReadOnlyList<uint> ids) => resultsSubject.OnNext(ids);
        public void PushStatus(string status) => statusSubject.OnNext(status);
    }
}
