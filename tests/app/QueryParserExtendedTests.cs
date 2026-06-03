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

    // ── infolder: (alias for child:) ──────────────────────────────────────

    [Fact]
    public void Parse_InFolder_SetsChildOfPath()
    {
        var q = QueryParser.Parse(@"infolder:C:\Projects");
        q.ChildOfPath.Should().Be(@"C:\Projects");
    }

    [Fact]
    public void Parse_InFolder_PreservesValueCasing()
    {
        QueryParser.Parse(@"infolder:C:\MyDir").ChildOfPath.Should().Be(@"C:\MyDir");
    }

    // ── run-count / run-date filters ──────────────────────────────────────

    [Fact]
    public void Parse_RunCount_ParsesComparison()
    {
        var q = QueryParser.Parse("rc:>5");
        q.RunCount.Should().NotBeNull();
        q.RunCount!.Op.Should().Be(SizeOp.GreaterThan);
        q.RunCount.Low.Should().Be(5UL);
    }

    [Fact]
    public void Parse_RunCountLongForm_Parses()
    {
        QueryParser.Parse("runcount:0").RunCount!.Op.Should().Be(SizeOp.Equal);
    }

    [Fact]
    public void Parse_DateRun_ParsesKeyword()
    {
        var q = QueryParser.Parse("dr:today");
        q.DateRun.Should().NotBeNull();
    }

    [Fact]
    public void Parse_RunCount_MakesQueryNonEmpty()
    {
        QueryParser.Parse("rc:>0").IsEmpty.Should().BeFalse();
        QueryParser.Parse("dr:thisweek").IsEmpty.Should().BeFalse();
    }

    // ── date keywords (month names, tomorrow) ─────────────────────────────

    [Fact]
    public void ParseDateSpec_Tomorrow_IsTheNextDay()
    {
        var now = new System.DateTime(2026, 6, 3, 14, 0, 0);
        var r = QueryParser.ParseDateSpec("tomorrow", now);
        r.Should().NotBeNull();
        r!.Min.Should().Be(new System.DateTime(2026, 6, 4));
        r.Max.Should().Be(new System.DateTime(2026, 6, 5).AddTicks(-1));
    }

    [Theory]
    [InlineData("july", 7)]
    [InlineData("jul", 7)]
    [InlineData("december", 12)]
    [InlineData("dec", 12)]
    [InlineData("january", 1)]
    [InlineData("sept", 9)]
    public void ParseDateSpec_MonthName_IsThatMonthOfCurrentYear(string spec, int month)
    {
        var now = new System.DateTime(2026, 6, 3);
        var r = QueryParser.ParseDateSpec(spec, now);
        r.Should().NotBeNull();
        r!.Min.Should().Be(new System.DateTime(2026, month, 1));
        r.Max.Should().Be(new System.DateTime(2026, month, 1).AddMonths(1).AddTicks(-1));
    }

    [Fact]
    public void ParseDateSpec_MonthName_IsCaseInsensitive()
    {
        var now = new System.DateTime(2026, 6, 3);
        QueryParser.ParseDateSpec("JULY", now).Should().NotBeNull();
    }

    [Fact]
    public void ParseDateSpec_PastWeek_IsRollingSevenDayWindow()
    {
        var now = new System.DateTime(2026, 6, 3, 10, 0, 0);
        var r = QueryParser.ParseDateSpec("pastweek", now);
        r.Should().NotBeNull();
        r!.Min.Should().Be(new System.DateTime(2026, 6, 3).AddDays(-7)); // from today's date
        r.Max.Should().Be(now);
    }

    [Fact]
    public void ParseDateSpec_PastMonthAndYear_AreRolling()
    {
        var now = new System.DateTime(2026, 6, 3, 10, 0, 0);
        QueryParser.ParseDateSpec("pastmonth", now)!.Min.Should().Be(new System.DateTime(2026, 5, 3));
        QueryParser.ParseDateSpec("pastyear", now)!.Min.Should().Be(new System.DateTime(2025, 6, 3));
    }

    [Theory]
    [InlineData("3days", 0, -3, 0)]
    [InlineData("last2weeks", 0, -14, 0)]
    [InlineData("past6months", -6, 0, 0)]
    [InlineData("1year", 0, 0, -1)]
    public void ParseDateSpec_RelativeSpan_IsRollingPastWindow(
        string spec, int monthDelta, int dayDelta, int yearDelta)
    {
        var now = new System.DateTime(2026, 6, 3, 10, 0, 0);
        var r = QueryParser.ParseDateSpec(spec, now);
        r.Should().NotBeNull();
        var expectedMin = now.AddMonths(monthDelta).AddDays(dayDelta).AddYears(yearDelta);
        r!.Min.Should().Be(expectedMin);
        r.Max.Should().Be(now);
    }

    [Fact]
    public void ParseDateSpec_NextRelativeSpan_IsFutureWindow()
    {
        var now = new System.DateTime(2026, 6, 3, 10, 0, 0);
        var r = QueryParser.ParseDateSpec("next2days", now);
        r.Should().NotBeNull();
        r!.Min.Should().Be(now);
        r.Max.Should().Be(now.AddDays(2));
    }

    [Fact]
    public void ParseDateSpec_FourDigitYear_StillParsesAsYearNotSpan()
    {
        // A bare 4-digit number must remain a year literal, not a relative span.
        var now = new System.DateTime(2026, 6, 3);
        var r = QueryParser.ParseDateSpec("2024", now);
        r!.Min.Should().Be(new System.DateTime(2024, 1, 1));
    }

    [Fact]
    public void ParseDateSpec_Weekday_ResolvesToMostRecentOccurrence()
    {
        // 2026-06-03 is a Wednesday; the most recent Monday is 2026-06-01.
        var now = new System.DateTime(2026, 6, 3);
        var r = QueryParser.ParseDateSpec("monday", now);
        r.Should().NotBeNull();
        r!.Min.Should().Be(new System.DateTime(2026, 6, 1));
        r.Max.Should().Be(new System.DateTime(2026, 6, 2).AddTicks(-1));
    }

    [Fact]
    public void ParseDateSpec_WeekdayToday_IsToday()
    {
        // 2026-06-03 is a Wednesday.
        var now = new System.DateTime(2026, 6, 3);
        var r = QueryParser.ParseDateSpec("wed", now);
        r!.Min.Should().Be(new System.DateTime(2026, 6, 3));
    }

    // ── ExtractHighlightTerms (result-list match highlighting) ────────────

    [Fact]
    public void ExtractHighlightTerms_ReturnsPositiveLiteralTerms()
    {
        QueryParser.ExtractHighlightTerms("report 2026")
            .Should().BeEquivalentTo("report", "2026");
    }

    [Fact]
    public void ExtractHighlightTerms_IncludesOrAlternatives()
    {
        QueryParser.ExtractHighlightTerms("report|log")
            .Should().BeEquivalentTo("report", "log");
    }

    [Fact]
    public void ExtractHighlightTerms_ExcludesNegatedClauses()
    {
        QueryParser.ExtractHighlightTerms("report !backup")
            .Should().Equal("report");
    }

    [Fact]
    public void ExtractHighlightTerms_ExcludesWildcardPatterns()
    {
        QueryParser.ExtractHighlightTerms("rep* ext:cs")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractHighlightTerms_RegexQuery_ReturnsEmpty()
    {
        QueryParser.ExtractHighlightTerms("regex:^a.*z$").Should().BeEmpty();
    }

    [Fact]
    public void ExtractHighlightTerms_IgnoresModifierOnlyQuery()
    {
        QueryParser.ExtractHighlightTerms("ext:cs size:>1mb").Should().BeEmpty();
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
