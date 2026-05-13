using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class SearchModifiersComposerTests
{
    [Fact]
    public void Apply_NoModifiers_LeavesQueryUntouched()
    {
        SearchModifiersComposer.Apply("foo", default).Should().Be("foo");
    }

    [Fact]
    public void Apply_CaseSensitive_PrependsCaseToken()
    {
        SearchModifiersComposer.Apply("foo", new SearchModifiers(CaseSensitive: true))
            .Should().Be("case: foo");
    }

    [Fact]
    public void Apply_Regex_PrependsRegexToken()
    {
        SearchModifiersComposer.Apply("foo", new SearchModifiers(Regex: true))
            .Should().Be("regex: foo");
    }

    [Fact]
    public void Apply_All_PrependsAllTokensInStableOrder()
    {
        SearchModifiersComposer.Apply("foo",
            new SearchModifiers(CaseSensitive: true, Regex: true, WholeWord: true, MatchPath: true))
            .Should().Be("case: regex: word: path: foo");
    }

    [Fact]
    public void Apply_StripsExistingModifiersBeforeRewriting()
    {
        SearchModifiersComposer.Apply("case: foo bar", new SearchModifiers(Regex: true))
            .Should().Be("regex: foo bar");
    }

    [Fact]
    public void Detect_FindsModifiersInQuery()
    {
        var m = SearchModifiersComposer.Detect("regex: case: foo");
        m.CaseSensitive.Should().BeTrue();
        m.Regex.Should().BeTrue();
        m.WholeWord.Should().BeFalse();
        m.MatchPath.Should().BeFalse();
    }

    [Fact]
    public void StripModifiers_RemovesAllKnownPrefixes()
    {
        SearchModifiersComposer.StripModifiers("case: word: regex: path: foo bar")
            .Should().Be("foo bar");
    }
}
