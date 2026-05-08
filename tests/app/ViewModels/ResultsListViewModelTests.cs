using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class ResultsListViewModelTests
{
    [Fact]
    public void BindResults_ProjectsIds()
    {
        var vm = new ResultsListViewModel(new FakeEngineClient());
        vm.BindResults(new uint[] { 7, 9 });
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void SortKeyChange_CallsSort()
    {
        var fake = new FakeEngineClient();
        var vm = new ResultsListViewModel(fake);
        vm.SortKey = "size";
        Assert.True(fake.SortCalled);
    }

    private sealed class FakeEngineClient : IEngineClient
    {
        public bool SortCalled { get; private set; }
        public IObservable<string> StatusChanges => System.Reactive.Linq.Observable.Empty<string>();
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => System.Reactive.Linq.Observable.Empty<IReadOnlyList<uint>>();
        public Task SearchAsync(string query, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken) { SortCalled = true; return Task.CompletedTask; }
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken) => Task.FromResult(new ResultRowModel("n", "p", 1, DateTimeOffset.UtcNow, "A"));
    }
}
