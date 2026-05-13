using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Everything-style boolean keyword tokens: implicit AND between terms,
/// "OR" between two terms acts like the "|" alternative operator,
/// "NOT" before a term negates it (same as the "!" prefix).
/// </summary>
public class BooleanKeywordTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-boolean-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _root.Delete(recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<string[]> NamesAsync(string query)
    {
        using var client = new InProcEngineClient(() => new[] { _root.FullName });
        IReadOnlyList<uint>? rec = null;
        using var _ = client.ObserveResults.Subscribe(ids => rec = ids);
        await client.SearchAsync(query, CancellationToken.None);
        if (rec is null or { Count: 0 }) return [];
        var names = new List<string>();
        foreach (var id in rec)
            names.Add((await client.GetRowAsync(id, CancellationToken.None)).Name);
        return [.. names];
    }

    [Fact]
    public async Task Or_Keyword_MatchesEitherTerm()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "log.txt"),    "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "readme.md"),  "");

        var names = await NamesAsync("report OR log");

        names.Should().Contain("report.txt");
        names.Should().Contain("log.txt");
        names.Should().NotContain("readme.md");
    }

    [Fact]
    public async Task Not_Keyword_NegatesTerm()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "backup_report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "");

        var names = await NamesAsync("ext:txt NOT backup");

        names.Should().Contain("report.txt");
        names.Should().Contain("notes.txt");
        names.Should().NotContain("backup_report.txt");
    }

    [Fact]
    public async Task And_Keyword_IsTreatedAsImplicit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "alpha-beta.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "alpha-gamma.txt"), "");

        var names = await NamesAsync("alpha AND beta");

        names.Should().Contain("alpha-beta.txt");
        names.Should().NotContain("alpha-gamma.txt");
    }

    [Fact]
    public async Task Or_IsCaseInsensitive()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "log.txt"),    "");

        var names = await NamesAsync("report or log");
        names.Should().Contain("report.txt");
        names.Should().Contain("log.txt");
    }
}
