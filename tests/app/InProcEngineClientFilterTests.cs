using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Tests for advanced query syntax in InProcEngineClient:
/// ext:, file:, folder:, audio:/video: macros, size:, NOT (!), OR (|), case:
/// Each test creates an isolated temp directory so results are deterministic.
/// </summary>
public class InProcEngineClientFilterTests : IAsyncLifetime
{
    private DirectoryInfo _root = null!;

    public Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("whereisit-filter-tests-");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _root.Delete(recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<uint>?> SearchAsync(string query)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<uint>?>();
        var client = new InProcEngineClient(() => new[] { _root.FullName });
        client.ObserveResults.Subscribe(ids => tcs.TrySetResult(ids));
        _ = client.SearchAsync(query, CancellationToken.None);
        return tcs.Task;
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

    // ── ext: filter ───────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ExtFilter_ReturnsOnlyMatchingExtension()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "b.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "c.cs"), "");

        var names = await GetNamesAsync("ext:cs");

        names.Should().BeEquivalentTo("a.cs", "c.cs");
    }

    [Fact]
    public async Task Search_ExtFilterMultiple_ReturnsMatchingExtensions()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "a.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "b.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "c.py"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "d.json"), "");

        var names = await GetNamesAsync("ext:cs;py");

        names.Should().BeEquivalentTo("a.cs", "c.py");
    }

    [Fact]
    public async Task Search_ExtWithTerm_FiltersExtensionAndName()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.cs"), "");

        var names = await GetNamesAsync("ext:txt report");

        names.Should().Equal("report.txt");
    }

    // ── file: / folder: type filter ───────────────────────────────────────

    [Fact]
    public async Task Search_FileColon_ReturnsOnlyFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "subdir"));

        var names = await GetNamesAsync("file:");

        names.Should().Contain("a.txt");
        names.Should().NotContain("subdir");
    }

    [Fact]
    public async Task Search_FolderColon_ReturnsOnlyFolders()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_root.FullName, "subdir"));

        var names = await GetNamesAsync("folder:");

        names.Should().Contain("subdir");
        names.Should().NotContain("a.txt");
    }

    // ── audio: / video: macros ────────────────────────────────────────────

    [Fact]
    public async Task Search_AudioMacro_ReturnsAudioFilesOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "song.mp3"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "track.flac"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "movie.mp4"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "");

        var names = await GetNamesAsync("audio:");

        names.Should().BeEquivalentTo("song.mp3", "track.flac");
    }

    [Fact]
    public async Task Search_VideoMacro_ReturnsVideoFilesOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "movie.mp4"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "clip.mkv"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "song.mp3"), "");

        var names = await GetNamesAsync("video:");

        names.Should().BeEquivalentTo("movie.mp4", "clip.mkv");
    }

    [Fact]
    public async Task Search_DocMacro_ReturnsDocumentFilesOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.pdf"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "song.mp3"), "");

        var names = await GetNamesAsync("doc:");

        names.Should().BeEquivalentTo("report.pdf", "notes.txt");
    }

    [Fact]
    public async Task Search_CodeMacro_ReturnsSourceFilesOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "main.cs"),    "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "util.py"),    "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "readme.md"),  "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "photo.jpg"),  "");

        var names = await GetNamesAsync("code:");

        names.Should().BeEquivalentTo("main.cs", "util.py");
    }

    [Fact]
    public async Task Search_PicMacro_ReturnsPictureFilesOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "photo.jpg"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "icon.png"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "song.mp3"), "");

        var names = await GetNamesAsync("pic:");

        names.Should().BeEquivalentTo("photo.jpg", "icon.png");
    }

    // ── NOT (!) operator ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_NotOperator_ExcludesMatchingFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "backup_report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "notes.txt"), "");

        var names = await GetNamesAsync("ext:txt !backup");

        names.Should().NotContain("backup_report.txt");
        names.Should().Contain("report.txt");
        names.Should().Contain("notes.txt");
    }

    // ── OR (|) operator ───────────────────────────────────────────────────

    [Fact]
    public async Task Search_OrOperator_ReturnsFilesMatchingEither()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "report.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "app.log"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "readme.md"), "");

        var names = await GetNamesAsync("report|log");

        names.Should().Contain("report.txt");
        names.Should().Contain("app.log");
        names.Should().NotContain("readme.md");
    }

    // ── case: modifier ────────────────────────────────────────────────────

    [Fact]
    public async Task Search_CaseModifier_EnablesCaseSensitiveMatch()
    {
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "README.md"), "");
        await File.WriteAllTextAsync(Path.Combine(_root.FullName, "readme.md"), "");

        var names = await GetNamesAsync("case:README");

        names.Should().Contain("README.md");
        names.Should().NotContain("readme.md");
    }

    // ── size: filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_SizeGreaterThan_ReturnsOnlyLargerFiles()
    {
        var bigFile = Path.Combine(_root.FullName, "big.bin");
        var smallFile = Path.Combine(_root.FullName, "small.bin");
        await File.WriteAllBytesAsync(bigFile, new byte[2000]);
        await File.WriteAllBytesAsync(smallFile, new byte[100]);

        var names = await GetNamesAsync("size:>1kb");

        names.Should().Contain("big.bin");
        names.Should().NotContain("small.bin");
    }

    [Fact]
    public async Task Search_SizeBetween_ReturnsFilesInRange()
    {
        var a = Path.Combine(_root.FullName, "a.bin");
        var b = Path.Combine(_root.FullName, "b.bin");
        var c = Path.Combine(_root.FullName, "c.bin");
        await File.WriteAllBytesAsync(a, new byte[500]);
        await File.WriteAllBytesAsync(b, new byte[1500]);
        await File.WriteAllBytesAsync(c, new byte[5000]);

        var names = await GetNamesAsync("size:1b..2kb");

        names.Should().Contain("a.bin");
        names.Should().Contain("b.bin");
        names.Should().NotContain("c.bin");
    }

    [Fact]
    public async Task Search_SizeEmpty_ReturnsOnlyZeroByteFiles()
    {
        var empty = Path.Combine(_root.FullName, "empty.txt");
        var nonEmpty = Path.Combine(_root.FullName, "full.txt");
        await File.WriteAllTextAsync(empty, "");
        await File.WriteAllTextAsync(nonEmpty, "content");

        var names = await GetNamesAsync("size:empty");

        names.Should().Contain("empty.txt");
        names.Should().NotContain("full.txt");
    }
}
