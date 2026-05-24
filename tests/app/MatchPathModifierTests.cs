using System;
using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// AUDIT FOLLOW-UP — KNOWN-FAILING until fixed on Windows.
///
/// The <c>path:</c> modifier only handles the bare flag form (<c>path:</c>
/// toggles match-path). Every other value-bearing modifier (<c>case:</c>,
/// <c>word:</c>, <c>file:</c>, <c>ext:</c> …) also supports <c>mod:VALUE</c>,
/// which sets the flag AND adds VALUE as a search term. So <c>path:report</c>
/// should mean "match against the path AND search for <c>report</c>", but today
/// it falls through to a literal substring search for the string
/// "path:report" and never enables match-path.
///
/// Intended fix: add a <c>path:</c>/<c>matchpath:</c> value branch in
/// <c>QueryParser.Parse</c> mirroring the existing <c>case:</c>/<c>word:</c>
/// branches (set the flag, then <c>AddTerm(clauses, token[5..], false)</c>).
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
