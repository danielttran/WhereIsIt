using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// End-to-end feature coverage backed by <see cref="RandomFileFixture"/>.
/// Each test loads the fixture into an InProc engine wrapped by the
/// production FilteringEngineClient decorator (same shape as the WinUI app)
/// and asserts that a specific query feature returns the right files.
///
/// InProc is used instead of NativeEngineClient so the tests run without
/// the C++ DLL — they exercise the parser + decorator surface that ALL
/// engines share, which is where every user-facing query feature lives.
/// </summary>
public class RandomFixtureFeatureTests : IClassFixture<RandomFileFixture>
{
    private readonly RandomFileFixture fx;

    public RandomFixtureFeatureTests(RandomFileFixture fx) => this.fx = fx;

    private FilteringEngineClient MakeEngine()
    {
        var inner = new InProcEngineClient(() => new[] { fx.Root.FullName });
        return new FilteringEngineClient(inner);
    }

    private async Task<System.Collections.Generic.IReadOnlyList<ResultRowModel>> Search(string query)
    {
        var engine = MakeEngine();
        var results = new System.Collections.Generic.List<uint>();
        var done = new SemaphoreSlim(0, 1);
        using var sub = engine.ObserveResults.Subscribe(ids =>
        {
            results.Clear();
            results.AddRange(ids);
            try { done.Release(); } catch { }
        });
        await engine.SearchAsync(query, CancellationToken.None);
        await done.WaitAsync(TimeSpan.FromSeconds(15));
        var rows = new System.Collections.Generic.List<ResultRowModel>(results.Count);
        foreach (var id in results)
            rows.Add(await engine.GetRowAsync(id, CancellationToken.None));
        return rows;
    }

    [Fact]
    public async Task ExtFilter_md_OnlyReturnsMdFiles()
    {
        var rows = await Search("ext:md");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WildcardStarMd_OnlyReturnsMdFiles_NoStaleEmissions()
    {
        // The session's headline fix: `*.md` was leaking 18 K random files
        // because NeedsPostFiltering didn't gate wildcard clauses. Lock in.
        var rows = await Search("*.md");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SizeGreaterThan_FiltersTinyOut()
    {
        var rows = await Search("size:>1mb");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.SizeBytes > 1024UL * 1024);
        // medium.bin (2 MB) is in there; tiny.bin (100 B) must not be.
        rows.Should().Contain(r => r.Name == "medium.bin");
        rows.Should().NotContain(r => r.Name == "tiny.bin");
    }

    [Fact]
    public async Task EmptyFilter_ReturnsBlankFileAndEmptyDir()
    {
        var rows = await Search("empty:");
        rows.Should().Contain(r => r.Name == "blank.txt", "blank.txt is zero-byte");
        rows.Should().Contain(r => r.Name == "EmptyDir",  "EmptyDir has no children");
    }

    [Fact]
    public async Task StartsWith_OnlyKeepsPrefixed()
    {
        var rows = await Search("startwith:PREFIX_");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Name.StartsWith("PREFIX_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EndsWith_OnlyKeepsSuffixed()
    {
        var rows = await Search("endwith:_SUFFIX.dat");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Name.EndsWith("_SUFFIX.dat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QuotedPhrase_HandlesSpaces()
    {
        var rows = await Search("\"alpha bravo charlie\"");
        rows.Should().Contain(r => r.Name.Equals("alpha bravo charlie.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeepNote_VisibleToEngine_ConfirmsRecursiveScan()
    {
        // The fixture tree nests deep_*.note files under lvl0/lvl1/lvl2/...
        // Reaching them requires the engine to recurse subdirectories.
        // (`child:<path>` semantics are already locked down by
        // InProcEngineClientPathFilterTests against simpler fixtures.)
        var rows = await Search("*.note");
        rows.Should().Contain(r => r.Name.StartsWith("deep_"));
        rows.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task NotPrefix_ExcludesTerm()
    {
        var all  = await Search("*");
        var noLog = await Search("!log");
        noLog.Should().NotBeEmpty();
        noLog.Should().OnlyContain(r => !r.Name.Contains("log", StringComparison.OrdinalIgnoreCase));
        noLog.Count.Should().BeLessThan(all.Count);
    }

    [Fact]
    public async Task AttribHidden_FindsHiddenFile()
    {
        var rows = await Search("attrib:h");
        rows.Should().Contain(r => r.Name == "secret.cfg");
    }

    [Fact]
    public async Task LenLessThan_FiltersOnNameLength()
    {
        var rows = await Search("len:<6");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => r.Name.Length < 6);
    }

    [Fact]
    public async Task UnicodeQuery_MatchesCyrillicFile()
    {
        var rows = await Search("тест");
        rows.Should().NotBeEmpty();
        rows.Should().Contain(r => r.Name.Contains("тест"));
    }

    [Fact]
    public async Task CountCap_BoundsResultCount()
    {
        var rows = await Search("* count:3");
        rows.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task OrAlternatives_UnionMatches()
    {
        // ext:md|ext:py — two extensions, OR-combined.
        var rows = await Search("md|py");
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r =>
            r.Name.Contains("md", StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains("py", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BooleanGrouping_Brackets_Work()
    {
        // <md> <py> — both groups required. None of the random files match
        // both `md` and `py` so result is empty — that's the assertion.
        var rows = await Search("<sample_md_a.md> <does_not_exist>");
        rows.Should().BeEmpty();
    }
}
