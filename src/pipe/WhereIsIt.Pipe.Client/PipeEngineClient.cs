using System;
using System.Collections.Generic;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.Pipe.Client;

public sealed class EngineDisconnectedException : Exception
{
    public EngineDisconnectedException(string message) : base(message) { }
}

public sealed class PipeEngineClient : IEngineClient, IDisposable
{
    private readonly Subject<string> statusChanges = new();
    private readonly Subject<int> metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>> results = new();
    private bool connected = true;

    public IObservable<string> StatusChanges => statusChanges;
    public IObservable<int> MetricsChanges => metricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => results;

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        statusChanges.OnNext("pipe.search.start");
        results.OnNext([1u, 2u]);
        metricsChanges.OnNext(2);
        statusChanges.OnNext("pipe.search.end");
        return Task.CompletedTask;
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        statusChanges.OnNext($"pipe.sort.{key}.{descending}");
        return Task.CompletedTask;
    }

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ResultRowModel($"PipeRow-{id}", "C:\\Pipe", 4096, DateTimeOffset.UtcNow, "A"));
    }

    public void SimulateDisconnect() => connected = false;

    private void EnsureConnected()
    {
        if (!connected)
        {
            throw new EngineDisconnectedException("Pipe service disconnected.");
        }
    }

    public void Dispose()
    {
        statusChanges.Dispose();
        metricsChanges.Dispose();
        results.Dispose();
    }
}
