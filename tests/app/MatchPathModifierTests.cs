using System;
using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Regression tests for the <c>path:VALUE</c> / <c>matchpath:VALUE</c> modifier.
/// Like every other value-bearing modifier (<c>case:</c>, <c>word:</c>,
/// <c>file:</c>, <c>ext:</c> …), <c>path:report</c> must set match-path AND add
/// <c>report</c> as a search term — not fall through to a literal substring
/// search for the string "path:report". The bare flag form (<c>path:</c>) still
/// just toggles match-path.
/// </summary>
public class MatchPathModifierTests
{
    [Fact]
    public void Path_WithValue_EnablesMatchPath()
    {
        QueryParser.Parse("path:report").MatchPath.Should().BeTrue();
    }

    [Fact]
    public void Path_WithValue_AddsValueAsSearchTerm()
    {
        var q = QueryParser.Parse("path:report");
        q.Clauses.SelectMany(c => c.Alternatives).Should().Contain("report");
    }

    [Fact]
    public void Path_WithValue_DoesNotLeakLiteralModifierToken()
    {
        var q = QueryParser.Parse("path:report");
        q.Clauses.SelectMany(c => c.Alternatives)
            .Should().NotContain(a => a.Contains("path:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MatchPathAlias_WithValue_EnablesFlagAndAddsTerm()
    {
        var q = QueryParser.Parse("matchpath:docs");
        q.MatchPath.Should().BeTrue();
        q.Clauses.SelectMany(c => c.Alternatives).Should().Contain("docs");
    }

    [Fact]
    public void Path_WithValue_CombinesWithOtherModifiers()
    {
        // path:report ext:cs — match-path on, term "report", extension filter cs.
        var q = QueryParser.Parse("path:report ext:cs");
        q.MatchPath.Should().BeTrue();
        q.ExtWhitelist.Should().Contain("cs");
        q.Clauses.SelectMany(c => c.Alternatives).Should().Contain("report");
    }

    // GUARD — already passes; the bare flag form must keep working after the fix.
    [Fact]
    public void Path_BareFlag_StillEnablesMatchPath()
    {
        QueryParser.Parse("path:").MatchPath.Should().BeTrue();
    }
}
