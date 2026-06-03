using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

/// <summary>
/// Minimal read-only FTP server (RFC 959 subset) exposing a folder tree, the
/// standard-protocol counterpart to Everything's FTP server. Bound to
/// <c>127.0.0.1</c> only and opt-in — same-host by design, like
/// <see cref="HttpSearchServer"/>. Supports anonymous login, passive mode,
/// directory listing and file download (no upload/delete/rename).
///
/// When an <see cref="IEngineClient"/> is supplied it also speaks Everything's
/// ETP extension (FTP + <c>EVERYTHING …</c> commands): <c>EVERYTHING SEARCH</c>
/// sets the query, <c>EVERYTHING QUERY</c> runs it and streams the matching full
/// paths over the data connection. The command set follows the public ETP
/// description; the exact result-column framing should be confirmed against a
/// real Everything ETP client.
/// </summary>
public sealed class FtpServer : IDisposable
{
    private readonly string root;
    private readonly int requestedPort;
    private readonly IEngineClient? engine;
    private TcpListener? control;
    private CancellationTokenSource? cts;
    private Task? loop;

    public FtpServer(string rootDirectory, int port = 0, IEngineClient? engine = null)
    {
        root = Path.GetFullPath(rootDirectory);
        requestedPort = port;
        this.engine = engine;
    }

