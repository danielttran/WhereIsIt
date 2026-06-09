using System;
using System.IO;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// The ThumbnailGate is the crash-safety fence that stopped the
/// STATUS_STOWED_EXCEPTION the user reported during indexing. The WinUI
/// ThumbnailService trusts its <see cref="ThumbnailGate.IsEligible"/> rule
/// before touching <c>StorageFile.GetFileFromPathAsync</c>, so these tests
/// are the locked-in contract for that rule.
/// </summary>
public class ThumbnailGateTests
{
    [Fact]
    public void IsEligible_Null_ReturnsFalse() =>
        ThumbnailGate.IsEligible(null).Should().BeFalse();

    [Fact]
    public void IsEligible_Empty_ReturnsFalse() =>
        ThumbnailGate.IsEligible(string.Empty).Should().BeFalse();

    [Fact]
    public void IsEligible_BareSegment_ReturnsFalse()
    {
        // Engine emits synthetic ancestor rows during scan whose FullPath is
        // a bare segment like "Users". Passing those to the WinRT API was
        // what triggered the indexing-time crash.
        ThumbnailGate.IsEligible("Users").Should().BeFalse();
        ThumbnailGate.IsEligible("AppData").Should().BeFalse();
        ThumbnailGate.IsEligible("subdir").Should().BeFalse();
    }

    [Fact]
    public void IsEligible_RootedButMissing_ReturnsFalse()
    {
        // Race: engine ID points at a file that was deleted before the UI
        // realised the row. The gate must catch this before the WinRT call.
        var nonExistent = Path.Combine(Path.GetTempPath(), $"wii-gate-missing-{Guid.NewGuid():N}.bin");
        File.Exists(nonExistent).Should().BeFalse("the test must observe a missing file");
        ThumbnailGate.IsEligible(nonExistent).Should().BeFalse();
    }

    [Fact]
    public void IsEligible_RootedAndExisting_ReturnsTrue()
    {
        var probe = Path.Combine(Path.GetTempPath(), $"wii-gate-real-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(probe, "eligible");
            ThumbnailGate.IsEligible(probe).Should().BeTrue();
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    [Fact]
    public void IsEligible_DirectoryPath_ReturnsTrue()
    {
        // Folders also get shell thumbnails (folder icon) — the gate must
        // not reject existing directories just because File.Exists is false.
        var dir = Directory.CreateTempSubdirectory("wii-gate-dir-");
        try
        {
            ThumbnailGate.IsEligible(dir.FullName).Should().BeTrue();
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public void IsEligible_BadPathChars_DoesNotThrow()
    {
        // Defensive: a malformed/unicode path must produce false, not throw —
        // the whole purpose of the gate is that the caller can ignore the
        // result and move on without try/catch noise on the dispatcher.
        var act = () => ThumbnailGate.IsEligible("\0\\?\\very?bad|path");
        act.Should().NotThrow();
        ThumbnailGate.IsEligible("\0\\?\\very?bad|path").Should().BeFalse();
    }
}
