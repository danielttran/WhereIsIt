using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Per-query path scoping with `child:` (recursive: anywhere under the path)
/// and `parent:` (strict: only files whose immediate parent IS the path).
/// </summary>
public class InProcEngineClientPathFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-pathfilter-");
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
    public async Task Child_MatchesAllUnderSpecifiedDir()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_root.FullName, "inner")).FullName;
        var deeper = Directory.CreateDirectory(Path.Combine(inner, "deeper")).FullName;
        var outer = _root.FullName;

        await File.WriteAllTextAsync(Path.Combine(outer,  "outer.txt"),  "");
        await File.WriteAllTextAsync(Path.Combine(inner,  "inner.txt"),  "");
        await File.WriteAllTextAsync(Path.Combine(deeper, "deeper.txt"), "");

        var names = await NamesAsync($"child:\"{inner}\"");

        names.Should().Contain("inner.txt");
        names.Should().Contain("deeper.txt");
        names.Should().NotContain("outer.txt");
    }

    [Fact]
    public async Task Parent_MatchesOnlyImmediateParent()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_root.FullName, "inner")).FullName;
        var deeper = Directory.CreateDirectory(Path.Combine(inner, "deeper")).FullName;

        await File.WriteAllTextAsync(Path.Combine(inner,  "in-direct.txt"),  "");
        await File.WriteAllTextAsync(Path.Combine(deeper, "way-down.txt"),   "");

        var names = await NamesAsync($"parent:\"{inner}\"");

        names.Should().Contain("in-direct.txt");
        names.Should().NotContain("way-down.txt");
    }

    [Fact]
    public async Task Child_CombinesWithExtFilter()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root.FullName, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub,             "a.cs"),  "");
        await File.WriteAllTextAsync(Path.Combine(sub,             "a.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName,  "b.cs"),  "");

        var names = await NamesAsync($"child:\"{sub}\" ext:cs");

        names.Should().Contain("a.cs");
        names.Should().NotContain("a.txt");
        names.Should().NotContain("b.cs");
    }
}
