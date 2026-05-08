using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

public sealed class InProcEngineClient : IEngineClient, IDisposable
{
    private readonly Subject<string> statusChanges = new();
    private readonly Subject<int> metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>> results = new();

    private FileInfo[] currentResults = [];
    private string sortKey = "name";
    private bool sortDescending;

    public IObservable<string> StatusChanges => statusChanges;
    public IObservable<int> MetricsChanges => metricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => results;

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        statusChanges.OnNext("searching");
        var found = await Task.Run(() => ScanFiles(query, cancellationToken), cancellationToken);
        currentResults = SortResults(found, sortKey, sortDescending);
        var ids = Enumerable.Range(0, currentResults.Length).Select(i => (uint)i).ToList();
        metricsChanges.OnNext(ids.Count);
        results.OnNext(ids);
        statusChanges.OnNext($"Found {ids.Count} results");
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        sortKey = key;
        sortDescending = descending;
        currentResults = SortResults(currentResults, key, descending);
        var ids = Enumerable.Range(0, currentResults.Length).Select(i => (uint)i).ToList();
        results.OnNext(ids);
        return Task.CompletedTask;
    }

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
    {
        if (id >= currentResults.Length)
            return Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));

        var fi = currentResults[id];
        var model = new ResultRowModel(
            fi.Name,
            fi.DirectoryName ?? string.Empty,
            (ulong)(fi.Exists ? fi.Length : 0),
            fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : DateTimeOffset.UtcNow,
            GetAttributes(fi));
        return Task.FromResult(model);
    }

    private static FileInfo[] ScanFiles(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        var bag = new ConcurrentBag<FileInfo>();
        var roots = new[] { @"C:\Users", @"C:\Program Files", @"C:\Program Files (x86)" };

        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, $"*{query}*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 8,
                    MatchCasing = MatchCasing.CaseInsensitive,
                }))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    bag.Add(new FileInfo(file));
                    if (bag.Count >= 2000) break;
                }
            }
            catch (Exception) { }
        }

        return [.. bag];
    }

    private static FileInfo[] SortResults(FileInfo[] files, string key, bool descending)
    {
        IEnumerable<FileInfo> sorted = key switch
        {
            "size" => files.OrderBy(f => { try { return f.Length; } catch { return 0L; } }),
            "modified" => files.OrderBy(f => { try { return f.LastWriteTimeUtc; } catch { return DateTime.MinValue; } }),
            _ => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        if (descending) sorted = sorted.Reverse();
        return sorted.ToArray();
    }

    private static string GetAttributes(FileInfo fi)
    {
        try
        {
            var attr = fi.Attributes;
            return string.Concat(
                attr.HasFlag(FileAttributes.ReadOnly) ? "R" : "",
                attr.HasFlag(FileAttributes.Hidden) ? "H" : "",
                attr.HasFlag(FileAttributes.System) ? "S" : "",
                attr.HasFlag(FileAttributes.Archive) ? "A" : "");
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        statusChanges.Dispose();
        metricsChanges.Dispose();
        results.Dispose();
    }
}
