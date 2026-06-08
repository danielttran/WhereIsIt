using System;
using System.Collections.Generic;
using System.Linq;
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
    public void BindResults_StoresTotalResultCount()
    {
        var vm = new ResultsListViewModel(new FakeEngineClient());
        var ids = Enumerable.Range(1, 50).Select(i => (uint)i).ToArray();
        vm.BindResults(ids);
        Assert.Equal(50, vm.TotalResultCount);
    }

    [Fact]
    public void BindResults_CapsDisplayAtDisplayCap()
    {
        var vm = new ResultsListViewModel(new FakeEngineClient());
        var ids = Enumerable.Range(1, ResultsListViewModel.DisplayCap + 500).Select(i => (uint)i).ToArray();
        vm.BindResults(ids);
        Assert.Equal(ResultsListViewModel.DisplayCap, vm.Rows.Count);
        Assert.Equal(ResultsListViewModel.DisplayCap + 500, vm.TotalResultCount);
    }

    [Fact]
    public void BindResults_PropagatesHighlightTermsToRows()
    {
        var vm = new ResultsListViewModel(new FakeEngineClient());
        vm.BindResults(new uint[] { 1, 2 }, "report\nlog");
        Assert.All(vm.Rows, r => Assert.Equal("report\nlog", r.HighlightTerms));
    }

    [Fact]
    public void BindResults_RefreshesHighlightTermsOnReusedRows()
    {
        var vm = new ResultsListViewModel(new FakeEngineClient());
        vm.BindResults(new uint[] { 1 }, "first");
        var firstRow = vm.Rows[0];
        vm.BindResults(new uint[] { 1 }, "second");
        // Same cached row instance, but terms updated for the new search.
        Assert.Same(firstRow, vm.Rows[0]);
        Assert.Equal("second", vm.Rows[0].HighlightTerms);
    }

    [Fact]
    public void SortKeyChange_CallsSort()
    {
        var fake = new FakeEngineClient();
        var vm = new ResultsListViewModel(fake);
        vm.SortKey = "size";
        Assert.True(fake.SortCalled);
    }

    [Theory]
    [InlineData("name", false, "name", " ▲")]
    [InlineData("name", true, "name", " ▼")]
    [InlineData("size", false, "size", " ▲")]
    [InlineData("size", true, "size", " ▼")]
    [InlineData("name", false, "path", "")]
    [InlineData("name", false, "size", "")]
    [InlineData("path", false, "name", "")]
    [InlineData("created", false, "created", " ▲")]
    [InlineData("accessed", true, "accessed", " ▼")]
    [InlineData("extension", false, "extension", " ▲")]
    [InlineData("attributes", true, "attributes", " ▼")]
    public void SortIndicators_ReflectCurrentSortState(
        string sortKey, bool descending, string indicatorColumn, string expectedIndicator)
    {
        var vm = new ResultsListViewModel(new FakeEngineClient()) { SortDescending = descending };
        vm.SortKey = sortKey;

        var indicator = indicatorColumn switch
        {
            "name" => vm.NameSortIndicator,
            "path" => vm.PathSortIndicator,
            "size" => vm.SizeSortIndicator,
            "modified" => vm.ModifiedSortIndicator,
            "created" => vm.CreatedSortIndicator,
            "accessed" => vm.AccessedSortIndicator,
            "extension" => vm.ExtensionSortIndicator,
            "attributes" => vm.AttributesSortIndicator,
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(expectedIndicator, indicator);
    }

    [Fact]
    public void SortBy_TogglesDescending_WhenSameKey()
    {
        var fake = new FakeEngineClient();
        var vm = new ResultsListViewModel(fake);
        vm.SortByCommand.Execute("name");
        Assert.True(vm.SortDescending);
        vm.SortByCommand.Execute("name");
        Assert.False(vm.SortDescending);
    }

    [Fact]
    public void SortBy_ResetsDescending_WhenDifferentKey()
    {
        var fake = new FakeEngineClient();
        var vm = new ResultsListViewModel(fake) { SortDescending = true };
        vm.SortByCommand.Execute("size");
        Assert.Equal("size", vm.SortKey);
        Assert.False(vm.SortDescending);
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
