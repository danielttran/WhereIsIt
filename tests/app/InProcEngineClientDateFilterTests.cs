using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Tests for Everything-style date modifiers in InProcEngineClient:
/// dm: (modified), dc: (created), da: (accessed)
/// Supports keywords (today, yesterday, thisweek, lastweek, thismonth, thisyear),
/// year/year-month/full-date literals, comparison operators (&gt;, &lt;, &gt;=, &lt;=),
/// and ranges via "..".
/// </summary>
public class InProcEngineClientDateFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-date-filter-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _root.Delete(recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<string[]> GetNamesAsync(string query)
    {
        using var client = new InProcEngineClient(() => new[] { _root.FullName });
        IReadOnlyList<uint>? received = null;
        using var _ = client.ObserveResults.Subscribe(ids => received = ids);
        await client.SearchAsync(query, CancellationToken.None);
        if (received is null or { Count: 0 }) return [];
        var names = new List<string>();
        foreach (var id in received)
        {
            var row = await client.GetRowAsync(id, CancellationToken.None);
            names.Add(row.Name);
        }
        return [.. names];
    }

    private async Task<string> WriteFileWithModifiedAsync(string name, DateTime modified)
    {
        var path = Path.Combine(_root.FullName, name);
        await File.WriteAllTextAsync(path, "");
        File.SetLastWriteTime(path, modified);
        return path;
    }

    // ── dm:today ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_DmToday_ReturnsFilesModifiedToday()
    {
        await WriteFileWithModifiedAsync("fresh.txt", DateTime.Now);
        await WriteFileWithModifiedAsync("old.txt",   DateTime.Now.AddDays(-30));

        var names = await GetNamesAsync("dm:today");

        names.Should().Contain("fresh.txt");
        names.Should().NotContain("old.txt");
    }

    // ── dm:yesterday ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_DmYesterday_ReturnsFilesModifiedYesterday()
    {
        await WriteFileWithModifiedAsync("yesterday.txt", DateTime.Now.AddDays(-1));
        await WriteFileWithModifiedAsync("today.txt",     DateTime.Now);
        await WriteFileWithModifiedAsync("week-ago.txt",  DateTime.Now.AddDays(-7));

        var names = await GetNamesAsync("dm:yesterday");

        names.Should().Contain("yesterday.txt");
        names.Should().NotContain("today.txt");
        names.Should().NotContain("week-ago.txt");
    }

    // ── dm:lastweek / dm:thisweek ─────────────────────────────────────────

    [Fact]
    public async Task Search_DmThisWeek_ReturnsFilesModifiedInCurrentWeek()
    {
        await WriteFileWithModifiedAsync("recent.txt", DateTime.Now);
        await WriteFileWithModifiedAsync("month-ago.txt", DateTime.Now.AddDays(-30));

        var names = await GetNamesAsync("dm:thisweek");

        names.Should().Contain("recent.txt");
        names.Should().NotContain("month-ago.txt");
    }

    // ── dm:>YYYY-MM-DD comparison ─────────────────────────────────────────

    [Fact]
    public async Task Search_DmGreaterThanDate_ReturnsFilesNewerThan()
    {
        await WriteFileWithModifiedAsync("new.txt", new DateTime(2026, 4, 1));
        await WriteFileWithModifiedAsync("old.txt", new DateTime(2024, 1, 1));

        var names = await GetNamesAsync("dm:>2025-01-01");

        names.Should().Contain("new.txt");
        names.Should().NotContain("old.txt");
    }

    [Fact]
    public async Task Search_DmLessThanDate_ReturnsFilesOlderThan()
    {
        await WriteFileWithModifiedAsync("new.txt", new DateTime(2026, 4, 1));
        await WriteFileWithModifiedAsync("old.txt", new DateTime(2024, 1, 1));

        var names = await GetNamesAsync("dm:<2025-01-01");

        names.Should().Contain("old.txt");
        names.Should().NotContain("new.txt");
    }

    // ── dm:YYYY (year shorthand) ──────────────────────────────────────────

    [Fact]
    public async Task Search_DmYearLiteral_ReturnsFilesModifiedInThatYear()
    {
        await WriteFileWithModifiedAsync("a-2024.txt", new DateTime(2024, 6, 15));
        await WriteFileWithModifiedAsync("a-2025.txt", new DateTime(2025, 6, 15));
        await WriteFileWithModifiedAsync("a-2026.txt", new DateTime(2026, 1, 1));

        var names = await GetNamesAsync("dm:2025");

        names.Should().Contain("a-2025.txt");
        names.Should().NotContain("a-2024.txt");
        names.Should().NotContain("a-2026.txt");
    }

    // ── dm:YYYY..YYYY range ───────────────────────────────────────────────

    [Fact]
    public async Task Search_DmYearRange_ReturnsFilesInRange()
    {
        await WriteFileWithModifiedAsync("y2023.txt", new DateTime(2023, 6, 15));
        await WriteFileWithModifiedAsync("y2024.txt", new DateTime(2024, 6, 15));
        await WriteFileWithModifiedAsync("y2025.txt", new DateTime(2025, 6, 15));
        await WriteFileWithModifiedAsync("y2027.txt", new DateTime(2027, 6, 15));

        var names = await GetNamesAsync("dm:2024..2025");

        names.Should().Contain("y2024.txt");
        names.Should().Contain("y2025.txt");
        names.Should().NotContain("y2023.txt");
        names.Should().NotContain("y2027.txt");
    }

    // ── combination with other filters ────────────────────────────────────

    [Fact]
    public async Task Search_DmYear_WithExtFilter_AppliesBoth()
    {
        await WriteFileWithModifiedAsync("a.cs",  new DateTime(2024, 6, 15));
        await WriteFileWithModifiedAsync("b.txt", new DateTime(2024, 6, 15));
        await WriteFileWithModifiedAsync("c.cs",  new DateTime(2026, 1, 1));

        var names = await GetNamesAsync("dm:2024 ext:cs");

        names.Should().Contain("a.cs");
        names.Should().NotContain("b.txt"); // wrong extension
        names.Should().NotContain("c.cs");  // wrong year
    }
}
