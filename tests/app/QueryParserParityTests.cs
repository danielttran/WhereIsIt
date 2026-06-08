using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class QueryParserParityTests
{
    [Fact]
    public void StartWith_And_EndWith_Collected()
    {
        var q = QueryParser.Parse("startwith:rep endwith:.txt");
        q.StartsWith.Should().Equal("rep");
        q.EndsWith.Should().Equal(".txt");
    }

    [Fact]
    public void StartsWith_Alias_Works()
    {
        QueryParser.Parse("startswith:foo").StartsWith.Should().Equal("foo");
        QueryParser.Parse("endswith:bar").EndsWith.Should().Equal("bar");
    }

    [Fact]
    public void WholeFilename_AndAlias_Collected()
    {
        QueryParser.Parse("wfn:report.txt").WholeFilename.Should().Equal("report.txt");
        QueryParser.Parse("wholefilename:a*.cs").WholeFilename.Should().Equal("a*.cs");
    }

    [Fact]
    public void Root_And_Empty_Flags()
    {
        var q = QueryParser.Parse("root: empty:");
        q.RootOnly.Should().BeTrue();
        q.EmptyOnly.Should().BeTrue();
        q.IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData("len:5", 5UL, 5UL, SizeOp.Equal)]
    [InlineData("len:>3", 3UL, 0UL, SizeOp.GreaterThan)]
    [InlineData("len:<=8", 8UL, 0UL, SizeOp.LessOrEqual)]
    public void Len_ParsesIntExpression(string query, ulong low, ulong high, SizeOp op)
    {
        var q = QueryParser.Parse(query);
        q.NameLength.Should().NotBeNull();
        q.NameLength!.Op.Should().Be(op);
        q.NameLength.Low.Should().Be(low);
        if (op == SizeOp.Between) q.NameLength.High.Should().Be(high);
    }

    [Fact]
    public void Len_Range()
    {
        var q = QueryParser.Parse("len:4..10");
        q.NameLength!.Op.Should().Be(SizeOp.Between);
        q.NameLength.Low.Should().Be(4);
        q.NameLength.High.Should().Be(10);
    }

    [Fact]
    public void Count_SetsMaxResults()
    {
        QueryParser.Parse("count:25").MaxResults.Should().Be(25);
        QueryParser.Parse("count:abc").MaxResults.Should().BeNull();
    }

    [Theory]
    [InlineData("dupe:", DupeKind.NameSize)]
    [InlineData("dupes:", DupeKind.NameSize)]
    [InlineData("sizedupe:", DupeKind.Size)]
    [InlineData("namepartdupe:", DupeKind.NamePart)]
    [InlineData("attribdupe:", DupeKind.Attrib)]
    public void DupeFamily_Maps(string token, DupeKind expected)
    {
        var q = QueryParser.Parse(token);
        q.DupeMode.Should().Be(expected);
        q.Dupe.Should().Be(expected != DupeKind.None);
    }

    [Fact]
    public void PlainQuery_HasNoNewModifiers()
    {
        var q = QueryParser.Parse("report 2026");
        q.StartsWith.Should().BeNull();
        q.EndsWith.Should().BeNull();
        q.WholeFilename.Should().BeNull();
        q.RootOnly.Should().BeFalse();
        q.EmptyOnly.Should().BeFalse();
        q.NameLength.Should().BeNull();
        q.MaxResults.Should().BeNull();
        q.DupeMode.Should().Be(DupeKind.None);
    }
}
