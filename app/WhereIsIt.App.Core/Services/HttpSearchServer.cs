using System;
using System.Collections.Generic;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

/// <summary>
/// Minimal HTTP frontend so other devices on localhost can query the index.
/// Listens on <c>http://127.0.0.1:{port}/</c> only — never on a network
/// interface — so this stays a same-host tool by design. Returns JSON.
/// </summary>
public sealed class HttpSearchServer : IDisposable
{
    private readonly IEngineClient engine;
    private readonly HttpListener listener = new();
    private readonly int requestedPort;
    private readonly SemaphoreSlim searchLock = new(1, 1);
    private CancellationTokenSource? cts;
    private Task? loopTask;

    public HttpSearchServer(IEngineClient engine, int port = 0)
    {
        this.engine = engine;
        this.requestedPort = port;
    }

    /// <summary>Starts listening; returns the actual TCP port (resolves 0 → OS pick).</summary>
    public int Start()
    {
        int port = requestedPort == 0 ? PickFreePort() : requestedPort;
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        cts = new CancellationTokenSource();
        loopTask = Task.Run(() => LoopAsync(cts.Token));
        return port;
    }

    private static int PickFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException)  { return; }

            // Fire-and-forget per-request so the listener loop keeps going.
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (!ctx.Request.Url!.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.Close();
                return;
            }

            var query = HttpUtility.ParseQueryString(ctx.Request.Url.Query).Get("q") ?? "";
            var result = await RunSearchAsync(query, ct);

            var json = JsonSerializer.Serialize(result);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private async Task<SearchResponse> RunSearchAsync(string query, CancellationToken ct)
    {
        // Serialize concurrent requests — the underlying engine is single-search-
        // at-a-time, so the lock is unavoidable. Bounded by an explicit timeout
        // so a slow earlier request can never starve incoming ones indefinitely.
        if (!await searchLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return new SearchResponse { query = query, count = 0, results = new() };
        try
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<uint>>();
            IDisposable? sub = null;
            sub = engine.ObserveResults.Take(1).Subscribe(ids =>
            {
                tcs.TrySetResult(ids);
                sub?.Dispose();
            });

            await engine.SearchAsync(query, ct);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    var ids = await tcs.Task;
                    var rows = new List<ResultEntry>(Math.Min(ids.Count, 500));
                    int cap = Math.Min(ids.Count, 500);
                    for (int i = 0; i < cap; i++)
                    {
                        try
                        {
                            var row = await engine.GetRowAsync(ids[i], ct);
                            rows.Add(new ResultEntry
                            {
                                name        = row.Name,
                                parentPath  = row.ParentPath,
                                size        = row.SizeBytes,
                                modifiedUtc = row.ModifiedUtc.UtcDateTime.ToString("O"),
                                createdUtc  = row.CreatedUtc  == default ? null : row.CreatedUtc.UtcDateTime.ToString("O"),
                                accessedUtc = row.AccessedUtc == default ? null : row.AccessedUtc.UtcDateTime.ToString("O"),
                                attributes  = row.Attributes,
                            });
                        }
                        catch { }
                    }
                    return new SearchResponse
                    {
                        query   = query,
                        count   = ids.Count,
                        results = rows,
                    };
                }
            }
        }
        finally { searchLock.Release(); }
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { }
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
        try { loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private sealed class SearchResponse
    {
        public string query   { get; set; } = "";
        public int    count   { get; set; }
        public List<ResultEntry> results { get; set; } = new();
    }

    private sealed class ResultEntry
    {
        public string  name        { get; set; } = "";
        public string  parentPath  { get; set; } = "";
        public ulong   size        { get; set; }
        public string  modifiedUtc { get; set; } = "";
        public string? createdUtc  { get; set; }
        public string? accessedUtc { get; set; }
        public string  attributes  { get; set; } = "";
    }
}
