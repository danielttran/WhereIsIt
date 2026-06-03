using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.Pipe.Client;

/// <summary>
/// Hosts an <see cref="IEngineClient"/> behind a named pipe so a non-elevated
/// process (or any local client) can query an engine running in another process
/// — the foundation for the optional background indexing service. Serves one
/// client at a time and loops to accept the next on disconnect.
///
/// Each request runs the inner engine and replies with the resulting record IDs
/// (search/sort) or a single row (getrow); the search wait mirrors the engine's
/// observable result delivery.
/// </summary>
public sealed class EnginePipeServer : IDisposable
{
    private readonly IEngineClient inner;
    private readonly string pipeName;
    private readonly TimeSpan searchTimeout;
    private CancellationTokenSource? cts;
    private Task? loop;

    public EnginePipeServer(IEngineClient inner, string pipeName = PipeProtocol.DefaultPipeName, TimeSpan? searchTimeout = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.pipeName = pipeName;
        this.searchTimeout = searchTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void Start()
    {
        cts = new CancellationTokenSource();
        loop = Task.Run(() => AcceptLoopAsync(cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch { return; }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { server.Dispose(); return; }
            catch { /* drop this connection, accept the next */ }
            finally { try { server.Dispose(); } catch { } }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, System.Text.Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested && server.IsConnected)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch { return; }
            if (line is null) return;
            if (line.Length == 0) continue;

            PipeResponse resp;
            try
            {
                var req = JsonSerializer.Deserialize<PipeRequest>(line)
                          ?? throw new InvalidOperationException("empty request");
                resp = await HandleAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                resp = new PipeResponse(PipeProtocol.Version, "", false, ex.Message);
            }

            try { await writer.WriteLineAsync(JsonSerializer.Serialize(resp)).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private async Task<PipeResponse> HandleAsync(PipeRequest req, CancellationToken ct)
    {
        switch (req.Operation)
        {
            case "search":
            {
                var ids = await RunAndCollectAsync(() => inner.SearchAsync(req.Query ?? "", ct), ct);
                return new PipeResponse(PipeProtocol.Version, req.CorrelationId, true, Ids: ids);
            }
            case "sort":
            {
                var ids = await RunAndCollectAsync(
                    () => inner.SortAsync(req.SortKey ?? "name", req.Descending ?? false, ct), ct);
                return new PipeResponse(PipeProtocol.Version, req.CorrelationId, true, Ids: ids);
            }
            case "getrow":
            {
                var row = await inner.GetRowAsync(req.RowId ?? 0, ct);
                return new PipeResponse(PipeProtocol.Version, req.CorrelationId, true, Row: ToDto(row));
            }
            default:
                return new PipeResponse(PipeProtocol.Version, req.CorrelationId, false, $"unknown op '{req.Operation}'");
        }
    }

    /// <summary>Runs an engine action and captures the next emitted result set.</summary>
    private async Task<uint[]> RunAndCollectAsync(Func<Task> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = inner.ObserveResults.Take(1).Subscribe(ids => tcs.TrySetResult(ids));
        await action().ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(searchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var _ = linked.Token.Register(() => tcs.TrySetResult(Array.Empty<uint>()));
        var result = await tcs.Task.ConfigureAwait(false);
        var arr = new uint[result.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = result[i];
        return arr;
    }

    private static PipeRowDto ToDto(ResultRowModel r) => new(
        r.Name, r.ParentPath, r.SizeBytes,
        r.ModifiedUtc.UtcDateTime.ToString("O"),
        r.Attributes ?? string.Empty,
        r.CreatedUtc == default ? null : r.CreatedUtc.UtcDateTime.ToString("O"),
        r.AccessedUtc == default ? null : r.AccessedUtc.UtcDateTime.ToString("O"));

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
