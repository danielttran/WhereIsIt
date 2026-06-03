using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WhereIsIt.App.Services;

/// <summary>
/// Minimal read-only FTP server (RFC 959 subset) exposing a folder tree, the
/// standard-protocol counterpart to Everything's FTP server. Bound to
/// <c>127.0.0.1</c> only and opt-in — same-host by design, like
/// <see cref="HttpSearchServer"/>. Supports anonymous login, passive mode,
/// directory listing and file download (no upload/delete/rename).
/// </summary>
public sealed class FtpServer : IDisposable
{
    private readonly string root;
    private readonly int requestedPort;
    private TcpListener? control;
    private CancellationTokenSource? cts;
    private Task? loop;

    public FtpServer(string rootDirectory, int port = 0)
    {
        root = Path.GetFullPath(rootDirectory);
        requestedPort = port;
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
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
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
