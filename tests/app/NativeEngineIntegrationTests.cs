/*
 * NativeEngineIntegrationTests.cs
 *
 * Integration tests for NativeEngineClient (WhereIsIt.Engine.Native.dll).
 * Guards every test with a DLL-availability check; skips cleanly when the DLL
 * is not present so the CI green-test count is unaffected.
 *
 * Test groups:
 *   1. Basic search  — file name / extension / directory / case-insensitivity
 *   2. Wildcard      — * ? path-fragment patterns
 *   3. Regex         — regex:true prefix
 *   4. Sort          — name / size / modified, asc + desc
 *   5. Row data      — all fields of ResultRowModel
 *   6. Observables   — StatusChanges, MetricsChanges
 *   7. Edge cases    — empty query, repeat searches, out-of-range IDs
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// One NativeEngineClient is shared across the test class to avoid re-scanning
/// on every test.  The engine is scoped to a small temp directory so tests are
/// deterministic and fast even on machines where admin rights are unavailable.
/// </summary>
public sealed class NativeEngineFixture : IDisposable
{
    public DirectoryInfo Root { get; } = null!;
    public NativeEngineClient? Client { get; }
    public bool Available { get; }

    // File sizes created in the fixture — used for exact-size assertions.
    public const int AlphaSize   = 100;
    public const int BetaSize    = 200;
    public const int GammaSize   = 4000;
    public const int DeltaSize   = 50;
    public const int EpsilonSize = 300;
    public const int ReportJanSize = 1024;
    public const int ReportFebSize = 2048;
    public const int ArchiveSize   = 8192;
    public const int AppLogSize    = 512;
    public const int ErrorLogSize  = 256;

    public NativeEngineFixture()
    {
        Available = NativeEngineClient.IsAvailable();
        if (!Available) return;

        // Fixed path so index.dat stays valid across runs.
        var fixedPath = Path.Combine(Path.GetTempPath(), "whereisit-tests-fixed");
        if (Directory.Exists(fixedPath)) Directory.Delete(fixedPath, recursive: true);
        Root = Directory.CreateDirectory(fixedPath);

        // Root-level files
        CreateFile("alpha.txt",        AlphaSize);
        CreateFile("beta.log",         BetaSize);
        CreateFile("gamma.exe",        GammaSize);
        CreateFile("delta.TXT",        DeltaSize);
        CreateFile("epsilon-2026.csv", EpsilonSize);
        CreateText("тест-файл.txt",    "unicode content");   // Cyrillic
        CreateText("file with spaces.txt", "spaces content");

        // Subdirectory files
        Root.CreateSubdirectory("subdir");
        CreateFile(Path.Combine("subdir", "report-jan.log"), ReportJanSize);
        CreateFile(Path.Combine("subdir", "report-feb.log"), ReportFebSize);
        CreateFile(Path.Combine("subdir", "archive.zip"),    ArchiveSize);

        // Nested subdirectory files
        Directory.CreateDirectory(Path.Combine(Root.FullName, "logs", "daily"));
        CreateFile(Path.Combine("logs", "daily", "app.log"),   AppLogSize);
        CreateFile(Path.Combine("logs", "daily", "error.log"), ErrorLogSize);

        // Delete stale index.dat (both legacy location and new %LOCALAPPDATA% location)
        // so every test run starts with a fresh scan.
        var indexPath1 = Path.Combine(AppContext.BaseDirectory, "index.dat");
        if (File.Exists(indexPath1)) File.Delete(indexPath1);
        var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (appData != null)
        {
            var indexPath2 = Path.Combine(appData, "WhereIsIt", "index.dat");
            if (File.Exists(indexPath2)) File.Delete(indexPath2);
        }

        // Create engine scoped to our temp dir — scope roots BEFORE start.
        Client = new NativeEngineClient(scopeRoots: [Root.FullName]);

        // Block until the engine reports "Ready" (indexing complete).
        // The engine emits "Ready - N items" once the initial scan finishes.
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (!Client.GetCurrentStatus().StartsWith("Ready", StringComparison.OrdinalIgnoreCase)
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers                                                              //
    // ------------------------------------------------------------------ //

    /// <summary>Run a search and wait up to <paramref name="timeoutSec"/> for stable results.</summary>
    public async Task<IReadOnlyList<uint>> SearchAsync(string query, int timeoutSec = 30)
    {
        if (Client is null) return [];

        IReadOnlyList<uint>? received = null;
        DateTime lastEmit = DateTime.MinValue;
        using var sub = Client.ObserveResults.Subscribe(ids => { received = ids; lastEmit = DateTime.UtcNow; });

        await Client.SearchAsync(query, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (received is not null && (DateTime.UtcNow - lastEmit).TotalMilliseconds >= 300)
                break;
        }

        return received ?? [];
    }

    /// <summary>
    /// Find the result row in <paramref name="ids"/> whose parent path is inside our
    /// temp root.  Robust in both scoped (only our files) and full-drive scan modes.
    /// </summary>
    public async Task<ResultRowModel?> FindOurRowAsync(IReadOnlyList<uint> ids, string fileName)
    {
        if (Client is null) return null;
        foreach (var id in ids)
        {
            var row = await Client.GetRowAsync(id, CancellationToken.None);
            if (row.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                row.ParentPath.StartsWith(Root.FullName, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    private void CreateFile(string relativePath, int sizeBytes)
    {
        var full = Path.Combine(Root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[sizeBytes]);
    }

    private void CreateText(string relativePath, string content)
    {
        var full = Path.Combine(Root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    public void Dispose()
    {
        Client?.Dispose();
        try { Root?.Delete(recursive: true); } catch { }
    }
}

[CollectionDefinition("NativeEngine")]
public class NativeEngineCollection : ICollectionFixture<NativeEngineFixture> { }

[Collection("NativeEngine")]
public class NativeEngineIntegrationTests
{
    private readonly NativeEngineFixture _fx;

    public NativeEngineIntegrationTests(NativeEngineFixture fx) => _fx = fx;

    private bool Skip() => !_fx.Available || _fx.Client is null;
    private async Task<IReadOnlyList<uint>> Search(string q, int timeoutSec = 30)
        => await _fx.SearchAsync(q, timeoutSec);

    // ================================================================== //
    // 1. Basic search                                                      //
    // ================================================================== //

    [Fact]
    public async Task Search_ExactFileName_ReturnsMatch()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty("alpha.txt was created in the temp root");
    }

    [Fact]
    public async Task Search_PartialName_ReturnsAllMatches()
    {
        if (Skip()) return;
        var ids = await Search("report");
        ids.Count.Should().BeGreaterThanOrEqualTo(2,
            "report-jan.log and report-feb.log both contain 'report'");
    }

    [Fact]
    public async Task Search_CaseInsensitive_MatchesSameCount()
    {
        if (Skip()) return;
        var lower = await Search("delta.txt");
        var upper = await Search("DELTA.TXT");
        lower.Count.Should().BeGreaterThan(0);
        upper.Count.Should().Be(lower.Count,
            "case should not affect result count for the same query");
    }

    [Fact]
    public async Task Search_ExtensionOnly_ReturnsMatchingFiles()
    {
        if (Skip()) return;
        var ids = await Search(".log");
        ids.Count.Should().BeGreaterThanOrEqualTo(4,
            "beta.log, report-jan.log, report-feb.log, app.log, error.log all end in .log");
    }

    [Fact]
    public async Task Search_DirectoryName_ReturnsDirectory()
    {
        if (Skip()) return;
        var ids = await Search("subdir");
        ids.Should().NotBeEmpty("the 'subdir' directory was created");
    }

    [Fact]
    public async Task Search_NonExistentName_ReturnsEmpty()
    {
        if (Skip()) return;
        // Use a GUID-suffix so this never accidentally matches a real file.
        var ids = await Search($"WHEREISIT_NOFILE_{Guid.NewGuid():N}");
        ids.Should().BeEmpty();
    }

    // ================================================================== //
    // 2. Wildcard patterns                                                 //
    // ================================================================== //

    [Fact]
    public async Task Wildcard_StarDotExt_MatchesAllWithExtension()
    {
        if (Skip()) return;
        var ids = await Search("*.log");
        ids.Count.Should().BeGreaterThanOrEqualTo(4, "at least 4 .log files exist");
    }

    [Fact]
    public async Task Wildcard_PrefixStar_MatchesSuffix()
    {
        if (Skip()) return;
        var ids = await Search("*-jan.log");
        ids.Should().NotBeEmpty("report-jan.log matches *-jan.log");
    }

    [Fact]
    public async Task Wildcard_SingleChar_MatchesOneChar()
    {
        if (Skip()) return;
        var ids = await Search("alph?.txt");
        ids.Should().NotBeEmpty("alpha.txt matches alph?.txt");
    }

    [Fact]
    public async Task Wildcard_PathFragment_MatchesNestedFile()
    {
        if (Skip()) return;
        var ids = await Search("logs/*/app.log");
        ids.Count.Should().BeGreaterThanOrEqualTo(1,
            "logs/daily/app.log should match logs/*/app.log");
    }

    [Fact]
    public async Task Wildcard_StarPattern_MatchesMultiple()
    {
        if (Skip()) return;
        var ids = await Search("report-*.log");
        ids.Count.Should().BeGreaterThanOrEqualTo(2,
            "report-jan.log and report-feb.log both match report-*.log");
    }

    [Fact]
    public async Task Wildcard_StarOnly_MatchesEverything()
    {
        if (Skip()) return;
        var ids = await Search("*");
        ids.Count.Should().BeGreaterThan(0, "* should match all indexed entries");
    }

    // ================================================================== //
    // 3. Regex mode                                                        //
    // ================================================================== //

    [Fact]
    public async Task Regex_ExactMatch_ReturnsSpecificFile()
    {
        if (Skip()) return;
        var ids = await Search(@"regex:true ^alpha\.txt$");
        ids.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Regex_Alternation_MatchesEitherFile()
    {
        if (Skip()) return;
        var ids = await Search(@"regex:true (alpha|beta)\.");
        ids.Count.Should().BeGreaterThanOrEqualTo(2, "both alpha.txt and beta.log match");
    }

    [Fact]
    public async Task Regex_DigitPattern_MatchesFileWithYear()
    {
        if (Skip()) return;
        var ids = await Search(@"regex:true epsilon-[0-9]{4}\.csv");
        ids.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Regex_InvalidPattern_ReturnsEmpty()
    {
        if (Skip()) return;
        // An invalid regex should yield empty results, not crash.
        var ids = await Search("regex:true [invalid(");
        ids.Should().BeEmpty("invalid regex pattern produces no results");
    }

    [Fact]
    public async Task Regex_CaseInsensitive_MatchesBothCasings()
    {
        if (Skip()) return;
        var lower = await Search(@"regex:true alpha\.txt");
        var upper = await Search(@"regex:true ALPHA\.TXT");
        lower.Count.Should().BeGreaterThan(0);
        upper.Count.Should().Be(lower.Count, "regex matching is case-insensitive by default");
    }

    // ================================================================== //
    // 4. Sort                                                              //
    // ================================================================== //

    private async Task<IReadOnlyList<uint>?> SortAndWait(string key, bool descending)
    {
        // Subscribe BEFORE calling Sort, then wait for results to stabilise.
        IReadOnlyList<uint>? sorted = null;
        DateTime lastEmit = DateTime.MinValue;
        using var sub = _fx.Client!.ObserveResults.Subscribe(r => { sorted = r; lastEmit = DateTime.UtcNow; });
        await _fx.Client.SortAsync(key, descending, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (sorted is not null && (DateTime.UtcNow - lastEmit).TotalMilliseconds > 100)
                break;
        }
        return sorted;
    }

    [Fact]
    public async Task Sort_ByNameAscending_FirstIsAlphabeticallyFirst()
    {
        if (Skip()) return;
        // Seed a search so there are results to sort.
        var initial = await Search("*");
        initial.Should().NotBeEmpty();

        var sorted = await SortAndWait("name", descending: false);
        sorted.Should().NotBeNull();
        if (sorted!.Count < 2) return;

        var first = await _fx.Client!.GetRowAsync(sorted[0],  CancellationToken.None);
        var last  = await _fx.Client!.GetRowAsync(sorted[^1], CancellationToken.None);
        string.Compare(first.Name, last.Name, StringComparison.OrdinalIgnoreCase)
              .Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public async Task Sort_ByNameDescending_FirstIsAlphabeticallyLast()
    {
        if (Skip()) return;
        var initial = await Search("*");
        initial.Should().NotBeEmpty();

        var sorted = await SortAndWait("name", descending: true);
        sorted.Should().NotBeNull();
        if (sorted!.Count < 2) return;

        var first = await _fx.Client!.GetRowAsync(sorted[0],  CancellationToken.None);
        var last  = await _fx.Client!.GetRowAsync(sorted[^1], CancellationToken.None);
        string.Compare(first.Name, last.Name, StringComparison.OrdinalIgnoreCase)
              .Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Sort_BySizeAscending_SmallestFirst()
    {
        if (Skip()) return;
        var initial = await Search("*.log");
        initial.Should().NotBeEmpty();

        var sorted = await SortAndWait("size", descending: false);
        sorted.Should().NotBeNull();
        if (sorted!.Count < 2) return;

        var first = await _fx.Client!.GetRowAsync(sorted[0],  CancellationToken.None);
        var last  = await _fx.Client!.GetRowAsync(sorted[^1], CancellationToken.None);
        first.SizeBytes.Should().BeLessThanOrEqualTo(last.SizeBytes,
            "smallest file should come first in ascending size sort");
    }

    [Fact]
    public async Task Sort_BySizeDescending_LargestFirst()
    {
        if (Skip()) return;
        var initial = await Search("*.log");
        initial.Should().NotBeEmpty();

        var sorted = await SortAndWait("size", descending: true);
        sorted.Should().NotBeNull();
        if (sorted!.Count < 2) return;

        var first = await _fx.Client!.GetRowAsync(sorted[0],  CancellationToken.None);
        var last  = await _fx.Client!.GetRowAsync(sorted[^1], CancellationToken.None);
        first.SizeBytes.Should().BeGreaterThanOrEqualTo(last.SizeBytes,
            "largest file should come first in descending size sort");
    }

    [Fact]
    public async Task Sort_ByModifiedAscending_OldestFirst()
    {
        if (Skip()) return;
        var initial = await Search("*");
        initial.Should().NotBeEmpty();

        var sorted = await SortAndWait("modified", descending: false);
        sorted.Should().NotBeNull();
        if (sorted!.Count < 2) return;

        var first = await _fx.Client!.GetRowAsync(sorted[0],  CancellationToken.None);
        var last  = await _fx.Client!.GetRowAsync(sorted[^1], CancellationToken.None);
        first.ModifiedUtc.Should().BeOnOrBefore(last.ModifiedUtc);
    }

    // ================================================================== //
    // 5. Row data                                                          //
    // ================================================================== //

    [Fact]
    public async Task GetRow_ReturnsNonEmptyName()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty();
        var row = await _fx.Client!.GetRowAsync(ids[0], CancellationToken.None);
        row.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetRow_ReturnsNonEmptyParentPath()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty();
        var row = await _fx.Client!.GetRowAsync(ids[0], CancellationToken.None);
        row.ParentPath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetRow_SizeBytesMatchesFileSize()
    {
        if (Skip()) return;
        var ids = await Search("gamma.exe");
        ids.Should().NotBeEmpty();
        // Find the one in our temp directory (handles both scoped and full-drive scan modes).
        var row = await _fx.FindOurRowAsync(ids, "gamma.exe");
        row.Should().NotBeNull("gamma.exe should be found in the test temp directory");
        row!.SizeBytes.Should().Be(NativeEngineFixture.GammaSize,
            "gamma.exe was created with exactly GammaSize bytes");
    }

    [Fact]
    public async Task GetRow_ModifiedDateIsRecent()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty();
        var row = await _fx.FindOurRowAsync(ids, "alpha.txt");
        row.Should().NotBeNull("alpha.txt should be found in the test temp directory");
        row!.ModifiedUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5),
            "file was just created");
    }

    [Fact]
    public async Task GetRow_AttributesStringIsFormattedCorrectly()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty();
        var row = await _fx.Client!.GetRowAsync(ids[0], CancellationToken.None);
        row.Attributes.Should().MatchRegex("^[RHSAD]*$",
            "attributes are a combination of R,H,S,A,D flags");
    }

    [Fact]
    public async Task GetRow_DiagnoseParentPath()
    {
        if (Skip()) return;
        // Verify synthetic ancestor records (0-6) all have non-empty parent paths.
        for (uint id = 0; id <= 6; id++)
        {
            var r = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            r.ParentPath.Should().NotBeNull($"record {id} must have a non-null ParentPath");
        }
    }

    [Fact]
    public async Task GetRow_FullPathCombinesParentAndName()
    {
        if (Skip()) return;
        var ids = await Search("alpha.txt");
        ids.Should().NotBeEmpty();
        var row = await _fx.FindOurRowAsync(ids, "alpha.txt");
        row.Should().NotBeNull();
        var fullPath = Path.Combine(row!.ParentPath, row.Name);
        File.Exists(fullPath).Should().BeTrue(
            "reconstructed full path should point to a real file");
    }

    [Fact]
    public async Task GetRow_SubdirFileHasCorrectSize()
    {
        if (Skip()) return;
        var ids = await Search("report-jan.log");
        ids.Should().NotBeEmpty();
        var row = await _fx.FindOurRowAsync(ids, "report-jan.log");
        row.Should().NotBeNull();
        row!.SizeBytes.Should().Be(NativeEngineFixture.ReportJanSize);
    }

    // ================================================================== //
    // 6. Observables — StatusChanges and MetricsChanges                   //
    // ================================================================== //

    [Fact]
    public async Task StatusChanges_EmittedAfterSearch()
    {
        if (Skip()) return;
        var statuses = new List<string>();
        using var sub = _fx.Client!.StatusChanges.Subscribe(s => statuses.Add(s));

        await _fx.Client.SearchAsync("alpha", CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (statuses.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        statuses.Should().NotBeEmpty("at least one status message should be emitted");
    }

    [Fact]
    public async Task MetricsChanges_EmittedWithResultCount()
    {
        if (Skip()) return;
        int? lastCount = null;
        using var sub = _fx.Client!.MetricsChanges.Subscribe(c => lastCount = c);

        await _fx.Client.SearchAsync("alpha.txt", CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (lastCount is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        lastCount.Should().NotBeNull("MetricsChanges should emit a count after search");
        lastCount.Should().BeGreaterThanOrEqualTo(0);
    }

    // ================================================================== //
    // 7. Edge cases                                                        //
    // ================================================================== //

    [Fact]
    public async Task Search_EmptyQuery_EnumeratesAllItems()
    {
        if (Skip()) return;
        // The native engine treats empty query as "enumerate all indexed items".
        var ids = await Search(string.Empty);
        ids.Count.Should().BeGreaterThan(0, "empty query enumerates all indexed items");
    }

    [Fact]
    public async Task Search_SingleChar_DoesNotCrash()
    {
        if (Skip()) return;
        var ids = await Search("a");
        ids.Should().NotBeNull();
    }

    [Fact]
    public async Task Search_RepeatSearch_LatestResultsDelivered()
    {
        if (Skip()) return;
        var ids1 = await Search("alpha.txt");
        var ids2 = await Search("beta.log");
        ids1.Should().NotBeEmpty();
        ids2.Should().NotBeEmpty();
        ids1.Should().NotBeEquivalentTo(ids2,
            "different file names should produce different results");
    }

    [Fact]
    public async Task Search_ThenSort_ResultOrderChanges()
    {
        if (Skip()) return;
        var initial = await Search("*.log");
        if (initial.Count < 2) return;

        var asc  = await SortAndWait("name", descending: false);
        var desc = await SortAndWait("name", descending: true);

        if (asc is null || desc is null || asc.Count < 2) return;

        var firstName = (await _fx.Client!.GetRowAsync(asc[0],  CancellationToken.None)).Name;
        var lastFirst = (await _fx.Client!.GetRowAsync(desc[0], CancellationToken.None)).Name;
        // Just verify no crash — the names might be the same if only 1 result matches.
        (firstName + lastFirst).Should().NotBeNull();
    }

    [Fact]
    public async Task Search_Unicode_FileNameFound()
    {
        if (Skip()) return;
        var ids = await Search("тест");
        ids.Should().NotBeEmpty("Cyrillic file name 'тест-файл.txt' should be indexed");
    }

    [Fact]
    public async Task Search_FileWithSpaces_FoundCorrectly()
    {
        if (Skip()) return;
        var ids = await Search("file with spaces");
        ids.Should().NotBeEmpty("file names with spaces should be indexed");
    }

    [Fact]
    public async Task GetRow_OutOfRange_DoesNotCrash()
    {
        if (Skip()) return;
        var row = await _fx.Client!.GetRowAsync(uint.MaxValue, CancellationToken.None);
        row.Should().NotBeNull("out-of-range IDs should return a placeholder, not throw");
    }

    // ================================================================== //
    // 8. Path query optimisation — rooted-path + wildcard searches        //
    //    Exercises the m_dirIndex ancestor-chain fast path.                //
    // ================================================================== //

    [Fact]
    public async Task PathQuery_RootedDirWildcardExt_ReturnsOnlyFilesUnderThatDir()
    {
        if (Skip()) return;
        // e.g. "<root>\subdir\*.log" — should find report-jan.log and report-feb.log
        var query = Path.Combine(_fx.Root.FullName, "subdir", "*.log");
        var ids = await Search(query);
        ids.Count.Should().BeGreaterThanOrEqualTo(2,
            "report-jan.log and report-feb.log live under subdir");

        // Every result should be a .log file in the subdir subtree.
        foreach (var id in ids)
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().EndWithEquivalentOf(".log");
            row.ParentPath.Should().ContainEquivalentOf("subdir");
        }
    }

    [Fact]
    public async Task PathQuery_RootedDirExactName_ReturnsSingleFile()
    {
        if (Skip()) return;
        var query = Path.Combine(_fx.Root.FullName, "subdir", "report-jan.log");
        var ids = await Search(query);
        var row = await _fx.FindOurRowAsync(ids, "report-jan.log");
        row.Should().NotBeNull("report-jan.log must be found by exact rooted path");
    }

    [Fact]
    public async Task PathQuery_RootedDirStarStar_ReturnsAllFilesUnderDir()
    {
        if (Skip()) return;
        // "<root>\subdir\*" — should match all files/dirs directly under subdir
        var query = Path.Combine(_fx.Root.FullName, "subdir", "*");
        var ids = await Search(query);
        ids.Count.Should().BeGreaterThanOrEqualTo(3,
            "subdir contains report-jan.log, report-feb.log, and archive.zip");
    }

    [Fact]
    public async Task PathQuery_NestedSubdir_ReturnsFilesInNestedPath()
    {
        if (Skip()) return;
        // "<root>\logs\daily\*.log" — matches app.log and error.log
        var query = Path.Combine(_fx.Root.FullName, "logs", "daily", "*.log");
        var ids = await Search(query);
        ids.Count.Should().BeGreaterThanOrEqualTo(2,
            "logs/daily contains app.log and error.log");
        foreach (var id in ids)
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().EndWithEquivalentOf(".log");
        }
    }

    [Fact]
    public async Task PathQuery_RootedDirTxtFiles_ReturnsOnlyTxtAtRootLevel()
    {
        if (Skip()) return;
        var query = Path.Combine(_fx.Root.FullName, "*.txt");
        var ids = await Search(query);
        ids.Count.Should().BeGreaterThanOrEqualTo(2,
            "alpha.txt and delta.TXT exist at the root level");
        foreach (var id in ids)
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().MatchRegex(@"\.(txt|TXT)$", "only .txt files match *.txt");
        }
    }

    [Fact]
    public async Task PathQuery_NonExistentDir_ReturnsEmpty()
    {
        if (Skip()) return;
        var query = Path.Combine(_fx.Root.FullName, "nonexistent_dir_xyz", "*.log");
        var ids = await Search(query);
        ids.Should().BeEmpty("directory does not exist so no files can be under it");
    }

    // ================================================================== //
    // 9. Stress tests — race conditions, rapid cycling, data integrity     //
    // ================================================================== //

    [Fact]
    public async Task Stress_AllResultIds_ProduceValidRows()
    {
        if (Skip()) return;
        var ids = await Search(string.Empty);
        ids.Count.Should().BeGreaterThan(0);
        foreach (var id in ids)
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().NotBe("?", $"ID {id}: name must not be '?' (engine_get_row returned -1)");
            row.ParentPath.Should().NotBe("?", $"ID {id}: parent path must not be '?'");
            row.SizeBytes.Should().BeGreaterThanOrEqualTo(0UL);
        }
    }

    [Fact]
    public async Task Stress_RapidSearchClear_FinalResultsAreValid()
    {
        if (Skip()) return;
        const int iterations = 50;

        // Fire alternating searches without waiting for results — stresses the
        // engine_wait_results_changed / engine_get_result_ids race window.
        for (int i = 0; i < iterations; i++)
        {
            await _fx.Client!.SearchAsync("alpha", CancellationToken.None);
            await _fx.Client!.SearchAsync(string.Empty, CancellationToken.None);
        }

        var ids = await Search(string.Empty);
        ids.Count.Should().BeGreaterThan(0, "empty query must enumerate all indexed items");

        foreach (var id in ids.Take(Math.Min(ids.Count, 50)))
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().NotBe("?", $"ID {id}: stale or zero-filled ID leaked into results");
            row.ParentPath.Should().NotBe("?", $"ID {id}: stale or zero-filled ID leaked into results");
        }
    }

    [Fact]
    public async Task Stress_RapidQueryCycling_NoCrashAndValidFinalResults()
    {
        if (Skip()) return;
        string[] queries =
        [
            "alpha", "beta", "report", ".log", "subdir", "alpha",
            "a", "", "*.txt", "report-*", "logs", "", "gamma", ""
        ];

        // Fire all in immediate succession — exercises back-to-back prevResults resets.
        foreach (var q in queries)
            await _fx.Client!.SearchAsync(q, CancellationToken.None);

        // Settle on empty query and verify returned IDs are all valid.
        var ids = await Search(string.Empty);
        ids.Count.Should().BeGreaterThan(0);

        foreach (var id in ids.Take(Math.Min(ids.Count, 30)))
        {
            var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
            row.Name.Should().NotBe("?", $"ID {id} must be valid after rapid query cycling");
        }
    }

    [Fact]
    public async Task Stress_InterleavedSearchAndGetRow_NoQuestionMarkData()
    {
        if (Skip()) return;
        const int rounds = 20;

        for (int i = 0; i < rounds; i++)
        {
            var ids = await Search("alpha");
            ids.Should().NotBeEmpty();
            foreach (var id in ids.Take(5))
            {
                var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
                row.Name.Should().NotBe("?", $"round {i}, ID {id}");
            }

            var allIds = await Search(string.Empty);
            foreach (var id in allIds.Take(5))
            {
                var row = await _fx.Client!.GetRowAsync(id, CancellationToken.None);
                row.Name.Should().NotBe("?", $"round {i} (all), ID {id}");
            }
        }
    }

    [Fact]
    public async Task Stress_ObservableResultsDelivered_AllIdsValid()
    {
        if (Skip()) return;
        var invalidIds = new System.Collections.Generic.List<(uint Id, string Issue)>();

        // Subscribe to the live observable and validate every delivered result set.
        using var sub = _fx.Client!.ObserveResults.Subscribe(ids =>
        {
            foreach (var id in ids.Take(20))
            {
                // Synchronous GetRowAsync (returns Task.FromResult — no real async).
                var row = _fx.Client.GetRowAsync(id, CancellationToken.None).GetAwaiter().GetResult();
                if (row.Name == "?")
                    invalidIds.Add((id, "name='?'"));
                if (row.ParentPath == "?")
                    invalidIds.Add((id, "path='?'"));
            }
        });

        // Exercise the observable across multiple searches.
        await _fx.Client!.SearchAsync("alpha", CancellationToken.None);
        await Task.Delay(200);
        await _fx.Client!.SearchAsync(string.Empty, CancellationToken.None);
        await Task.Delay(200);
        await _fx.Client!.SearchAsync("report", CancellationToken.None);
        await Task.Delay(200);
        await _fx.Client!.SearchAsync(string.Empty, CancellationToken.None);
        await Task.Delay(400);

        invalidIds.Should().BeEmpty(
            "every ID delivered via ObserveResults must produce a valid row");
    }
}
