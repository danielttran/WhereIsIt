using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class BookmarkServiceTests
{
    [Fact]
    public void Add_NewBookmark_AppearsInList()
    {
        var svc = new BookmarkService();
        svc.Add("My CS files", "ext:cs");
        svc.Items.Should().HaveCount(1);
        svc.Items[0].Name.Should().Be("My CS files");
        svc.Items[0].Query.Should().Be("ext:cs");
    }

    [Fact]
    public void Add_DuplicateName_Overwrites()
    {
        var svc = new BookmarkService();
        svc.Add("Code", "ext:cs");
        svc.Add("Code", "ext:cs;py");
        svc.Items.Should().HaveCount(1);
        svc.Items[0].Query.Should().Be("ext:cs;py");
    }

    [Fact]
    public void Remove_ExistingBookmark_RemovesIt()
    {
        var svc = new BookmarkService();
        svc.Add("A", "qa");
        svc.Add("B", "qb");
        svc.Remove("A");
        svc.Items.Should().HaveCount(1);
        svc.Items[0].Name.Should().Be("B");
    }

    [Fact]
    public void Remove_Missing_NoOp()
    {
        var svc = new BookmarkService();
        svc.Add("A", "qa");
        svc.Remove("Z");
        svc.Items.Should().HaveCount(1);
    }

    [Fact]
    public void Add_WithEmptyName_Ignored()
    {
        var svc = new BookmarkService();
        svc.Add("", "qa");
        svc.Add("   ", "qa");
        svc.Items.Should().BeEmpty();
    }

    [Fact]
    public void Load_ReplacesAllEntries()
    {
        var svc = new BookmarkService();
        svc.Add("A", "qa");
        svc.Load(new[]
        {
            new Bookmark("X", "qx"),
            new Bookmark("Y", "qy"),
        });
        svc.Items.Select(i => i.Name).Should().Equal("X", "Y");
    }

    [Fact]
    public void Snapshot_ReturnsImmutableCopy()
    {
        var svc = new BookmarkService();
        svc.Add("A", "qa");
        var snap = svc.Snapshot();
        svc.Add("B", "qb");
        snap.Should().HaveCount(1);
    }
}
