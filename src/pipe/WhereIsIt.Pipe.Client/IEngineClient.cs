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

public sealed record ResultRowModel(string Name, string ParentPath, ulong SizeBytes, DateTimeOffset ModifiedUtc, string Attributes);
