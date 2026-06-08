using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Regression tests for stray-quote handling in <c>QueryParser.Tokenize</c>.
/// A single unterminated <c>"</c> (a common typo or a pasted quoted path) must
/// NOT put the tokenizer into quote mode for the rest of the string and swallow
/// every following modifier into one literal term — so <c>report" ext:cs</c>
/// still parses the <c>ext:</c> filter. A quote only groups when it has a
/// matching close quote; balanced quoted phrases stay a single term.
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
