using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Performance contract: rapid keystrokes (≤75ms apart) must coalesce into a
/// single SearchAsync at the inner engine. Asserts the structural count, not
/// wall-clock — flaky timing assertions don't belong in CI.
/// </summary>
public class PerfThrottleTests
{
    private sealed class CountingInner : IEngineClient
    {
        public int SearchCount;
        public string? LastQuery;
        public readonly Subject<string> StatusChangesSubject = new();
        public readonly Subject<int> MetricsChangesSubject = new();
        public readonly Subject<IReadOnlyList<uint>> ResultsSubject = new();

        public IObservable<string> StatusChanges => StatusChangesSubject;
        public IObservable<int> MetricsChanges => MetricsChangesSubject;
        public IObservable<IReadOnlyList<uint>> ObserveResults => ResultsSubject;

        public Task SearchAsync(string query, CancellationToken ct)
        {
            Interlocked.Increment(ref SearchCount);
            LastQuery = query;
            return Task.CompletedTask;
        }
        public Task SortAsync(string key, bool descending, CancellationToken ct) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken ct) =>
            Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));
    }

    private sealed class InlineDispatcher : IAppDispatcher
    {
        public void Enqueue(Action work) => work();
    }

    [Fact]
    public async Task ThreeRapidKeystrokes_CoalesceInto_OneInnerSearch()
    {
        var inner   = new CountingInner();
        var wrapped = new FilteringEngineClient(inner);
        var vm      = new MainViewModel(wrapped, new InlineDispatcher());

        // 3 keystrokes inside the 75ms throttle window.
        vm.SearchBox.Query = "a";
        await Task.Delay(15);
        vm.SearchBox.Query = "ab";
        await Task.Delay(15);
        vm.SearchBox.Query = "abc";

        // Wait past the throttle window for the single emission to fire.
        await Task.Delay(200);

        inner.SearchCount.Should().Be(1,
            "the throttle must collapse the burst into one inner SearchAsync");
        inner.LastQuery.Should().Contain("abc");
    }

    [Fact]
    public async Task TwoKeystrokes_FarApart_FireTwoSearches()
    {
        var inner   = new CountingInner();
        var wrapped = new FilteringEngineClient(inner);
        var vm      = new MainViewModel(wrapped, new InlineDispatcher());

        vm.SearchBox.Query = "first";
        await Task.Delay(250);   // well past throttle window
        vm.SearchBox.Query = "second";
        await Task.Delay(200);

        inner.SearchCount.Should().Be(2);
    }

    [Fact]
    public void BindResults_ReusesRowViewModels_AcrossCalls()
    {
        var inner = new CountingInner();
        var rl    = new ResultsListViewModel(inner);

        rl.BindResults(new uint[] { 1, 2, 3 });
        var first1 = rl.Rows[0];
        var first2 = rl.Rows[1];

        rl.BindResults(new uint[] { 2, 3, 4 });
        var after2 = rl.Rows[0];
        var after3 = rl.Rows[1];

        after2.Should().BeSameAs(first2, "id 2 should reuse its cached row VM");
        after3.Should().BeSameAs(rl.Rows[1]);
        ReferenceEquals(after2, first1).Should().BeFalse("id 2 is not id 1");
    }
}
