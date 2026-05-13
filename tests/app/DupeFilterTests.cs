using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class DupeFilterTests
{
    private sealed class FakeInner : IEngineClient
    {
        public readonly Subject<IReadOnlyList<uint>> ResultsSubject = new();
        public readonly Dictionary<uint, ResultRowModel> Rows = new();

        public IObservable<string> StatusChanges  => new Subject<string>();
        public IObservable<int>    MetricsChanges => new Subject<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => ResultsSubject;

        public Task SearchAsync(string query, CancellationToken ct) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken ct) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken ct) => Task.FromResult(Rows[id]);

        public void Emit(params uint[] ids) => ResultsSubject.OnNext(ids);
    }

    private static ResultRowModel Row(string name, ulong size)
        => new(name, @"C:\folder", size, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "A");

    [Fact]
    public async Task Dupe_ReturnsOnlyFilesWithDuplicateNameAndSize()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("alpha.txt", 100);
        inner.Rows[2] = Row("alpha.txt", 100); // duplicate of 1
        inner.Rows[3] = Row("unique.txt", 50);
        inner.Rows[4] = Row("alpha.txt", 999); // same name, different size — not a dupe of 1/2

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("dupe:", CancellationToken.None);
        inner.Emit(1, 2, 3, 4);
        await Task.Delay(50);

        rec.Should().NotBeNull();
        rec!.Should().BeEquivalentTo(new uint[] { 1, 2 });
    }

    [Fact]
    public async Task Dupe_IsCaseInsensitiveOnName()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("Foo.txt", 10);
        inner.Rows[2] = Row("FOO.TXT", 10);

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("dupe:", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().BeEquivalentTo(new uint[] { 1, 2 });
    }

    [Fact]
    public async Task Dupe_ComposesWithExtFilter()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("dupe.cs",  10);
        inner.Rows[2] = Row("dupe.cs",  10);
        inner.Rows[3] = Row("dupe.txt", 10);
        inner.Rows[4] = Row("dupe.txt", 10);

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("dupe: ext:cs", CancellationToken.None);
        inner.Emit(1, 2, 3, 4);
        await Task.Delay(50);

        rec!.Should().BeEquivalentTo(new uint[] { 1, 2 });
    }
}
