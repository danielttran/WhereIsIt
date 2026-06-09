using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class CommandLineArgsTests
{
    [Fact]
    public void Parse_NoArgs_ReturnsEmpty()
    {
        var p = CommandLineArgs.Parse(System.Array.Empty<string>());
        p.Query.Should().BeNull();
        p.ScopeRoot.Should().BeNull();
    }

    [Fact]
    public void Parse_SearchShortFlag_PopulatesQuery()
    {
        var p = CommandLineArgs.Parse(new[] { "-s", "report ext:txt" });
        p.Query.Should().Be("report ext:txt");
    }

    [Fact]
    public void Parse_SearchLongFlag_PopulatesQuery()
    {
        var p = CommandLineArgs.Parse(new[] { "--search", "foo" });
        p.Query.Should().Be("foo");
    }

    [Fact]
    public void Parse_PathFlag_PopulatesScopeRoot()
    {
        var p = CommandLineArgs.Parse(new[] { "-p", @"C:\Users\me" });
        p.ScopeRoot.Should().Be(@"C:\Users\me");
    }

    [Fact]
    public void Parse_BothFlags_PopulatesBoth()
    {
        var p = CommandLineArgs.Parse(new[] { "-s", "foo", "-p", @"D:\data" });
        p.Query.Should().Be("foo");
        p.ScopeRoot.Should().Be(@"D:\data");
    }

    [Fact]
    public void Parse_BareTerm_TreatedAsQuery()
    {
        var p = CommandLineArgs.Parse(new[] { "report" });
        p.Query.Should().Be("report");
    }

    [Fact]
    public void Parse_MultipleBareTerms_JoinedAsQuery()
    {
        var p = CommandLineArgs.Parse(new[] { "report", "2026" });
        p.Query.Should().Be("report 2026");
    }

    [Fact]
    public void Parse_FlagMissingValue_LeavesFieldNull()
    {
        var p = CommandLineArgs.Parse(new[] { "-s" });
        p.Query.Should().BeNull();
    }

    // ── NEW: headless / harness flags ───────────────────────────────────────

    [Fact]
    public void Parse_HeadlessFlag_SetsHeadless()
    {
        CommandLineArgs.Parse(new[] { "--headless" }).Headless.Should().BeTrue();
        CommandLineArgs.Parse(new[] { "--no-ui" }).Headless.Should().BeTrue();
    }

    [Fact]
    public void Parse_QueryAlias_PopulatesQuery()
    {
        // -q and --query are aliases for -s/--search so the harness can use the
        // verb that reads most naturally in scripts ("--query *.md").
        CommandLineArgs.Parse(new[] { "-q", "alpha" }).Query.Should().Be("alpha");
        CommandLineArgs.Parse(new[] { "--query", "beta" }).Query.Should().Be("beta");
    }

    [Fact]
    public void Parse_OutputFlag_PopulatesOutputPath()
    {
        CommandLineArgs.Parse(new[] { "--output", @"C:\tmp\r.txt" }).OutputPath.Should().Be(@"C:\tmp\r.txt");
        CommandLineArgs.Parse(new[] { "-o",       @"D:\out.tsv"   }).OutputPath.Should().Be(@"D:\out.tsv");
    }

    [Fact]
    public void Parse_MaxResults_PopulatesInt()
    {
        CommandLineArgs.Parse(new[] { "--max-results", "42" }).MaxResults.Should().Be(42);
        CommandLineArgs.Parse(new[] { "--max", "7" }).MaxResults.Should().Be(7);
    }

    [Fact]
    public void Parse_MaxResults_NonNumeric_StaysZero()
    {
        // Unparseable values must NOT become Query — they're consumed by the
        // flag and just leave the numeric default in place. This avoids the
        // foot-gun where "--max foo bar" would otherwise leak "bar" as bare.
        var p = CommandLineArgs.Parse(new[] { "--max-results", "not-a-number" });
        p.MaxResults.Should().Be(0);
        p.Query.Should().BeNull();
    }

    [Fact]
    public void Parse_Timeout_PopulatesPositiveInt_DefaultsTo10()
    {
        CommandLineArgs.Parse(new[] { "--timeout", "30" }).TimeoutSeconds.Should().Be(30);
        // No --timeout flag → default 10.
        CommandLineArgs.Parse(new[] { "--query", "x" }).TimeoutSeconds.Should().Be(10);
        // Non-positive is clamped to default so a bug in the harness can't
        // accidentally produce a 0 ms wait that races every emission.
        CommandLineArgs.Parse(new[] { "--timeout", "0" }).TimeoutSeconds.Should().Be(10);
        CommandLineArgs.Parse(new[] { "--timeout", "-5" }).TimeoutSeconds.Should().Be(10);
    }

    [Fact]
    public void Parse_NameOnly_SetsNameOnly()
    {
        CommandLineArgs.Parse(new[] { "--name-only" }).NameOnly.Should().BeTrue();
        CommandLineArgs.Parse(new[] { "-n" }).NameOnly.Should().BeTrue();
    }

    [Fact]
    public void Parse_MinimisedFlag_SetsStartMinimised()
    {
        // Both spellings (UK + US) supported because Everything's docs use the
        // US one and contributors use the UK one in code review.
        CommandLineArgs.Parse(new[] { "--minimized" }).StartMinimised.Should().BeTrue();
        CommandLineArgs.Parse(new[] { "--minimised" }).StartMinimised.Should().BeTrue();
    }

    [Fact]
    public void Parse_EnableHttp_SetsFlag()
    {
        CommandLineArgs.Parse(new[] { "--enable-http" }).EnableHttp.Should().BeTrue();
    }

    [Fact]
    public void Parse_HeadlessCombo_FullHarnessShape()
    {
        // The canonical harness invocation — every field should land in its
        // matching property so the headless path can run from a script
        // without touching the WinUI dispatcher.
        var p = CommandLineArgs.Parse(new[]
        {
            "--headless", "--query", "ext:cs",
            "--max-results", "50",
            "--timeout", "60",
            "--name-only",
            "--output", @"C:\tmp\out.txt",
        });
        p.Headless.Should().BeTrue();
        p.Query.Should().Be("ext:cs");
        p.MaxResults.Should().Be(50);
        p.TimeoutSeconds.Should().Be(60);
        p.NameOnly.Should().BeTrue();
        p.OutputPath.Should().Be(@"C:\tmp\out.txt");
    }
}
