using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WhereIsIt.App.Contracts;

public interface IEngineClient
{
    IObservable<string> StatusChanges { get; }
    IObservable<int> MetricsChanges { get; }
    IObservable<IReadOnlyList<uint>> ObserveResults { get; }

    Task SearchAsync(string query, CancellationToken cancellationToken);
    Task SortAsync(string key, bool descending, CancellationToken cancellationToken);
    Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken);
}

public sealed record ResultRowModel(
    string Name,
    string ParentPath,
    ulong SizeBytes,
    DateTimeOffset ModifiedUtc,
    string Attributes)
{
    /// <summary>File creation time, when known. <c>default</c> when the engine
    /// did not provide it (legacy native rows).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Last access time, when known. <c>default</c> when the engine
    /// did not provide it.</summary>
    public DateTimeOffset AccessedUtc { get; init; }
}
