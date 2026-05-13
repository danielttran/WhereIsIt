using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class SearchHistoryTests
{
    [Fact]
    public void Empty_RecallPrev_ReturnsNull()
    {
        var h = new SearchHistory();
        h.RecallPrev().Should().BeNull();
        h.RecallNext().Should().BeNull();
    }

    [Fact]
    public void Add_Then_RecallPrev_ReturnsLastEntry()
    {
        var h = new SearchHistory();
        h.Add("foo");
        h.RecallPrev().Should().Be("foo");
    }

    [Fact]
    public void RecallPrev_CyclesFromNewestToOldest()
    {
        var h = new SearchHistory();
        h.Add("oldest");
        h.Add("middle");
        h.Add("newest");

        h.RecallPrev().Should().Be("newest");
        h.RecallPrev().Should().Be("middle");
        h.RecallPrev().Should().Be("oldest");
        h.RecallPrev().Should().Be("oldest"); // clamps at oldest
    }

    [Fact]
    public void RecallNext_MovesTowardNewest()
    {
        var h = new SearchHistory();
        h.Add("a"); h.Add("b"); h.Add("c");

        h.RecallPrev().Should().Be("c");
        h.RecallPrev().Should().Be("b");
        h.RecallNext().Should().Be("c");
        h.RecallNext().Should().BeNull(); // past newest -> back to empty
    }

    [Fact]
    public void Add_Dedupes_ConsecutiveDuplicates()
    {
        var h = new SearchHistory();
        h.Add("foo");
        h.Add("foo");
        h.Add("foo");

        h.RecallPrev().Should().Be("foo");
        h.RecallPrev().Should().Be("foo"); // single entry, clamps
    }

    [Fact]
    public void Add_MovesExistingEntryToTop_NoDuplicates()
    {
        var h = new SearchHistory();
        h.Add("a");
        h.Add("b");
        h.Add("a"); // re-adding should move "a" to top, not duplicate

        h.RecallPrev().Should().Be("a");
        h.RecallPrev().Should().Be("b");
        h.RecallPrev().Should().Be("b"); // only two entries
    }

    [Fact]
    public void Add_CapsAtMaxEntries()
    {
        var h = new SearchHistory(maxEntries: 3);
        h.Add("a"); h.Add("b"); h.Add("c"); h.Add("d");

        h.Entries.Should().HaveCount(3);
        h.Entries.Should().Equal("d", "c", "b"); // newest first
    }

    [Fact]
    public void Add_IgnoresNullOrWhitespace()
    {
        var h = new SearchHistory();
        h.Add(null!);
        h.Add("");
        h.Add("   ");

        h.RecallPrev().Should().BeNull();
        h.Entries.Should().BeEmpty();
    }

    [Fact]
    public void ResetCursor_AfterRecall_NextAddStartsFresh()
    {
        var h = new SearchHistory();
        h.Add("a"); h.Add("b");
        h.RecallPrev().Should().Be("b");
        h.RecallPrev().Should().Be("a");

        h.ResetCursor();
        h.RecallPrev().Should().Be("b");
    }

    [Fact]
    public void LoadFromArray_PopulatesEntries_NewestFirst()
    {
        var h = new SearchHistory();
        h.Load(new[] { "x", "y", "z" });

        h.Entries.Should().Equal("x", "y", "z");
        h.RecallPrev().Should().Be("x");
    }
}
