using System.Collections.Generic;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class RunCountServiceTests
{
    [Fact]
    public void Increment_FirstHit_StartsAtOne()
    {
        var svc = new RunCountService();
        svc.Increment(@"C:\file.txt");
        svc.Get(@"C:\file.txt").Should().Be(1);
    }

    [Fact]
    public void Increment_MultipleHits_Accumulates()
    {
        var svc = new RunCountService();
        svc.Increment(@"C:\file.txt");
        svc.Increment(@"C:\file.txt");
        svc.Increment(@"C:\file.txt");
        svc.Get(@"C:\file.txt").Should().Be(3);
    }

    [Fact]
    public void Get_UnknownPath_ReturnsZero()
    {
        new RunCountService().Get(@"C:\unknown").Should().Be(0);
    }

    [Fact]
    public void PathLookup_IsCaseInsensitive()
    {
        var svc = new RunCountService();
        svc.Increment(@"C:\Foo.txt");
        svc.Get(@"c:\foo.txt").Should().Be(1);
        svc.Get(@"C:\FOO.TXT").Should().Be(1);
    }

    [Fact]
    public void Load_PopulatesFromDictionary()
    {
        var svc = new RunCountService();
        svc.Load(new Dictionary<string, int>
        {
            { @"C:\one.txt", 5 },
            { @"C:\two.txt", 2 },
        });
        svc.Get(@"C:\one.txt").Should().Be(5);
        svc.Get(@"C:\two.txt").Should().Be(2);
    }

    [Fact]
    public void Snapshot_ReturnsCurrentCounts()
    {
        var svc = new RunCountService();
        svc.Increment(@"C:\a"); svc.Increment(@"C:\a"); svc.Increment(@"C:\b");
        var snap = svc.Snapshot();
        snap.Should().ContainKey(@"c:\a").WhoseValue.Should().Be(2);
        snap.Should().ContainKey(@"c:\b").WhoseValue.Should().Be(1);
    }
}
