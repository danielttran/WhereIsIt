using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// End-to-end coverage (through InProcEngineClient's real ParsedQuery matching)
/// for the parity functions added in the audit: nowildcards:, nodiacritics:,
/// and the childcount: family. Each test uses an isolated temp directory.
/// </summary>
public class InProcEngineClientExtendedFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-ext-filter-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _root.Delete(recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<string[]> GetNamesAsync(string query)
    {
        using var client = new InProcEngineClient(() => new[] { _root.FullName });
        IReadOnlyList<uint>? received = null;
        using var _ = client.ObserveResults.Subscribe(ids => received = ids);
        await client.SearchAsync(query, CancellationToken.None);
        if (received is null or { Count: 0 }) return [];
        var names = new List<string>();
        foreach (var id in received)
            names.Add((await client.GetRowAsync(id, CancellationToken.None)).Name);
        return [.. names];
    }

    // ── nowildcards: ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_WildcardsDefault_TreatsStarAsWildcard()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");

        var names = await GetNamesAsync("rep*");

        names.Should().Contain("report.txt");
    }

    [Fact]
    public async Task Search_NoWildcards_TreatsStarLiterally()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");

        // A literal "rep*" never appears in a real filename, so nothing matches.
        var names = await GetNamesAsync("nowildcards: rep*");

        names.Should().BeEmpty();
    }

    // ── nodiacritics: ─────────────────────────────────────────────────────

    [Fact]
    public async Task Search_DiacriticSensitiveByDefault_DoesNotFold()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "café.txt"), "");

        var names = await GetNamesAsync("cafe");

        names.Should().NotContain("café.txt");
    }

    [Fact]
    public async Task Search_NoDiacritics_FoldsAccents()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "café.txt"), "");

        var names = await GetNamesAsync("nodiacritics: cafe");

        names.Should().Contain("café.txt");
    }

    // ── childcount: family ────────────────────────────────────────────────

    [Fact]
    public async Task Search_ChildCountZero_ReturnsEmptyFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "empty_dir"));
        var full = Directory.CreateDirectory(Path.Combine(_root.FullName, "full_dir"));
        await File.WriteAllTextAsync(Path.Combine(full.FullName, "a.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(full.FullName, "b.txt"), "");

        var names = await GetNamesAsync("childcount:0");

        names.Should().Contain("empty_dir");
        names.Should().NotContain("full_dir");
    }

    [Fact]
    public async Task Search_ChildFileCount_CountsFilesOnly()
    {
        var mixed = Directory.CreateDirectory(Path.Combine(_root.FullName, "mixed"));
        await File.WriteAllTextAsync(Path.Combine(mixed.FullName, "only.txt"), "");
        Directory.CreateDirectory(Path.Combine(mixed.FullName, "sub"));

        var names = await GetNamesAsync("childfilecount:1");

        names.Should().Contain("mixed");
        // "sub" has zero files, so it must not match.
        names.Should().NotContain("sub");
    }

    [Fact]
    public async Task Search_ChildFolderCount_CountsFoldersOnly()
    {
        var mixed = Directory.CreateDirectory(Path.Combine(_root.FullName, "mixed2"));
        await File.WriteAllTextAsync(Path.Combine(mixed.FullName, "only.txt"), "");
        Directory.CreateDirectory(Path.Combine(mixed.FullName, "sub"));

        var names = await GetNamesAsync("childfoldercount:1");

        names.Should().Contain("mixed2");
    }

    // ── depth: ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_Depth_FiltersByFolderDepth()
    {
        // _root itself sits at some absolute depth D; a file directly in _root is
        // at D+1, and a file one folder deeper is at D+2. Assert the deeper file
        // has strictly greater depth via a relative comparison.
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "shallow.txt"), "");
        var sub = Directory.CreateDirectory(Path.Combine(_root.FullName, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sub.FullName, "deep.txt"), "");

        int rootDepth = QueryParser.FolderDepth(_root.FullName);
        // Files directly under _root are at rootDepth+1.
        var atRootPlusOne = await GetNamesAsync($"depth:{rootDepth + 1} file:");
        atRootPlusOne.Should().Contain("shallow.txt");
        atRootPlusOne.Should().NotContain("deep.txt");

        var deeper = await GetNamesAsync($"depth:{rootDepth + 2} file:");
        deeper.Should().Contain("deep.txt");
        deeper.Should().NotContain("shallow.txt");
    }

    // ── image dimensions (width:/height:) ─────────────────────────────────

    [Fact]
    public async Task Search_Width_MatchesImageDimensions()
    {
        // Minimal PNG header describing a 2x3 image (enough for the reader).
        byte[] png =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03,
        };
        await File.WriteAllBytesAsync(Path.Combine(_root.FullName, "img.png"), png);
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "not an image");

        (await GetNamesAsync("width:2")).Should().Contain("img.png");
        (await GetNamesAsync("width:2")).Should().NotContain("notes.txt");
        (await GetNamesAsync("height:3")).Should().Contain("img.png");
        (await GetNamesAsync("width:99")).Should().NotContain("img.png");
    }

    // ── < > boolean grouping ──────────────────────────────────────────────

    [Fact]
    public async Task Search_GroupedOrOfAnds_MatchesEitherGroup()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report_log.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "temp_data.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report_only.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "other.txt"), "");

        // (report AND log) OR (temp AND data)
        var names = await GetNamesAsync("<report log>|<temp data>");

        names.Should().BeEquivalentTo("report_log.txt", "temp_data.txt");
    }

    [Fact]
    public async Task Search_GroupWithGlobalFunction_AppliesBoth()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "alpha.cs"),  "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "beta.cs"),   "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "alpha.txt"), "");

        // ext:cs applies globally; <alpha|beta> is the grouped term.
        var names = await GetNamesAsync("ext:cs <alpha|beta>");

        names.Should().BeEquivalentTo("alpha.cs", "beta.cs");
    }

    [Fact]
    public async Task Search_ChildCount_ExcludesFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "loose.txt"), "");

        // A file can never satisfy a child-count filter.
        var names = await GetNamesAsync("childcount:0");

        names.Should().NotContain("loose.txt");
    }
}
