using System.Collections.Generic;
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
}
