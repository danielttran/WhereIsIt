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
}
