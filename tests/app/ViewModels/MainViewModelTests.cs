using System;
using System.Collections.Generic;
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
}
