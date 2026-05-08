using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.Pipe.Client;
using Xunit;

namespace WhereIsIt.App.Tests;

public class PipeEngineClientTests
{
    [Fact]
    public async Task SimulateDisconnect_ThrowsOnSubsequentSearch()
    {
        using var client = new PipeEngineClient();
        client.SimulateDisconnect();
        var act = async () => await client.SearchAsync("q", CancellationToken.None);
        await act.Should().ThrowAsync<EngineDisconnectedException>();
    }

    [Fact]
    public async Task SearchAsync_PushesResultsToObserver()
    {
        using var client = new PipeEngineClient();
        int? receivedCount = null;
        using var _ = client.ObserveResults.Subscribe(ids => receivedCount = ids.Count);

        await client.SearchAsync("q", CancellationToken.None);

        receivedCount.Should().BeGreaterThan(0);
    }
}
