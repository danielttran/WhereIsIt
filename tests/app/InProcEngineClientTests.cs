using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class InProcEngineClientTests
{
    [Fact]
    public async Task SearchAsync_EmptyQuery_PushesNoResults()
    {
        using var client = new InProcEngineClient();
        IReadOnlyList<uint>? received = null;
        using var _ = client.ObserveResults.Subscribe(ids => received = ids);

        await client.SearchAsync("", CancellationToken.None);

        received.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task GetRowAsync_AfterSearch_ReturnsModel()
    {
        using var client = new InProcEngineClient();
        IReadOnlyList<uint>? received = null;
        using var _ = client.ObserveResults.Subscribe(ids => received = ids);

        await client.SearchAsync("txt", CancellationToken.None);

        if (received is { Count: > 0 })
        {
            var row = await client.GetRowAsync(received[0], CancellationToken.None);
            row.Name.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task SearchAsync_Cancellation_ThrowsOperationCanceled()
    {
        using var client = new InProcEngineClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await client.SearchAsync("exe", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchAsync_QueryMatchesFileInConfiguredRoot_ReturnsResult()
    {
        var tempRoot = Directory.CreateTempSubdirectory("whereisit-tests-");
        try
        {
            var filePath = Path.Combine(tempRoot.FullName, "needle-file-123.txt");
            await File.WriteAllTextAsync(filePath, "x");

            using var client = new InProcEngineClient(() => new[] { tempRoot.FullName });
            IReadOnlyList<uint>? received = null;
            using var _ = client.ObserveResults.Subscribe(ids => received = ids);

            await client.SearchAsync("needle", CancellationToken.None);

            received.Should().NotBeNull();
            received!.Count.Should().Be(1);
            var row = await client.GetRowAsync(received[0], CancellationToken.None);
            row.Name.Should().Be("needle-file-123.txt");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_WildcardSearch_MatchesFileNamePattern()
    {
        var tempRoot = Directory.CreateTempSubdirectory("whereisit-tests-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot.FullName, "report-2026.log"), "x");
            await File.WriteAllTextAsync(Path.Combine(tempRoot.FullName, "report-2026.txt"), "x");

            using var client = new InProcEngineClient(() => new[] { tempRoot.FullName });
            IReadOnlyList<uint>? received = null;
            using var _ = client.ObserveResults.Subscribe(ids => received = ids);

            await client.SearchAsync("report-*.log", CancellationToken.None);

            received.Should().NotBeNull();
            received!.Count.Should().Be(1);
            var row = await client.GetRowAsync(received[0], CancellationToken.None);
            row.Name.Should().Be("report-2026.log");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_WildcardPathFragment_MatchesNestedPath()
    {
        var tempRoot = Directory.CreateTempSubdirectory("whereisit-tests-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "logs", "daily"));
            await File.WriteAllTextAsync(Path.Combine(nested.FullName, "app.log"), "x");

            using var client = new InProcEngineClient(() => new[] { tempRoot.FullName });
            IReadOnlyList<uint>? received = null;
            using var _ = client.ObserveResults.Subscribe(ids => received = ids);

            await client.SearchAsync("logs/*/app.*", CancellationToken.None);

            received.Should().NotBeNull();
            received!.Count.Should().Be(1);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_RegexMode_MatchesByExpression()
    {
        var tempRoot = Directory.CreateTempSubdirectory("whereisit-tests-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot.FullName, "alpha-100.log"), "x");
            await File.WriteAllTextAsync(Path.Combine(tempRoot.FullName, "beta-xyz.log"), "x");

            using var client = new InProcEngineClient(() => new[] { tempRoot.FullName });
            IReadOnlyList<uint>? received = null;
            using var _ = client.ObserveResults.Subscribe(ids => received = ids);

            await client.SearchAsync("regex:true ^alpha-[0-9]+\\.log$", CancellationToken.None);

            received.Should().NotBeNull();
            received!.Count.Should().Be(1);
            var row = await client.GetRowAsync(received[0], CancellationToken.None);
            row.Name.Should().Be("alpha-100.log");
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
