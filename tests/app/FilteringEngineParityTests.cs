using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class FilteringEngineParityTests
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

    private static ResultRowModel Row(string name, string parent = @"C:\folder",
        ulong size = 10, string attr = "A")
        => new(name, parent, size, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), attr);

    private static async Task<IReadOnlyList<uint>> RunAsync(
        FakeInner inner, string query, params uint[] emit)
    {
        using var client = new FilteringEngineClient(inner);
        IReadOnlyList<uint>? rec = null;
        using var sub = client.ObserveResults.Subscribe(ids => rec = ids);
        await client.SearchAsync(query, CancellationToken.None);
        inner.Emit(emit);
        await Task.Delay(60);
        rec.Should().NotBeNull();
        return rec!;
    }

    [Fact]
    public async Task StartWith_FiltersByPrefix()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("report-final.txt");
        inner.Rows[2] = Row("draft-report.txt");
        (await RunAsync(inner, "startwith:report", 1, 2))
            .Should().Equal(1u);
    }

    [Fact]
    public async Task EndWith_FiltersBySuffix()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a.txt");
        inner.Rows[2] = Row("a.txtx");
        (await RunAsync(inner, "endwith:.txt", 1, 2)).Should().Equal(1u);
    }

    [Fact]
    public async Task WholeFilename_IsAnchoredAndWildcardAware()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("notes.cs");
        inner.Rows[2] = Row("notes.cshtml");
        (await RunAsync(inner, "wfn:*.cs", 1, 2)).Should().Equal(1u);
    }

    [Fact]
    public async Task Len_FiltersByNameLength()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("ab.txt");   // length 6
        inner.Rows[2] = Row("abcdefg.txt"); // length 11
        (await RunAsync(inner, "len:>8", 1, 2)).Should().Equal(2u);
    }

    [Fact]
    public async Task Root_KeepsOnlyVolumeRootParents()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a.txt", parent: @"C:\");
        inner.Rows[2] = Row("b.txt", parent: @"C:\sub");
        (await RunAsync(inner, "root:", 1, 2)).Should().Equal(1u);
    }

    [Fact]
    public async Task Count_CapsResults()
    {
        var inner = new FakeInner();
        for (uint i = 1; i <= 5; i++) inner.Rows[i] = Row($"f{i}.txt");
        (await RunAsync(inner, "count:2", 1, 2, 3, 4, 5)).Should().Equal(1u, 2u);
    }

    [Fact]
    public async Task EmptyFile_MatchesZeroBytes_RejectsNonEmpty()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("empty.txt", size: 0);
        inner.Rows[2] = Row("data.txt",  size: 42);
        (await RunAsync(inner, "empty:", 1, 2)).Should().Equal(1u);
    }

    [Fact]
    public async Task EmptyFolder_MatchesChildlessDirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "wii_" + Guid.NewGuid().ToString("N"));
        var emptyDir = Path.Combine(baseDir, "emptydir");
        var fullDir  = Path.Combine(baseDir, "fulldir");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(fullDir);
        File.WriteAllText(Path.Combine(fullDir, "x.txt"), "x");
        try
        {
            var inner = new FakeInner();
            inner.Rows[1] = Row("emptydir", parent: baseDir, size: 0, attr: "D");
            inner.Rows[2] = Row("fulldir",  parent: baseDir, size: 0, attr: "D");
            (await RunAsync(inner, "empty:", 1, 2)).Should().Equal(1u);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public async Task SizeDupe_GroupsBySizeOnly()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a.txt", size: 100);
        inner.Rows[2] = Row("b.bin", size: 100); // same size, different name
        inner.Rows[3] = Row("c.txt", size: 7);
        (await RunAsync(inner, "sizedupe:", 1, 2, 3))
            .Should().BeEquivalentTo(new uint[] { 1, 2 });
    }

    [Fact]
    public async Task NamePartDupe_IgnoresExtension()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("logo.png", size: 1);
        inner.Rows[2] = Row("logo.svg", size: 2);
        inner.Rows[3] = Row("other.png", size: 3);
        (await RunAsync(inner, "namepartdupe:", 1, 2, 3))
            .Should().BeEquivalentTo(new uint[] { 1, 2 });
    }

    [Fact]
    public async Task AttribDupe_GroupsByAttributeSet()
    {
        var inner = new FakeInner();
        inner.Rows[1] = Row("a", attr: "A");
        inner.Rows[2] = Row("b", attr: "A");
        inner.Rows[3] = Row("c", attr: "RH");
        (await RunAsync(inner, "attribdupe:", 1, 2, 3))
            .Should().BeEquivalentTo(new uint[] { 1, 2 });
    }
}
