using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class InProcEngineClientSortTests
{
    private static async Task<List<string>> OrderedNamesAsync(
        InProcEngineClient client, IReadOnlyList<uint> ids)
    {
        var names = new List<string>();
        foreach (var id in ids)
            names.Add((await client.GetRowAsync(id, CancellationToken.None)).Name);
        return names;
    }

    [Fact]
    public async Task Sort_ByExtension_OrdersAlphabeticallyByExt()
    {
        var root = Directory.CreateTempSubdirectory("whereisit-sort-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "alpha.txt"), "x");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "bravo.log"), "x");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "charlie.md"), "x");

            using var client = new InProcEngineClient(() => new[] { root.FullName });
            IReadOnlyList<uint>? ids = null;
            using var _ = client.ObserveResults.Subscribe(x => ids = x);

            await client.SearchAsync("*", CancellationToken.None);
            await client.SortAsync("extension", descending: false, CancellationToken.None);

            ids.Should().NotBeNull();
            (await OrderedNamesAsync(client, ids!))
                .Should().Equal("bravo.log", "charlie.md", "alpha.txt");
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Sort_ByCreated_OrdersByCreationTime()
    {
        var root = Directory.CreateTempSubdirectory("whereisit-sort-");
        try
        {
            var older = Path.Combine(root.FullName, "older.dat");
            var newer = Path.Combine(root.FullName, "newer.dat");
            await File.WriteAllTextAsync(older, "x");
            await File.WriteAllTextAsync(newer, "x");
            File.SetCreationTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetCreationTimeUtc(newer, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            using var client = new InProcEngineClient(() => new[] { root.FullName });
            IReadOnlyList<uint>? ids = null;
            using var _ = client.ObserveResults.Subscribe(x => ids = x);

            await client.SearchAsync("*", CancellationToken.None);
            await client.SortAsync("created", descending: false, CancellationToken.None);

            (await OrderedNamesAsync(client, ids!))
                .Should().Equal("older.dat", "newer.dat");

            await client.SortAsync("created", descending: true, CancellationToken.None);
            (await OrderedNamesAsync(client, ids!))
                .Should().Equal("newer.dat", "older.dat");
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task Sort_ByAccessed_OrdersByAccessTime()
    {
        var root = Directory.CreateTempSubdirectory("whereisit-sort-");
        try
        {
            var a = Path.Combine(root.FullName, "a.bin");
            var b = Path.Combine(root.FullName, "b.bin");
            await File.WriteAllTextAsync(a, "x");
            await File.WriteAllTextAsync(b, "x");
            File.SetLastAccessTimeUtc(a, new DateTime(2021, 3, 3, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastAccessTimeUtc(b, new DateTime(2019, 3, 3, 0, 0, 0, DateTimeKind.Utc));

            using var client = new InProcEngineClient(() => new[] { root.FullName });
            IReadOnlyList<uint>? ids = null;
            using var _ = client.ObserveResults.Subscribe(x => ids = x);

            await client.SearchAsync("*", CancellationToken.None);
            await client.SortAsync("accessed", descending: false, CancellationToken.None);

            (await OrderedNamesAsync(client, ids!))
                .Should().Equal("b.bin", "a.bin");
        }
        finally { root.Delete(recursive: true); }
    }
}
