using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class ResultRowViewModelTests
{
    [Fact]
    public async Task ToModel_PreservesOptionalDatesAndExtension()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var accessed = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var vm = new ResultRowViewModel(new FakeEngine(created, accessed), 7);

        await vm.EnsureLoadedAsync(CancellationToken.None);
        var model = vm.ToModel();

        Assert.Equal("txt", vm.ExtensionText);
        Assert.Equal(created, model.CreatedUtc);
        Assert.Equal(accessed, model.AccessedUtc);
    }

    [Theory]
    [InlineData((ulong)512, "512 B")]
    [InlineData((ulong)1024, "1 KB")]
    [InlineData((ulong)1536, "1.5 KB")]
    public void FormatBytes_Works(ulong bytes, string expected)
    {
        var actual = ResultRowViewModel.FormatBytes(bytes);
        Assert.Equal(expected, actual);
    }
    private sealed class FakeEngine(DateTimeOffset created, DateTimeOffset accessed) : IEngineClient
    {
        public IObservable<string> StatusChanges => System.Reactive.Linq.Observable.Empty<string>();
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => System.Reactive.Linq.Observable.Empty<IReadOnlyList<uint>>();
        public Task SearchAsync(string query, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
            => Task.FromResult(new ResultRowModel("notes.txt", @"C:\Docs", 1, DateTimeOffset.UtcNow, "A")
            {
                CreatedUtc = created,
                AccessedUtc = accessed,
            });
    }
}
