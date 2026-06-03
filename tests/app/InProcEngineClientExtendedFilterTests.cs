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

    [Fact]
    public async Task Search_ChildCount_ExcludesFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "loose.txt"), "");

        // A file can never satisfy a child-count filter.
        var names = await GetNamesAsync("childcount:0");

        names.Should().NotContain("loose.txt");
    }
}
