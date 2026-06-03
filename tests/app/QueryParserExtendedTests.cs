using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Parity coverage for the Everything search functions added in the
/// feature-parity audit: wildcards:/nowildcards:, diacritics:/nodiacritics:,
/// the encoding-specific content aliases, and the childcount: family.
/// </summary>
public class QueryParserExtendedTests
{
    // ── wildcards: / nowildcards: ─────────────────────────────────────────

    [Fact]
    public void Parse_DefaultsWildcardsOn()
    {
        QueryParser.Parse("report").Wildcards.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoWildcards_DisablesWildcards()
    {
        QueryParser.Parse("nowildcards:").Wildcards.Should().BeFalse();
        QueryParser.Parse("wildcards:false").Wildcards.Should().BeFalse();
    }

    [Fact]
    public void Parse_WildcardsTrue_EnablesWildcards()
    {
        QueryParser.Parse("wildcards:true").Wildcards.Should().BeTrue();
    }

    [Fact]
    public void Parse_WildcardsWithTerm_EnablesAndExtractsTerm()
    {
        var q = QueryParser.Parse("wildcards:rep*");
        q.Wildcards.Should().BeTrue();
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("rep*");
    }

    // ── diacritics: / nodiacritics: ───────────────────────────────────────

    [Fact]
    public void Parse_DefaultsMatchDiacriticsOn()
    {
        QueryParser.Parse("cafe").MatchDiacritics.Should().BeTrue();
    }

    [Fact]
    public void Parse_NoDiacritics_DisablesMatchDiacritics()
    {
        QueryParser.Parse("nodiacritics:").MatchDiacritics.Should().BeFalse();
        QueryParser.Parse("diacritics:false").MatchDiacritics.Should().BeFalse();
    }

    [Fact]
    public void Parse_DiacriticsWithTerm_EnablesAndExtractsTerm()
    {
        var q = QueryParser.Parse("diacritics:resume");
        q.MatchDiacritics.Should().BeTrue();
        q.Clauses[0].Alternatives.Should().Equal("resume");
    }

    // ── content aliases ───────────────────────────────────────────────────

    [Theory]
    [InlineData("content:hello", "hello")]
    [InlineData("ansicontent:hello", "hello")]
    [InlineData("utf8content:hello", "hello")]
    [InlineData("utf16content:hello", "hello")]
    [InlineData("utf16becontent:hello", "hello")]
    public void Parse_ContentAliases_MapToContentSearch(string query, string expected)
    {
        QueryParser.Parse(query).ContentSearch.Should().Be(expected);
    }

    // ── childcount: family ────────────────────────────────────────────────

    [Fact]
    public void Parse_ChildCount_ParsesComparison()
    {
        var q = QueryParser.Parse("childcount:>5");
        q.ChildCount.Should().NotBeNull();
        q.ChildCount!.Op.Should().Be(SizeOp.GreaterThan);
        q.ChildCount.Low.Should().Be(5UL);
    }

    [Fact]
    public void Parse_ChildFileCount_ParsesEquality()
    {
        var q = QueryParser.Parse("childfilecount:0");
        q.ChildFileCount.Should().NotBeNull();
        q.ChildFileCount!.Op.Should().Be(SizeOp.Equal);
        q.ChildFileCount.Low.Should().Be(0UL);
    }

    [Fact]
    public void Parse_ChildFolderCount_ParsesRange()
    {
        var q = QueryParser.Parse("childfoldercount:2..4");
        q.ChildFolderCount.Should().NotBeNull();
        q.ChildFolderCount!.Op.Should().Be(SizeOp.Between);
        q.ChildFolderCount.Low.Should().Be(2UL);
        q.ChildFolderCount.High.Should().Be(4UL);
    }

    [Fact]
    public void Parse_ChildCount_MakesQueryNonEmpty()
    {
        QueryParser.Parse("childcount:0").IsEmpty.Should().BeFalse();
    }

    // ── RemoveDiacritics helper ───────────────────────────────────────────

    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("résumé", "resume")]
    [InlineData("naïve", "naive")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void RemoveDiacritics_FoldsAccents(string input, string expected)
    {
        QueryParser.RemoveDiacritics(input).Should().Be(expected);
    }
}
