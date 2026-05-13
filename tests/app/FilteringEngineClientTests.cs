using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Tests for the FilteringEngineClient decorator. Uses a fake inner engine
/// that emits a fixed set of IDs and answers GetRowAsync from a dictionary,
/// so we can verify the post-filtering layer without touching the file system
/// or the native DLL.
/// </summary>
public class FilteringEngineClientTests
{
    private sealed class FakeInner : IEngineClient
    {
        public readonly Subject<string> StatusChangesSubject = new();
        public readonly Subject<int> MetricsChangesSubject = new();
        public readonly Subject<IReadOnlyList<uint>> ResultsSubject = new();
        public readonly Dictionary<uint, ResultRowModel> Rows = new();

        public string? LastQuery { get; private set; }

        public IObservable<string> StatusChanges => StatusChangesSubject;
        public IObservable<int> MetricsChanges => MetricsChangesSubject;
        public IObservable<IReadOnlyList<uint>> ObserveResults => ResultsSubject;

        public Task SearchAsync(string query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.CompletedTask;
        }
        public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
            => Task.FromResult(Rows[id]);

        public void Emit(params uint[] ids) => ResultsSubject.OnNext(ids);
    }

    private static ResultRowModel Row(string name, string parent, ulong size = 0, string attrs = "A")
        => new(name, parent, size, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), attrs);

    [Fact]
    public async Task ExtFilter_OnlyAllowsMatchingExtension()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a.cs",  @"C:\src");
        inner.Rows[2] = Row("b.txt", @"C:\src");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("ext:cs", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec.Should().NotBeNull();
        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task SizeFilter_AppliedAfterEngineReturns()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("big.bin",   @"C:\", size: 5000);
        inner.Rows[2] = Row("small.bin", @"C:\", size:  100);

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("size:>1kb", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task FileOnly_ExcludesDirectories()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("file.txt", @"C:\");
        inner.Rows[2] = Row("dir",      @"C:\", attrs: "AD");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("file:", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task FolderOnly_ExcludesFiles()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("file.txt", @"C:\");
        inner.Rows[2] = Row("dir",      @"C:\", attrs: "AD");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("folder:", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().Equal((uint)2);
    }

    [Fact]
    public async Task NameClause_Substring_PassesThrough_TrustingInnerEngine()
    {
        // A single positive substring clause is a query the inner native engine
        // handles natively; the decorator should pass IDs through unchanged
        // rather than redundantly re-filtering each row via GetRowAsync.
        var inner = new FakeInner();
        inner.Rows[1] = Row("report.txt", @"C:\");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("report", CancellationToken.None);
        inner.Emit(1);
        await Task.Delay(50);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task NegatedClause_ExcludesMatch()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("report.txt",        @"C:\");
        inner.Rows[2] = Row("backup_report.txt", @"C:\");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("report !backup", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task NoFilters_PassesThroughIdsUnchanged()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a.txt", @"C:\");
        inner.Rows[2] = Row("b.txt", @"C:\");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(50);

        rec!.Should().Equal((uint)1, (uint)2);
    }

    [Fact]
    public async Task SearchAsync_StripsUnknownFiltersFromQueryPassedToInner()
    {
        var inner = new FakeInner();
        using var client = new FilteringEngineClient(inner);

        await client.SearchAsync("report ext:cs size:>1kb", CancellationToken.None);

        // The inner engine should receive a query without the filter tokens it
        // can't parse; only the substring "report" should be present.
        inner.LastQuery.Should().NotBeNull();
        inner.LastQuery!.Should().Contain("report");
        inner.LastQuery.Should().NotContain("ext:");
        inner.LastQuery.Should().NotContain("size:");
    }

    [Fact]
    public async Task ConcurrentSearch_DoesNotPublishStaleResults()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("alpha.txt", @"C:\");
        inner.Rows[2] = Row("beta.txt",  @"C:\");

        using var client = new FilteringEngineClient(inner);
        var observed = new List<IReadOnlyList<uint>>();
        using var sub = client.ObserveResults.Subscribe(ids => observed.Add(ids));

        // First search has a filter that *would* publish ID 1 only after a
        // GetRowAsync round-trip. Before that round-trip completes, a second
        // search arrives that should completely replace the first.
        await client.SearchAsync("ext:txt alpha", CancellationToken.None);
        inner.Emit(1, 2);                     // would be filtered by the decorator

        // Immediately start a fresh search (newer seq) before allowing the
        // first emission's post-filter pass to finish publishing.
        await client.SearchAsync("ext:txt beta", CancellationToken.None);
        inner.Emit(2);                        // would be filtered by the decorator

        await Task.Delay(100);

        // The last result observed must correspond to the newer search; the
        // older emission's downstream publish must NOT clobber it.
        observed.Should().NotBeEmpty();
        observed[^1].Should().Contain((uint)2);
        observed[^1].Should().NotContain((uint)1);
    }

    [Fact]
    public async Task SearchAsync_TranslatesModifierTokens()
    {
        var inner = new FakeInner();
        using var client = new FilteringEngineClient(inner);

        // "path:" in our parser → "matchpath:true" for the native engine.
        await client.SearchAsync("path: foo", CancellationToken.None);

        inner.LastQuery.Should().NotBeNull();
        inner.LastQuery!.Should().Contain("matchpath:true");
    }
}
