using System.IO;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Wire-shape contract for the SingleInstance launch forwarder. The WinUI
/// <c>SingleInstance</c> in the App project orchestrates the named pipe; the
/// JSON payload + sentinel format live in App.Core so xUnit can verify them
/// without standing up a WinUI dispatcher.
/// </summary>
public class LaunchForwardingTests
{
    [Fact]
    public void Serialize_RoundTrips_AllFields()
    {
        var cli = CommandLineArgs.Parse(new[]
        {
            "--query",  "ext:md report",
            "--path",   @"E:\Dev\WhereIsIt",
            "--minimized",
        });

        var wire = LaunchForwarding.Serialize(cli);
        var round = LaunchForwarding.Deserialize(wire);

        round.Should().NotBeNull();
        round!.Query.Should().Be("ext:md report");
        round.ScopeRoot.Should().Be(@"E:\Dev\WhereIsIt");
        round.StartMinimised.Should().BeTrue();
    }

    [Fact]
    public void Serialize_EmptyCli_ProducesNullableFields()
    {
        var cli = CommandLineArgs.Parse(System.Array.Empty<string>());
        var wire = LaunchForwarding.Serialize(cli);
        var round = LaunchForwarding.Deserialize(wire);

        round!.Query.Should().BeNull();
        round.ScopeRoot.Should().BeNull();
        round.StartMinimised.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_Garbage_ReturnsNull()
    {
        // The listener loop relies on this: a malformed forward must not throw
        // — the ListenLoop's `if (payload is null) continue` keeps it alive.
        LaunchForwarding.Deserialize("this is not json").Should().BeNull();
        LaunchForwarding.Deserialize("").Should().BeNull();
        LaunchForwarding.Deserialize("{").Should().BeNull();
    }

    [Fact]
    public void Deserialize_PartialJson_PopulatesPresentFields()
    {
        // Forward-compat: a future flag added to the payload should not break
        // an older primary; deserialisation ignores unknown fields and leaves
        // missing ones at their defaults.
        var p = LaunchForwarding.Deserialize(@"{""Query"":""alpha"",""UnknownField"":42}");
        p.Should().NotBeNull();
        p!.Query.Should().Be("alpha");
        p.ScopeRoot.Should().BeNull();
        p.StartMinimised.Should().BeFalse();
    }

    [Fact]
    public void WriteSentinel_WritesGrepFormatToOverridePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wii-sentinel-{System.Guid.NewGuid():N}.txt");
        try
        {
            var ok = LaunchForwarding.WriteSentinel(new LaunchForwarding.Payload
            {
                Query = "ext:cs",
                ScopeRoot = @"C:\src",
                StartMinimised = true,
            }, path);
            ok.Should().BeTrue();

            var text = File.ReadAllText(path);
            // The audit harness greps for "query=<expected>" — that exact shape
            // is part of the contract, NOT an implementation detail.
            text.Should().Contain("query=ext:cs");
            text.Should().Contain(@"scope=C:\src");
            text.Should().Contain("min=True");
            text.Should().MatchRegex(@"time=\d{4}-\d\d-\d\dT");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void WriteSentinel_NullFields_BecomeEmptyStrings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wii-sentinel-{System.Guid.NewGuid():N}.txt");
        try
        {
            LaunchForwarding.WriteSentinel(new LaunchForwarding.Payload(), path).Should().BeTrue();
            var text = File.ReadAllText(path);
            text.Should().Contain("query=\n");   // empty string, not "null"
            text.Should().Contain("scope=\n");
            text.Should().Contain("min=False");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void WriteSentinel_InvalidPath_ReturnsFalse_DoesNotThrow()
    {
        // Dispatch on the primary calls this; a tmp-dir hiccup must not crash
        // the dispatcher callback, just return false.
        var bad = Path.Combine("Z:\\does\\not\\exist", "sentinel.txt");
        var ok = LaunchForwarding.WriteSentinel(new LaunchForwarding.Payload(), bad);
        ok.Should().BeFalse();
    }

    [Fact]
    public void DefaultSentinelPath_LivesUnderTempAndUsesKnownName()
    {
        // The manual audit reads from %TEMP%\whereisit-last-forward.txt — that
        // exact name is the cross-process contract. Don't rename without
        // updating audit.ps1.
        var p = LaunchForwarding.DefaultSentinelPath;
        Path.GetFileName(p).Should().Be("whereisit-last-forward.txt");
        Path.GetDirectoryName(p).Should().Be(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
    }
}