    /// <summary>Starts listening; returns the actual control-connection port.</summary>
    public int Start()
    {
        control = new TcpListener(IPAddress.Loopback, requestedPort);
        control.Start();
        var port = ((IPEndPoint)control.LocalEndpoint).Port;
        cts = new CancellationTokenSource();
        loop = Task.Run(() => AcceptLoopAsync(cts.Token));
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await control!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    // Per-connection mutable state (a class so async handlers can mutate it —
    // async methods can't take ref/out parameters).
    private sealed class Session
    {
        public string Cwd = "/";
        public bool Binary = true;
        public TcpListener? Pasv;
        // ETP (Everything extension) query state.
        public string EtpSearch = string.Empty;
        public int EtpOffset;
        public int EtpMaxResults = int.MaxValue;
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var clientScope = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        var s = new Session();
        await writer.WriteLineAsync("220 WhereIsIt FTP ready").ConfigureAwait(false);

        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                var (cmd, arg) = SplitCommand(line);
                switch (cmd)
                {
                    case "USER": await writer.WriteLineAsync("331 Any user accepted").ConfigureAwait(false); break;
                    case "PASS": await writer.WriteLineAsync("230 Logged in").ConfigureAwait(false); break;
                    case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8").ConfigureAwait(false); break;
                    case "FEAT": await writer.WriteLineAsync("211-Features:\r\n PASV\r\n SIZE\r\n211 End").ConfigureAwait(false); break;
                    case "TYPE": s.Binary = !arg.StartsWith('A'); await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
                    case "PWD": await writer.WriteLineAsync($"257 \"{s.Cwd}\"").ConfigureAwait(false); break;
                    case "NOOP": await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
                    case "CWD": s.Cwd = ResolveDir(s.Cwd, arg, out var okCwd);
                        await writer.WriteLineAsync(okCwd ? "250 OK" : "550 No such directory").ConfigureAwait(false); break;
                    case "CDUP": s.Cwd = ResolveDir(s.Cwd, "..", out _); await writer.WriteLineAsync("250 OK").ConfigureAwait(false); break;
                    case "SIZE": await writer.WriteLineAsync(SizeReply(s.Cwd, arg)).ConfigureAwait(false); break;
                    case "PASV": OpenPasv(writer, s); break;
                    case "LIST": await DataTransferAsync(writer, s, Listing(s.Cwd), ct).ConfigureAwait(false); break;
                    case "NLST": await DataTransferAsync(writer, s, Names(s.Cwd), ct).ConfigureAwait(false); break;
                    case "RETR": await RetrAsync(writer, s, s.Cwd, arg, ct).ConfigureAwait(false); break;
                    case "EVERYTHING": await HandleEverythingAsync(writer, s, arg, ct).ConfigureAwait(false); break;
                    case "QUIT": await writer.WriteLineAsync("221 Bye").ConfigureAwait(false); return;
                    default: await writer.WriteLineAsync("502 Not implemented").ConfigureAwait(false); break;
                }
            }
        }
        catch { /* client gone */ }
        finally { try { s.Pasv?.Stop(); } catch { } }
    }

    private static void OpenPasv(StreamWriter writer, Session s)
    {
        try { s.Pasv?.Stop(); } catch { }
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        s.Pasv = l;
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        // 227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)
        writer.WriteLine($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
    }

    private static async Task DataTransferAsync(StreamWriter writer, Session s, string payload, CancellationToken ct)
    {
        if (s.Pasv is null) { await writer.WriteLineAsync("425 Use PASV first").ConfigureAwait(false); return; }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        try
        {
            using var data = await s.Pasv.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var ds = data.GetStream();
            var bytes = Encoding.UTF8.GetBytes(payload);
            await ds.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally { try { s.Pasv?.Stop(); } catch { } s.Pasv = null; }
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    private async Task RetrAsync(StreamWriter writer, Session s, string cwd, string name, CancellationToken ct)
    {
        var full = MapToDisk(cwd, name);
        if (full is null || !File.Exists(full)) { await writer.WriteLineAsync("550 No such file").ConfigureAwait(false); return; }
        if (s.Pasv is null) { await writer.WriteLineAsync("425 Use PASV first").ConfigureAwait(false); return; }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        try
        {
            using var data = await s.Pasv.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var ds = data.GetStream();
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await fs.CopyToAsync(ds, ct).ConfigureAwait(false);
        }
        finally { try { s.Pasv?.Stop(); } catch { } s.Pasv = null; }
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    // ── ETP: Everything's FTP extension commands ─────────────────────────

    private async Task HandleEverythingAsync(StreamWriter writer, Session s, string arg, CancellationToken ct)
    {
        if (engine is null) { await writer.WriteLineAsync("502 ETP not enabled").ConfigureAwait(false); return; }

        int sp = arg.IndexOf(' ');
        string sub = (sp < 0 ? arg : arg[..sp]).ToUpperInvariant();
        string rest = sp < 0 ? string.Empty : arg[(sp + 1)..].Trim();

        switch (sub)
        {
            case "SEARCH": s.EtpSearch = rest; await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
            case "RESULT_OFFSET": s.EtpOffset = ParseInt(rest, 0); await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
            case "MAX_RESULTS": s.EtpMaxResults = ParseInt(rest, int.MaxValue); await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
            // Column/sort directives are accepted (results carry the full path).
            case "SORT" or "SIZE_COLUMN" or "DATE_MODIFIED_COLUMN" or "DATE_CREATED_COLUMN" or "PATH_COLUMN" or "ATTRIBUTES_COLUMN":
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
            case "QUERY":
                await EtpQueryAsync(writer, s, ct).ConfigureAwait(false); break;
            default:
                await writer.WriteLineAsync("502 Not implemented").ConfigureAwait(false); break;
        }
    }

    private async Task EtpQueryAsync(StreamWriter writer, Session s, CancellationToken ct)
    {
        var lines = new StringBuilder();
        try
        {
            var paths = await EtpSearchAsync(s.EtpSearch, s.EtpOffset, s.EtpMaxResults, ct).ConfigureAwait(false);
            foreach (var p in paths) lines.Append(p).Append("\r\n");
        }
        catch { /* return whatever we have */ }
        await DataTransferAsync(writer, s, lines.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<List<string>> EtpSearchAsync(string query, int offset, int max, CancellationToken ct)
    {
        var result = new List<string>();
        if (engine is null) return result;

        var tcs = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = engine.ObserveResults.Take(1).Subscribe(ids => tcs.TrySetResult(ids));
        await engine.SearchAsync(query, ct).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var _ = linked.Token.Register(() => tcs.TrySetResult(Array.Empty<uint>()));
        var ids = await tcs.Task.ConfigureAwait(false);

        int start = Math.Min(offset < 0 ? 0 : offset, ids.Count);
        int end = max == int.MaxValue ? ids.Count : Math.Min(ids.Count, start + Math.Max(0, max));
        for (int i = start; i < end; i++)
        {
            try
            {
                var row = await engine.GetRowAsync(ids[i], ct).ConfigureAwait(false);
                result.Add(string.IsNullOrEmpty(row.ParentPath) ? row.Name : Path.Combine(row.ParentPath, row.Name));
            }
            catch { }
        }
        return result;
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

    private string SizeReply(string cwd, string name)
    {
        var full = MapToDisk(cwd, name);
        try { return full is not null && File.Exists(full) ? $"213 {new FileInfo(full).Length}" : "550 No such file"; }
        catch { return "550 No such file"; }
    }

    // ── virtual-path helpers (sandboxed under root) ──────────────────────

    private string ResolveDir(string cwd, string arg, out bool ok)
    {
        var target = arg == ".." ? ParentVirtual(cwd) : (arg.StartsWith('/') ? arg : CombineVirtual(cwd, arg));
        var disk = MapToDisk(target);
        if (disk is not null && Directory.Exists(disk)) { ok = true; return target; }
        ok = false; return cwd;
    }

    private static string ParentVirtual(string v)
    {
        v = v.TrimEnd('/');
        int i = v.LastIndexOf('/');
        return i <= 0 ? "/" : v[..i];
    }

    private static string CombineVirtual(string cwd, string sub)
        => (cwd.TrimEnd('/') + "/" + sub.Trim('/')).Replace("//", "/");

    /// <summary>Maps a virtual path to a real path, refusing anything that would
    /// escape <see cref="root"/> (directory-traversal guard).</summary>
    private string? MapToDisk(string virtualPath, string? name = null)
    {
        var combined = name is null ? virtualPath : CombineVirtual(virtualPath, name);
        var rel = combined.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(rel.Length == 0 ? root : Path.Combine(root, rel));
        return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    private string Listing(string cwd)
    {
        var dir = MapToDisk(cwd);
        var sb = new StringBuilder();
        if (dir is null || !Directory.Exists(dir)) return sb.ToString();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
                sb.Append(FormatEntry(new DirectoryInfo(d), isDir: true));
            foreach (var f in Directory.EnumerateFiles(dir))
                sb.Append(FormatEntry(new FileInfo(f), isDir: false));
        }
        catch { }
        return sb.ToString();
    }

    private static string FormatEntry(FileSystemInfo info, bool isDir)
    {
        long size = isDir ? 0 : ((FileInfo)info).Length;
        var perm = isDir ? "drwxr-xr-x" : "-rw-r--r--";
        var when = info.LastWriteTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{perm} 1 owner group {size,12} {when} {info.Name}\r\n";
    }

    private string Names(string cwd)
    {
        var dir = MapToDisk(cwd);
        var sb = new StringBuilder();
        if (dir is null || !Directory.Exists(dir)) return sb.ToString();
        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                sb.Append(Path.GetFileName(e)).Append("\r\n");
        }
        catch { }
        return sb.ToString();
    }

    private static (string Cmd, string Arg) SplitCommand(string line)
    {
        int sp = line.IndexOf(' ');
        return sp < 0
            ? (line.ToUpperInvariant(), string.Empty)
            : (line[..sp].ToUpperInvariant(), line[(sp + 1)..].Trim());
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch { }
        try { control?.Stop(); } catch { }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}
