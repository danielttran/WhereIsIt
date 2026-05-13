using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class QueryParserTests
{
    // ── empty / whitespace ────────────────────────────────────────────────

    [Fact]
    public void Parse_Empty_ReturnsEmptyQuery()
    {
        var q = QueryParser.Parse("");
        q.IsEmpty.Should().BeTrue();
        q.Clauses.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Whitespace_ReturnsEmptyQuery()
    {
        QueryParser.Parse("   ").IsEmpty.Should().BeTrue();
    }

    // ── simple terms ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleTerm_ProducesOneClauses()
    {
        var q = QueryParser.Parse("report");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Negated.Should().BeFalse();
        q.Clauses[0].Alternatives.Should().Equal("report");
    }

    [Fact]
    public void Parse_TwoTerms_ProducesTwoClauses()
    {
        var q = QueryParser.Parse("report 2026");
        q.Clauses.Should().HaveCount(2);
    }

    // ── OR alternatives ──────────────────────────────────────────────────

    [Fact]
    public void Parse_PipeSeparated_ProducesOneClauseWithMultipleAlternatives()
    {
        var q = QueryParser.Parse("report|log");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("report", "log");
    }

    // ── NOT operator ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_BangPrefix_ProducesNegatedClause()
    {
        var q = QueryParser.Parse("!backup");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Negated.Should().BeTrue();
        q.Clauses[0].Alternatives.Should().Equal("backup");
    }

    [Fact]
    public void Parse_TermAndNot_ProducesTwoClausesOneNegated()
    {
        var q = QueryParser.Parse("report !backup");
        q.Clauses.Should().HaveCount(2);
        q.Clauses.Any(c => c.Negated).Should().BeTrue();
        q.Clauses.Any(c => !c.Negated).Should().BeTrue();
    }

    // ── extension filter ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ExtCs_SetsExtWhitelist()
    {
        var q = QueryParser.Parse("ext:cs");
        q.ExtWhitelist.Should().Equal("cs");
    }

    [Fact]
    public void Parse_ExtMultiple_SetsAllExtensions()
    {
        var q = QueryParser.Parse("ext:cs;py;js");
        q.ExtWhitelist.Should().BeEquivalentTo("cs", "py", "js");
    }

    [Fact]
    public void Parse_ExtWithTerm_SetsWhitelistAndAddsTerm()
    {
        var q = QueryParser.Parse("ext:txt report");
        q.ExtWhitelist.Should().Equal("txt");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("report");
    }

    [Fact]
    public void Parse_ExtIsCaseInsensitive()
    {
        var q = QueryParser.Parse("EXT:CS;PY");
        q.ExtWhitelist.Should().BeEquivalentTo("cs", "py");
    }

    // ── type filters ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_FileColon_SetsFileOnly()
    {
        var q = QueryParser.Parse("file:");
        q.FileOnly.Should().BeTrue();
        q.FolderOnly.Should().BeFalse();
    }

    [Fact]
    public void Parse_FolderColon_SetsFolderOnly()
    {
        var q = QueryParser.Parse("folder:");
        q.FolderOnly.Should().BeTrue();
        q.FileOnly.Should().BeFalse();
    }

    [Fact]
    public void Parse_FileColonWithTerm_SetsFileOnlyAndAddsTerm()
    {
        var q = QueryParser.Parse("file: report");
        q.FileOnly.Should().BeTrue();
        q.Clauses.Should().HaveCount(1);
    }

    // ── type macros ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_AudioMacro_SetsAudioExtensions()
    {
        var q = QueryParser.Parse("audio:");
        q.ExtWhitelist.Should().NotBeNullOrEmpty();
        q.ExtWhitelist.Should().Contain("mp3");
        q.ExtWhitelist.Should().Contain("flac");
        q.ExtWhitelist.Should().Contain("wav");
    }

    [Fact]
    public void Parse_VideoMacro_SetsVideoExtensions()
    {
        var q = QueryParser.Parse("video:");
        q.ExtWhitelist.Should().Contain("mp4");
        q.ExtWhitelist.Should().Contain("mkv");
        q.ExtWhitelist.Should().NotContain("mp3");
    }

    [Fact]
    public void Parse_DocMacro_SetsDocExtensions()
    {
        var q = QueryParser.Parse("doc:");
        q.ExtWhitelist.Should().Contain("pdf");
        q.ExtWhitelist.Should().Contain("docx");
        q.ExtWhitelist.Should().Contain("txt");
    }

    [Fact]
    public void Parse_PicMacro_SetsPicExtensions()
    {
        var q = QueryParser.Parse("pic:");
        q.ExtWhitelist.Should().Contain("jpg");
        q.ExtWhitelist.Should().Contain("png");
        q.ExtWhitelist.Should().NotContain("mp4");
    }

    [Fact]
    public void Parse_ExeMacro_SetsExeExtensions()
    {
        var q = QueryParser.Parse("exe:");
        q.ExtWhitelist.Should().Contain("exe");
        q.ExtWhitelist.Should().Contain("msi");
    }

    [Fact]
    public void Parse_ZipMacro_SetsZipExtensions()
    {
        var q = QueryParser.Parse("zip:");
        q.ExtWhitelist.Should().Contain("zip");
        q.ExtWhitelist.Should().Contain("7z");
        q.ExtWhitelist.Should().Contain("rar");
    }

    // ── case modifier ────────────────────────────────────────────────────

    [Fact]
    public void Parse_CaseColon_SetsCaseSensitive()
    {
        var q = QueryParser.Parse("case:");
        q.CaseSensitive.Should().BeTrue();
        q.Clauses.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NocaseColon_LeavesInsensitive()
    {
        var q = QueryParser.Parse("nocase:");
        q.CaseSensitive.Should().BeFalse();
    }

    [Fact]
    public void Parse_CaseWithTerm_SetsModifierAndExtractsTerm()
    {
        var q = QueryParser.Parse("case:README");
        q.CaseSensitive.Should().BeTrue();
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("README");
    }

    // ── regex modifier ───────────────────────────────────────────────────

    [Fact]
    public void Parse_RegexColon_SetsRegexMode()
    {
        var q = QueryParser.Parse("regex:");
        q.RegexMode.Should().BeTrue();
    }

    [Fact]
    public void Parse_RegexTruePrefix_SetsRegexModeAndExtractsTerm()
    {
        var q = QueryParser.Parse("regex:true ^readme\\.md$");
        q.RegexMode.Should().BeTrue();
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("^readme\\.md$");
    }

    // ── whole word modifier ──────────────────────────────────────────────

    [Fact]
    public void Parse_WordColon_SetsWholeWord()
    {
        var q = QueryParser.Parse("word:");
        q.WholeWord.Should().BeTrue();
    }

    [Fact]
    public void Parse_WwColon_SetsWholeWord()
    {
        var q = QueryParser.Parse("ww:");
        q.WholeWord.Should().BeTrue();
    }

    // ── match path modifier ──────────────────────────────────────────────

    [Fact]
    public void Parse_PathColon_SetsMatchPath()
    {
        var q = QueryParser.Parse("path:");
        q.MatchPath.Should().BeTrue();
    }

    [Fact]
    public void Parse_MatchpathColon_SetsMatchPath()
    {
        var q = QueryParser.Parse("matchpath:");
        q.MatchPath.Should().BeTrue();
    }

    // ── size filter ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_SizeGreaterThan_ParsesCorrectly()
    {
        var q = QueryParser.Parse("size:>1mb");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.GreaterThan);
        q.Size.Low.Should().Be(1024UL * 1024);
    }

    [Fact]
    public void Parse_SizeGreaterOrEqual_ParsesCorrectly()
    {
        var q = QueryParser.Parse("size:>=100kb");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.GreaterOrEqual);
        q.Size.Low.Should().Be(100UL * 1024);
    }

    [Fact]
    public void Parse_SizeLessThan_ParsesCorrectly()
    {
        var q = QueryParser.Parse("size:<500");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.LessThan);
        q.Size.Low.Should().Be(500UL);
    }

    [Fact]
    public void Parse_SizeRange_ParsesCorrectly()
    {
        var q = QueryParser.Parse("size:1kb..1mb");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.Between);
        q.Size.Low.Should().Be(1024UL);
        q.Size.High.Should().Be(1024UL * 1024);
    }

    [Fact]
    public void Parse_SizeTinyConstant_ParsesToCorrectRange()
    {
        var q = QueryParser.Parse("size:tiny");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.Between);
        q.Size.Low.Should().Be(1UL);
        q.Size.High.Should().Be(10 * 1024UL);
    }

    [Fact]
    public void Parse_SizeEmpty_ParsesAsZero()
    {
        var q = QueryParser.Parse("size:empty");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.Equal);
        q.Size.Low.Should().Be(0UL);
    }

    [Fact]
    public void Parse_SizeGigantic_ParsesAsGreaterThan128Mb()
    {
        var q = QueryParser.Parse("size:gigantic");
        q.Size.Should().NotBeNull();
        q.Size!.Op.Should().Be(SizeOp.GreaterThan);
        q.Size.Low.Should().Be(128UL * 1024 * 1024);
    }

    // ── SizeRange.Matches ─────────────────────────────────────────────────

    [Theory]
    [InlineData(SizeOp.Equal,          100, 0,   100, true)]
    [InlineData(SizeOp.Equal,          100, 0,   101, false)]
    [InlineData(SizeOp.GreaterThan,    100, 0,   101, true)]
    [InlineData(SizeOp.GreaterThan,    100, 0,   100, false)]
    [InlineData(SizeOp.GreaterOrEqual, 100, 0,   100, true)]
    [InlineData(SizeOp.LessThan,       100, 0,    99, true)]
    [InlineData(SizeOp.LessThan,       100, 0,   100, false)]
    [InlineData(SizeOp.LessOrEqual,    100, 0,   100, true)]
    [InlineData(SizeOp.Between,        100, 200, 150, true)]
    [InlineData(SizeOp.Between,        100, 200,  99, false)]
    [InlineData(SizeOp.Between,        100, 200, 201, false)]
    public void SizeRange_Matches_ReturnsExpected(SizeOp op, ulong low, ulong high, ulong size, bool expected)
    {
        new SizeRange(op, low, high).Matches(size).Should().Be(expected);
    }

    // ── combined queries ─────────────────────────────────────────────────

    [Fact]
    public void Parse_ExtAndTerm_BothParsed()
    {
        var q = QueryParser.Parse("ext:cs report");
        q.ExtWhitelist.Should().Equal("cs");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Alternatives.Should().Equal("report");
    }

    [Fact]
    public void Parse_TypeMacroAndNot_BothParsed()
    {
        var q = QueryParser.Parse("audio: !temp");
        q.ExtWhitelist.Should().Contain("mp3");
        q.Clauses.Should().HaveCount(1);
        q.Clauses[0].Negated.Should().BeTrue();
    }

    [Fact]
    public void Parse_SizeAndExt_BothParsed()
    {
        var q = QueryParser.Parse("size:>1mb ext:mp4");
        q.Size.Should().NotBeNull();
        q.ExtWhitelist.Should().Equal("mp4");
        q.Clauses.Should().BeEmpty();
    }
}
