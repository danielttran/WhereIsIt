using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class ThumbnailCacheTests
{
    private sealed class FakeBitmap
    {
        public string Tag { get; init; } = "";
        public override string ToString() => Tag;
    }

    [Fact]
    public void Miss_OnEmptyCache_ReturnsFalse()
    {
        var c = new ThumbnailCache<FakeBitmap>();
        c.TryGet(@"C:\a.txt", ThumbnailSize.Medium, out var bmp).Should().BeFalse();
        bmp.Should().BeNull();
    }

    [Fact]
    public void Put_Then_Get_ReturnsSameBitmap()
    {
        var c = new ThumbnailCache<FakeBitmap>();
        var b = new FakeBitmap { Tag = "a-med" };
        c.Put(@"C:\a.txt", ThumbnailSize.Medium, b);
        c.TryGet(@"C:\a.txt", ThumbnailSize.Medium, out var got).Should().BeTrue();
        got.Should().BeSameAs(b);
    }

    [Fact]
    public void PathAndSize_AreKeyedSeparately()
    {
        var c = new ThumbnailCache<FakeBitmap>();
        var med = new FakeBitmap { Tag = "med" };
        var lrg = new FakeBitmap { Tag = "lrg" };
        c.Put(@"C:\a.txt", ThumbnailSize.Medium, med);
        c.Put(@"C:\a.txt", ThumbnailSize.Large,  lrg);

        c.TryGet(@"C:\a.txt", ThumbnailSize.Medium, out var m).Should().BeTrue();
        c.TryGet(@"C:\a.txt", ThumbnailSize.Large,  out var l).Should().BeTrue();
        m.Should().BeSameAs(med);
        l.Should().BeSameAs(lrg);
    }

    [Fact]
    public void LruEviction_DropsOldestWhenAtCapacity()
    {
        var c = new ThumbnailCache<FakeBitmap>(capacity: 2);
        c.Put(@"A", ThumbnailSize.Medium, new FakeBitmap { Tag = "A" });
        c.Put(@"B", ThumbnailSize.Medium, new FakeBitmap { Tag = "B" });
        c.Put(@"C", ThumbnailSize.Medium, new FakeBitmap { Tag = "C" });
        c.TryGet(@"A", ThumbnailSize.Medium, out _).Should().BeFalse("A is the oldest, evicted");
        c.TryGet(@"B", ThumbnailSize.Medium, out _).Should().BeTrue();
        c.TryGet(@"C", ThumbnailSize.Medium, out _).Should().BeTrue();
    }

    [Fact]
    public void Get_PromotesEntryToFront_PreventingEviction()
    {
        var c = new ThumbnailCache<FakeBitmap>(capacity: 2);
        c.Put(@"A", ThumbnailSize.Medium, new FakeBitmap { Tag = "A" });
        c.Put(@"B", ThumbnailSize.Medium, new FakeBitmap { Tag = "B" });
        // Touch A → makes A most recent, B oldest.
        c.TryGet(@"A", ThumbnailSize.Medium, out _);
        c.Put(@"C", ThumbnailSize.Medium, new FakeBitmap { Tag = "C" });
        c.TryGet(@"A", ThumbnailSize.Medium, out _).Should().BeTrue();
        c.TryGet(@"B", ThumbnailSize.Medium, out _).Should().BeFalse("B was oldest after A's hit");
    }

    [Fact]
    public void Put_ExistingKey_Replaces()
    {
        var c = new ThumbnailCache<FakeBitmap>();
        var first  = new FakeBitmap { Tag = "first" };
        var second = new FakeBitmap { Tag = "second" };
        c.Put(@"A", ThumbnailSize.Medium, first);
        c.Put(@"A", ThumbnailSize.Medium, second);
        c.TryGet(@"A", ThumbnailSize.Medium, out var got).Should().BeTrue();
        got.Should().BeSameAs(second);
        c.Count.Should().Be(1);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var c = new ThumbnailCache<FakeBitmap>();
        c.Put(@"A", ThumbnailSize.Medium, new FakeBitmap());
        c.Put(@"B", ThumbnailSize.Medium, new FakeBitmap());
        c.Clear();
        c.Count.Should().Be(0);
        c.TryGet(@"A", ThumbnailSize.Medium, out _).Should().BeFalse();
    }
}
