using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Tests for the Everything-style attribute filter `attrib:` in InProcEngineClient.
/// Letters: r = ReadOnly, h = Hidden, s = System, a = Archive, d = Directory.
/// A leading `-` requires the listed attributes to be ABSENT.
/// </summary>
public class InProcEngineClientAttribFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-attrib-filter-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            foreach (var f in _root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { f.Attributes = FileAttributes.Normal; } catch { }
            }
            _root.Delete(recursive: true);
        }
        catch { }
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
        {
            var row = await client.GetRowAsync(id, CancellationToken.None);
            names.Add(row.Name);
        }
        return [.. names];
    }

    [Fact]
    public async Task Search_AttribHidden_ReturnsOnlyHiddenFiles()
    {
        var hidden = Path.Combine(_root.FullName, "secret.txt");
        var visible = Path.Combine(_root.FullName, "open.txt");
        await File.WriteAllTextAsync(hidden, "");
        await File.WriteAllTextAsync(visible, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var names = await GetNamesAsync("attrib:h");

        names.Should().Contain("secret.txt");
        names.Should().NotContain("open.txt");
    }

    [Fact]
    public async Task Search_AttribReadOnly_ReturnsOnlyReadOnlyFiles()
    {
        var ro = Path.Combine(_root.FullName, "locked.txt");
        var rw = Path.Combine(_root.FullName, "writable.txt");
        await File.WriteAllTextAsync(ro, "");
        await File.WriteAllTextAsync(rw, "");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var names = await GetNamesAsync("attrib:r");

        names.Should().Contain("locked.txt");
        names.Should().NotContain("writable.txt");
    }

    [Fact]
    public async Task Search_AttribDirectory_ReturnsOnlyDirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "file.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "subdir"));

        var names = await GetNamesAsync("attrib:d");

        names.Should().Contain("subdir");
        names.Should().NotContain("file.txt");
    }

    [Fact]
    public async Task Search_AttribNegated_ExcludesMatchingAttribute()
    {
        var hidden = Path.Combine(_root.FullName, "secret.txt");
        var visible = Path.Combine(_root.FullName, "open.txt");
        await File.WriteAllTextAsync(hidden, "");
        await File.WriteAllTextAsync(visible, "");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var names = await GetNamesAsync("attrib:-h");

        names.Should().Contain("open.txt");
        names.Should().NotContain("secret.txt");
    }

    [Fact]
    public async Task Search_AttribMultiple_RequiresAllListedAttributes()
    {
        var roHidden = Path.Combine(_root.FullName, "rohidden.txt");
        var roOnly   = Path.Combine(_root.FullName, "roOnly.txt");
        var hOnly    = Path.Combine(_root.FullName, "hOnly.txt");
        await File.WriteAllTextAsync(roHidden, "");
        await File.WriteAllTextAsync(roOnly, "");
        await File.WriteAllTextAsync(hOnly, "");
        File.SetAttributes(roHidden, FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.SetAttributes(roOnly,   FileAttributes.ReadOnly);
        File.SetAttributes(hOnly,    FileAttributes.Hidden);

        var names = await GetNamesAsync("attrib:rh");

        names.Should().Contain("rohidden.txt");
        names.Should().NotContain("roOnly.txt");
        names.Should().NotContain("hOnly.txt");
    }
}
