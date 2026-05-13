using FluentAssertions;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests;

public class TabRestoreServiceTests
{
    [Fact]
    public void Snapshot_SingleEmptyTab_ProducesEmptyResult()
    {
        var tabs = new TabsViewModel(); // default = one empty tab
        var snap = TabRestoreService.SnapshotForPersistence(tabs.Tabs);
        snap.Should().BeEmpty("a fresh single empty tab is not worth restoring");
    }

    [Fact]
    public void Snapshot_OneTabWithQuery_PersistsIt()
    {
        var tabs = new TabsViewModel();
        tabs.Tabs[0].Query = "foo";
        TabRestoreService.SnapshotForPersistence(tabs.Tabs)
            .Should().Equal("foo");
    }

    [Fact]
    public void Snapshot_MultipleTabs_PersistsAll()
    {
        var tabs = new TabsViewModel();
        tabs.Tabs[0].Query = "a";
        tabs.AddTab("b");
        tabs.AddTab("");           // empty middle tab still counts as a tab
        TabRestoreService.SnapshotForPersistence(tabs.Tabs)
            .Should().Equal("a", "b", "");
    }

    [Fact]
    public void WorthRestoring_NullOrEmpty_False()
    {
        TabRestoreService.WorthRestoring(null).Should().BeFalse();
        TabRestoreService.WorthRestoring(System.Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void WorthRestoring_SingleEmpty_False()
    {
        TabRestoreService.WorthRestoring(new[] { "" }).Should().BeFalse();
        TabRestoreService.WorthRestoring(new[] { "   " }).Should().BeFalse();
    }

    [Fact]
    public void WorthRestoring_SingleNonEmpty_True()
    {
        TabRestoreService.WorthRestoring(new[] { "foo" }).Should().BeTrue();
    }

    [Fact]
    public void WorthRestoring_MultipleTabs_True_EvenIfAllEmpty()
    {
        // Two visible tabs is itself a workspace state worth restoring.
        TabRestoreService.WorthRestoring(new[] { "", "" }).Should().BeTrue();
    }
}
