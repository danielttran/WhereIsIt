using System;
using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class ResultExporterEfuTests
{
    private static ResultRowModel Row(string name, string parent, ulong size,
        string attr = "A")
        => new(name, parent, size,
               new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero), attr)
        {
            CreatedUtc = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

    [Fact]
    public void ToEfu_EmitsVoidtoolsHeader()
    {
        var efu = ResultExporter.ToEfu(new[] { Row("a.txt", @"C:\u", 5) });
        var first = efu.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        first.Should().Be("Filename,Size,Date Modified,Date Created,Attributes");
    }

    [Fact]
    public void ToEfu_UsesFullPath_AndZeroSizeForDirectories()
    {
        var efu = ResultExporter.ToEfu(new[]
        {
            Row("file.txt", @"C:\data", 1234),
            Row("sub",      @"C:\data", 4096, attr: "D"),
        });
        efu.Should().Contain(@"C:\data\file.txt,1234,");
        efu.Should().Contain(@"C:\data\sub,0,");
    }

    [Fact]
    public void EfuRoundTrip_PreservesCoreFields()
    {
        var rows = new[]
        {
            Row("report,v2.txt", @"C:\users\me", 9001),
            Row("logo.png",      @"D:\assets",   42, attr: "RH"),
        };

        var parsed = ResultExporter.ParseEfu(ResultExporter.ToEfu(rows)).ToList();

        parsed.Should().HaveCount(2);

        parsed[0].Name.Should().Be("report,v2.txt");
        parsed[0].ParentPath.Should().Be(@"C:\users\me");
        parsed[0].SizeBytes.Should().Be(9001);
        parsed[0].ModifiedUtc.Should().Be(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        parsed[0].CreatedUtc.Should().Be(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero));

        parsed[1].Name.Should().Be("logo.png");
        parsed[1].ParentPath.Should().Be(@"D:\assets");
        parsed[1].Attributes.Should().Be("RH");
    }

    [Fact]
    public void ParseEfu_ToleratesMissingHeaderAndBlankLines()
    {
        // No header row; a stray blank line in the middle.
        var text = "C:\\x\\a.txt,10,0,0,32\n\nC:\\x\\b.txt,20,0,0,32\n";
        var parsed = ResultExporter.ParseEfu(text);
        parsed.Select(r => r.Name).Should().Equal("a.txt", "b.txt");
        parsed[0].SizeBytes.Should().Be(10);
    }

    [Fact]
    public void ParseEfu_UnknownDates_BecomeDefault()
    {
        var text = "Filename,Size,Date Modified,Date Created,Attributes\nC:\\z.bin,3,0,0,0\n";
        var r = ResultExporter.ParseEfu(text).Single();
        r.ModifiedUtc.Should().Be(default);
        r.CreatedUtc.Should().Be(default);
    }
}
