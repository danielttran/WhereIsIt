using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.Pipe.Client;

/// <summary>
/// Real <see cref="IEngineClient"/> that forwards Search/Sort/GetRow over a named
/// pipe to an <see cref="EnginePipeServer"/> (e.g. the background service). RPC is
/// serialised by a lock so the single duplex pipe carries one request at a time.
/// </summary>
public sealed class NamedPipeEngineClient : IEngineClient, IDisposable
{
    private readonly string pipeName;
    private readonly int connectTimeoutMs;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Subject<string> statusChanges = new();
    private readonly Subject<int> metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>> results = new();

    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public NamedPipeEngineClient(string pipeName = PipeProtocol.DefaultPipeName, int connectTimeoutMs = 2000)
    {
        this.pipeName = pipeName;
        this.connectTimeoutMs = connectTimeoutMs;
    }

    public IObservable<string> StatusChanges => statusChanges;
    public IObservable<int> MetricsChanges => metricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => results;

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        var resp = await RpcAsync(new PipeRequest(PipeProtocol.Version, "search", NewCorr(), Query: query), cancellationToken);
        var ids = resp.Ids ?? Array.Empty<uint>();
        metricsChanges.OnNext(ids.Length);
        results.OnNext(ids);
    }

    public async Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        var resp = await RpcAsync(
            new PipeRequest(PipeProtocol.Version, "sort", NewCorr(), SortKey: key, Descending: descending), cancellationToken);
        results.OnNext(resp.Ids ?? Array.Empty<uint>());
    }

    public async Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
    {
        var resp = await RpcAsync(new PipeRequest(PipeProtocol.Version, "getrow", NewCorr(), RowId: id), cancellationToken);
        var r = resp.Row;
        if (r is null) return new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, "");
        return new ResultRowModel(r.Name, r.ParentPath, r.SizeBytes, ParseTime(r.ModifiedUtc) ?? DateTimeOffset.UtcNow, r.Attributes)
        {
            CreatedUtc = ParseTime(r.CreatedUtc) ?? default,
            AccessedUtc = ParseTime(r.AccessedUtc) ?? default,
        };
    }

    private async Task<PipeResponse> RpcAsync(PipeRequest req, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await writer!.WriteLineAsync(JsonSerializer.Serialize(req)).ConfigureAwait(false);
            var line = await reader!.ReadLineAsync(ct).ConfigureAwait(false)
                       ?? throw new EngineDisconnectedException("Pipe closed by the service.");
            var resp = JsonSerializer.Deserialize<PipeResponse>(line)
                       ?? throw new EngineDisconnectedException("Malformed pipe response.");
            if (!resp.Ok) throw new EngineDisconnectedException(resp.Error ?? "Pipe RPC failed.");
            return resp;
        }
        finally { gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (pipe is { IsConnected: true }) return;
        DisposePipe();
        var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await p.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);
        pipe = p;
        reader = new StreamReader(p, System.Text.Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        writer = new StreamWriter(p, new System.Text.UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };
    }

    private static string NewCorr() => Guid.NewGuid().ToString("N");

    private static DateTimeOffset? ParseTime(string? s)
        => string.IsNullOrEmpty(s)
            ? null
            : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private void DisposePipe()
    {
        try { reader?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        reader = null; writer = null; pipe = null;
    }

    public void Dispose()
    {
        DisposePipe();
        gate.Dispose();
        statusChanges.Dispose();
        metricsChanges.Dispose();
        results.Dispose();
    }
}
