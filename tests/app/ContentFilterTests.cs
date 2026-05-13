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
/// Everything-style content: filter — open each candidate file and keep only
/// those whose contents contain the search string. Implemented as a post-pass
/// in FilteringEngineClient, so we feed it through a fake inner engine that
/// emits IDs and we set up real on-disk files for the GetRowAsync stub to
/// reference.
/// </summary>
public class ContentFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-content-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _root.Delete(recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private sealed class FakeInner : IEngineClient
    {
        public readonly Subject<IReadOnlyList<uint>> ResultsSubject = new();
        public readonly Dictionary<uint, ResultRowModel> Rows = new();

        public IObservable<string> StatusChanges => new Subject<string>();
        public IObservable<int> MetricsChanges => new Subject<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => ResultsSubject;

        public Task SearchAsync(string query, CancellationToken ct) => Task.CompletedTask;
        public Task SortAsync(string key, bool descending, CancellationToken ct) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken ct) => Task.FromResult(Rows[id]);
        public void Emit(params uint[] ids) => ResultsSubject.OnNext(ids);
    }

    private ResultRowModel WriteFile(string name, string content)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, content);
        return new ResultRowModel(name, _root.FullName, (ulong)new FileInfo(path).Length,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "A");
    }

    [Fact]
    public async Task ContentFilter_ReturnsOnlyFilesWithMatchingContent()
    {
        var inner = new FakeInner();
        inner.Rows[1] = WriteFile("alpha.txt", "the report says yes");
        inner.Rows[2] = WriteFile("beta.txt",  "no mention of that word");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("content:report", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(100);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task ContentFilter_IsCaseInsensitiveByDefault()
    {
        var inner = new FakeInner();
        inner.Rows[1] = WriteFile("alpha.txt", "REPORT pending");
        inner.Rows[2] = WriteFile("beta.txt",  "no match here");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("content:report", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(100);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task ContentFilter_HonoursCaseSensitiveWhenCombinedWithCaseModifier()
    {
        var inner = new FakeInner();
        inner.Rows[1] = WriteFile("alpha.txt", "Report has it");
        inner.Rows[2] = WriteFile("beta.txt",  "REPORT shouted");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("case: content:Report", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(100);

        rec!.Should().Equal((uint)1);
    }

    [Fact]
    public async Task ContentFilter_SkipsBinaryAndOversizeFiles()
    {
        var inner = new FakeInner();
        // Oversize file (>4MB) — must be skipped to keep search fast.
        var bigPath = Path.Combine(_root.FullName, "huge.bin");
        File.WriteAllBytes(bigPath, new byte[5 * 1024 * 1024]);
        inner.Rows[1] = new ResultRowModel("huge.bin", _root.FullName, 5UL * 1024 * 1024,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "A");

        inner.Rows[2] = WriteFile("ok.txt", "the report here");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("content:report", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(100);

        rec.Should().NotBeNull();
        rec!.Should().NotContain((uint)1);   // oversize → skipped
        rec.Should().Contain((uint)2);
    }

    [Fact]
    public async Task ContentFilter_DirectoryIsSkipped()
    {
        var inner = new FakeInner();
        inner.Rows[1] = new ResultRowModel("subdir", _root.FullName, 0,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "AD");
        inner.Rows[2] = WriteFile("ok.txt", "the report");

        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);

        await client.SearchAsync("content:report", CancellationToken.None);
        inner.Emit(1, 2);
        await Task.Delay(100);

        rec!.Should().Equal((uint)2);
    }
}
