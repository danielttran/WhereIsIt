using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// AUDIT FOLLOW-UP — KNOWN-FAILING until fixed on Windows.
///
/// <c>QueryParser.Tokenize</c> flips an in-quote flag on every <c>"</c> with no
/// handling for an unterminated quote and no escape for a literal quote. A
/// single stray <c>"</c> (a common typo, or a pasted quoted path) puts the
/// tokenizer into quote mode for the rest of the string, swallowing every
/// following modifier into one giant literal term — so <c>report" ext:cs</c>
/// silently loses the <c>ext:</c> filter and searches for the literal phrase
/// <c>report ext:cs</c> instead.
///
/// Intended fix: make a dangling quote deterministic so it cannot consume
/// trailing modifier tokens (treat a lone quote as a literal character, or
/// close it at the next whitespace, or support a "" escape — implementer's
/// choice). These tests pin only the load-bearing property: a stray quote must
/// not eat the rest of the query.
/// </summary>
public class QueryTokenizeQuoteTests
{
    [Fact]
    public void StrayTrailingQuote_DoesNotSwallowFollowingExtModifier()
    {
        var q = QueryParser.Parse("report\" ext:cs");
        q.ExtWhitelist.Should().Contain("cs");
    }

    [Fact]
    public void StrayLeadingQuote_DoesNotSwallowFollowingExtModifier()
    {
        var q = QueryParser.Parse("\"unterminated ext:log");
        q.ExtWhitelist.Should().Contain("log");
    }

    [Fact]
    public void StrayQuote_DoesNotSwallowSizeModifier()
    {
        var q = QueryParser.Parse("notes\" size:>1mb");
        q.Size.Should().NotBeNull();
    }

    [Fact]
    public void StrayQuote_KeepsFollowingTermsSeparate()
    {
        // The two words must stay distinct AND-ed terms, not be fused into one
        // literal "alpha beta" phrase by the runaway quote.
        var q = QueryParser.Parse("alpha\" beta");
        q.Clauses.SelectMany(c => c.Alternatives).Should().NotContain("alpha beta");
    }

    // GUARD — already passes; a *balanced* quoted phrase must stay one term.
    [Fact]
    public void BalancedQuotedPhrase_StaysSingleTerm()
    {
        var q = QueryParser.Parse("\"hello world\"");
        q.Clauses.SelectMany(c => c.Alternatives).Should().Contain("hello world");
    }

    // GUARD — already passes; balanced quotes before a modifier keep both.
    [Fact]
    public void BalancedQuotedPhrase_ThenModifier_ParsesBoth()
    {
        var q = QueryParser.Parse("\"my file\" ext:txt");
        q.ExtWhitelist.Should().Contain("txt");
        q.Clauses.SelectMany(c => c.Alternatives).Should().Contain("my file");
    }
}
