using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using WhereIsIt.Pipe.Client;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Round-trips a search and a row fetch through the named-pipe server + client
/// (the foundation for the background service). Named pipes work cross-platform
/// in .NET, so this runs in CI without Windows.
/// </summary>
public class EnginePipeServerTests
{
    [Fact]
    public async Task RoundTrip_SearchThenGetRow()
    {
        var dir = Directory.CreateTempSubdirectory("whereisit-pipe-tests-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "alpha.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "notes.txt"), "");

        var pipeName = "WhereIsItTest-" + Guid.NewGuid().ToString("N");
        using var inner = new InProcEngineClient(() => new[] { dir.FullName });
        using var server = new EnginePipeServer(inner, pipeName);
        server.Start();

        try
        {
            using var client = new NamedPipeEngineClient(pipeName, connectTimeoutMs: 5000);
            IReadOnlyList<uint>? ids = null;
            using var sub = client.ObserveResults.Subscribe(x => ids = x);

            await client.SearchAsync("ext:cs", CancellationToken.None);

            ids.Should().NotBeNull();
            ids!.Count.Should().Be(1);

            var row = await client.GetRowAsync(ids[0], CancellationToken.None);
            row.Name.Should().Be("alpha.cs");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }
}
