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
/// interface — so this stays a same-host tool by design. Serves an HTML search
/// page at <c>/</c> (like Everything's HTTP server) and JSON at <c>/search</c>.
/// </summary>
public sealed class HttpSearchServer : IDisposable
{
    private readonly IEngineClient engine;
    private readonly HttpListener listener = new();
    private readonly int requestedPort;
    private readonly SemaphoreSlim searchLock = new(1, 1);
    private readonly TimeSpan searchTimeout;
    private CancellationTokenSource? cts;
    private Task? loopTask;

    public HttpSearchServer(IEngineClient engine, int port = 0, TimeSpan? searchTimeout = null)
    {
        this.engine = engine;
        this.requestedPort = port;
        this.searchTimeout = searchTimeout ?? TimeSpan.FromSeconds(10);
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
            var path = ctx.Request.Url!.AbsolutePath;

            // Serve the HTML search page at the root (Everything-style web UI).
            if (path.Length == 0 || path == "/")
            {
                var html = Encoding.UTF8.GetBytes(IndexHtml);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = html.Length;
                await ctx.Response.OutputStream.WriteAsync(html, ct);
                ctx.Response.Close();
                return;
            }

            if (!path.Equals("/search", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/search/", StringComparison.OrdinalIgnoreCase))
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
            var tcs = new TaskCompletionSource<IReadOnlyList<uint>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = engine.ObserveResults.Take(1).Subscribe(ids => tcs.TrySetResult(ids));

            await engine.SearchAsync(query, ct);
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                using var timeout = new CancellationTokenSource(searchTimeout);
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

    // Self-contained search page: an input that queries /search and renders the
    // results. Filenames are inserted with textContent, never innerHTML, so a
    // crafted name can't inject markup.
    internal const string IndexHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>WhereIsIt</title>
          <style>
            body { font: 14px system-ui, sans-serif; margin: 0; padding: 1rem; }
            h1 { font-size: 1.1rem; }
            #q { width: 100%; padding: .5rem; box-sizing: border-box; font-size: 1rem; }
            #status { color: #666; margin: .5rem 0; }
            table { border-collapse: collapse; width: 100%; }
            th, td { text-align: left; padding: .3rem .6rem; border-bottom: 1px solid #ddd; font-size: 13px; }
            td.size { text-align: right; white-space: nowrap; }
            td.path { color: #666; }
          </style>
        </head>
        <body>
          <h1>WhereIsIt</h1>
          <input id="q" type="search" placeholder="Search files and folders…" autofocus>
          <div id="status"></div>
          <table><thead><tr><th>Name</th><th>Path</th><th class="size">Size</th><th>Modified</th></tr></thead>
          <tbody id="rows"></tbody></table>
          <script>
            const q = document.getElementById('q');
            const rows = document.getElementById('rows');
            const status = document.getElementById('status');
            let timer = null;
            function fmt(n) { if (n < 1024) return n + ' B'; const u=['KB','MB','GB','TB']; let i=-1; do { n/=1024; i++; } while (n>=1024 && i<u.length-1); return n.toFixed(1)+' '+u[i]; }
            async function run() {
              const term = q.value.trim();
              rows.replaceChildren();
              if (!term) { status.textContent = ''; return; }
              status.textContent = 'Searching…';
              try {
                const r = await fetch('/search?q=' + encodeURIComponent(term));
                const data = await r.json();
                status.textContent = data.count + ' results' + (data.count > data.results.length ? ' (showing ' + data.results.length + ')' : '');
                for (const it of data.results) {
                  const tr = document.createElement('tr');
                  const c = [it.name, it.parentPath, fmt(it.size), (it.modifiedUtc||'').replace('T',' ').slice(0,16)];
                  c.forEach((v, i) => { const td = document.createElement('td'); if (i===2) td.className='size'; if (i===1) td.className='path'; td.textContent = v; tr.appendChild(td); });
                  rows.appendChild(tr);
                }
              } catch (e) { status.textContent = 'Error: ' + e; }
            }
            q.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(run, 150); });
          </script>
        </body>
        </html>
        """;

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
