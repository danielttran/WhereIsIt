using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Coverage for the &lt; &gt; grouped boolean query parser + evaluator.
/// </summary>
public class BooleanQueryTests
{
    [Fact]
    public void TryParse_NoOperators_ReturnsNull()
    {
        // Pure implicit-AND with no group/OR — nothing for the tree to add.
        BooleanQuery.TryParse("report log").Should().BeNull();
    }

    [Fact]
    public void TryParse_TopLevelOr_Parses()
    {
        // A top-level OR is now modelled (the QueryParser caller only engages it
        // for a standalone '|' token, so bare "a|b" queries stay the flat form).
        BooleanQuery.TryParse("a|b").Should().BeOfType<BoolOr>();
    }

    [Fact]
    public void TryParse_SingleGroup_IsAndOfTerms()
    {
        var e = BooleanQuery.TryParse("<a b>");
        e.Should().BeOfType<BoolAnd>();
        ((BoolAnd)e!).Parts.Should().HaveCount(2);
    }

    [Fact]
    public void TryParse_GroupedOr_BuildsOrOfAnds()
    {
        var e = BooleanQuery.TryParse("<a b>|<c d>");
        e.Should().BeOfType<BoolOr>();
        var or = (BoolOr)e!;
        or.Parts.Should().HaveCount(2);
        or.Parts[0].Should().BeOfType<BoolAnd>();
        or.Parts[1].Should().BeOfType<BoolAnd>();
    }

    [Fact]
    public void TryParse_OrInsideGroup()
    {
        var e = BooleanQuery.TryParse("<a|b>");
        e.Should().BeOfType<BoolOr>();
        ((BoolOr)e!).Parts.Should().HaveCount(2);
    }

    [Fact]
    public void TryParse_NotBeforeGroup()
    {
        var e = BooleanQuery.TryParse("!<a b>");
        e.Should().BeOfType<BoolNot>();
        ((BoolNot)e!).Inner.Should().BeOfType<BoolAnd>();
    }

    [Fact]
    public void TryParse_FunctionBecomesLeaf()
    {
        // ext:cs is now a function leaf AND-ed with the <a b> group.
        var e = BooleanQuery.TryParse("ext:cs <a b>");
        e.Should().BeOfType<BoolAnd>();
        var and = (BoolAnd)e!;
        and.Parts.Should().HaveCount(2);
        and.Parts.OfType<BoolFunc>().Should().ContainSingle(f => f.Token == "ext:cs");
    }

    [Fact]
    public void TryParse_OrOfFunctionGroups()
    {
        // <ext:cs>|<ext:txt> — function-level OR via grouping.
        var e = BooleanQuery.TryParse("<ext:cs>|<ext:txt>");
        e.Should().BeOfType<BoolOr>();
        var or = (BoolOr)e!;
        or.Parts.Should().HaveCount(2);
        or.Parts[0].Should().BeOfType<BoolFunc>();
        or.Parts[1].Should().BeOfType<BoolFunc>();
    }

    [Fact]
    public void CollectFunctionTokens_GathersAllFuncLeaves()
    {
        var e = BooleanQuery.TryParse("<ext:cs>|<size:>1mb>")!;
        var tokens = new List<string>();
        BooleanQuery.CollectFunctionTokens(e, tokens);
        tokens.Should().Contain("ext:cs");
    }

    // ── evaluation ────────────────────────────────────────────────────────

    private static bool EvalWith(string query, params string[] present)
    {
        var set = new HashSet<string>(present);
        var e = BooleanQuery.TryParse(query);
        e.Should().NotBeNull();
        return BooleanQuery.EvalTerms(e!, alts => alts.Any(set.Contains));
    }

    [Theory]
    [InlineData(true, "a", "b")]      // (a AND b) holds
    [InlineData(false, "a")]          // missing b ⇒ first group fails, second fails
    [InlineData(true, "c", "d")]      // second group holds
    [InlineData(false, "a", "c")]     // neither group fully present
    public void Eval_GroupedOrOfAnds(bool expected, params string[] present)
    {
        EvalWith("<a b>|<c d>", present).Should().Be(expected);
    }

    [Fact]
    public void Eval_NotGroup_Negates()
    {
        EvalWith("!<a b>", "a", "b").Should().BeFalse();
        EvalWith("!<a b>", "a").Should().BeTrue();
    }
}
