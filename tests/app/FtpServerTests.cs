using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class FtpServerTests
{
    private static int ParsePasvPort(string line)
    {
        int o = line.IndexOf('(');
        int c = line.IndexOf(')');
        var parts = line.Substring(o + 1, c - o - 1).Split(',');
        return int.Parse(parts[4]) * 256 + int.Parse(parts[5]);
    }

    [Fact]
    public async Task Ftp_ListsAndRetrievesFiles()
    {
        var dir = Directory.CreateTempSubdirectory("whereisit-ftp-tests-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "hello.txt"), "world");

        using var server = new FtpServer(dir.FullName);
        var port = server.Start();

        try
        {
            using var ctrl = new TcpClient();
            await ctrl.ConnectAsync(IPAddress.Loopback, port);
            using var cs = ctrl.GetStream();
            using var r = new StreamReader(cs);
            using var w = new StreamWriter(cs) { AutoFlush = true, NewLine = "\r\n" };

            (await r.ReadLineAsync())!.Should().StartWith("220");
            await w.WriteLineAsync("USER anonymous"); (await r.ReadLineAsync())!.Should().StartWith("331");
            await w.WriteLineAsync("PASS x"); (await r.ReadLineAsync())!.Should().StartWith("230");

            // LIST over a passive data connection.
            await w.WriteLineAsync("PASV");
            int dport = ParsePasvPort((await r.ReadLineAsync())!);
            using (var data = new TcpClient())
            {
                await data.ConnectAsync(IPAddress.Loopback, dport);
                await w.WriteLineAsync("LIST");
                (await r.ReadLineAsync())!.Should().StartWith("150");
                var listing = await new StreamReader(data.GetStream()).ReadToEndAsync();
                (await r.ReadLineAsync())!.Should().StartWith("226");
                listing.Should().Contain("hello.txt");
            }

            // RETR the file.
            await w.WriteLineAsync("PASV");
            int dport2 = ParsePasvPort((await r.ReadLineAsync())!);
            using (var data2 = new TcpClient())
            {
                await data2.ConnectAsync(IPAddress.Loopback, dport2);
                await w.WriteLineAsync("RETR hello.txt");
                (await r.ReadLineAsync())!.Should().StartWith("150");
                var content = await new StreamReader(data2.GetStream()).ReadToEndAsync();
                (await r.ReadLineAsync())!.Should().StartWith("226");
                content.Should().Be("world");
            }

            await w.WriteLineAsync("QUIT");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Etp_SearchAndQueryReturnsMatches()
    {
        var dir = Directory.CreateTempSubdirectory("whereisit-etp-tests-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "alpha.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "notes.txt"), "");

        using var inner = new WhereIsIt.App.Services.InProcEngineClient(() => new[] { dir.FullName });
        using var engine = new WhereIsIt.App.Services.FilteringEngineClient(inner);
        using var server = new FtpServer(dir.FullName, 0, engine);
        var port = server.Start();

        try
        {
            using var ctrl = new TcpClient();
            await ctrl.ConnectAsync(IPAddress.Loopback, port);
            using var cs = ctrl.GetStream();
            using var r = new StreamReader(cs);
            using var w = new StreamWriter(cs) { AutoFlush = true, NewLine = "\r\n" };

            (await r.ReadLineAsync())!.Should().StartWith("220");
            await w.WriteLineAsync("USER anonymous"); (await r.ReadLineAsync())!.Should().StartWith("331");
            await w.WriteLineAsync("PASS x"); (await r.ReadLineAsync())!.Should().StartWith("230");

            await w.WriteLineAsync("EVERYTHING SEARCH ext:cs");
            (await r.ReadLineAsync())!.Should().StartWith("200");

            await w.WriteLineAsync("PASV");
            int dport = ParsePasvPort((await r.ReadLineAsync())!);
            using var data = new TcpClient();
            await data.ConnectAsync(IPAddress.Loopback, dport);
            await w.WriteLineAsync("EVERYTHING QUERY");
            (await r.ReadLineAsync())!.Should().StartWith("150");
            var results = await new StreamReader(data.GetStream()).ReadToEndAsync();
            (await r.ReadLineAsync())!.Should().StartWith("226");

            results.Should().Contain("alpha.cs");
            results.Should().NotContain("notes.txt");

            await w.WriteLineAsync("QUIT");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }
}
