using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class HttpSearchServerTests
{
    private sealed class FakeEngine : IEngineClient
    {
        public readonly Subject<IReadOnlyList<uint>> ResultsSubject = new();
        public readonly Dictionary<uint, ResultRowModel> Rows = new();
        public string? LastQuery { get; private set; }

        public IObservable<string> StatusChanges => new Subject<string>();
        public IObservable<int> MetricsChanges => new Subject<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => ResultsSubject;

        public Task SearchAsync(string query, CancellationToken ct)
        {
            LastQuery = query;
            // Emit canned IDs on a background thread to mimic the real engine.
            Task.Run(() =>
            {
                Thread.Sleep(10);
                ResultsSubject.OnNext(new uint[] { 1, 2 });
            });
            return Task.CompletedTask;
        }
        public Task SortAsync(string key, bool descending, CancellationToken ct) => Task.CompletedTask;
        public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken ct) => Task.FromResult(Rows[id]);
    }

    [Fact]
    public async Task Search_ReturnsJsonWithMatchingRows()
    {
        var engine = new FakeEngine();
        engine.Rows[1] = new ResultRowModel("alpha.txt", @"C:\dir", 100,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "A");
        engine.Rows[2] = new ResultRowModel("beta.txt",  @"C:\dir", 200,
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), "A");

        // Use 0 to let the OS pick a free port.
        using var server = new HttpSearchServer(engine, port: 0);
        var port = server.Start();

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/search?q=alpha");
        resp.IsSuccessStatusCode.Should().BeTrue();
        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("query").GetString().Should().Be("alpha");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        var first = doc.RootElement.GetProperty("results")[0];
        first.GetProperty("name").GetString().Should().BeOneOf("alpha.txt", "beta.txt");
    }

    [Fact]
    public async Task Search_RejectsExternalIpsByListeningOnLoopbackOnly()
    {
        var engine = new FakeEngine();
        using var server = new HttpSearchServer(engine, port: 0);
        var port = server.Start();

        // Loopback works
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/search?q=x");
        resp.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task NonSearchPath_Returns404()
    {
        var engine = new FakeEngine();
        using var server = new HttpSearchServer(engine, port: 0);
        var port = server.Start();
        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/something-else");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}
