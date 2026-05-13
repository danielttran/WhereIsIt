using FluentAssertions;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests;

public class TabsViewModelTests
{
    [Fact]
    public void Default_OneTab_WithEmptyQuery()
    {
        var tabs = new TabsViewModel();
        tabs.Tabs.Should().HaveCount(1);
        tabs.Tabs[0].Query.Should().BeEmpty();
        tabs.CurrentTab.Should().BeSameAs(tabs.Tabs[0]);
    }

    [Fact]
    public void AddTab_AppendsAndSelects()
    {
        var tabs = new TabsViewModel();
        var newTab = tabs.AddTab("foo");
        tabs.Tabs.Should().HaveCount(2);
        tabs.CurrentTab.Should().BeSameAs(newTab);
        newTab.Query.Should().Be("foo");
    }

    [Fact]
    public void CloseTab_LastRemainingTabIsNotClosed()
    {
        var tabs = new TabsViewModel();
        tabs.CloseTab(tabs.Tabs[0]);
        tabs.Tabs.Should().HaveCount(1, "the last tab is preserved");
    }

    [Fact]
    public void CloseTab_NonCurrent_KeepsCurrent()
    {
        var tabs = new TabsViewModel();
        var a = tabs.Tabs[0];
        var b = tabs.AddTab("b");
        // current is now b; close a
        tabs.CloseTab(a);
        tabs.Tabs.Should().HaveCount(1);
        tabs.CurrentTab.Should().BeSameAs(b);
    }

    [Fact]
    public void CloseTab_Current_SelectsNeighbour()
    {
        var tabs = new TabsViewModel();
        var a = tabs.Tabs[0];
        var b = tabs.AddTab("b");
        var c = tabs.AddTab("c");
        // current = c, close c, neighbour b should become current
        tabs.CloseTab(c);
        tabs.CurrentTab.Should().BeSameAs(b);
    }

    [Fact]
    public void Title_DerivesFromQueryOrFallback()
    {
        var t = new TabRecord();
        t.Title.Should().Be("(new)");
        t.Query = "report";
        t.Title.Should().Be("report");
    }
}
