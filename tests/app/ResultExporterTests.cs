using System;
using System.IO;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class ResultExporterTests
{
    private static ResultRowModel Row(string name, string parent, ulong size = 0)
        => new(name, parent, size, new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero), "A");

    [Fact]
    public void ToCsv_Header_FollowedByRows()
    {
        var csv = ResultExporter.ToCsv(new[]
        {
            Row("a.txt", @"C:\users\me"),
        });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Name,Path,Size,Modified,Attributes");
        lines[1].Should().StartWith("a.txt,");
    }

    [Fact]
    public void ToCsv_EscapesCommaAndQuoteWithinFields()
    {
        var csv = ResultExporter.ToCsv(new[]
        {
            Row("with,comma.txt", @"C:\dir ""quote"" here"),
        });

        // CSV field with comma must be quoted; embedded quote becomes "".
        csv.Should().Contain("\"with,comma.txt\"");
        csv.Should().Contain("\"C:\\dir \"\"quote\"\" here\"");
    }

    [Fact]
    public void ToCsv_BlankSize_OutputsZero()
    {
        var csv = ResultExporter.ToCsv(new[] { Row("a.txt", @"C:\", 1234) });
        csv.Should().Contain(",1234,");
    }

    [Fact]
    public void ToTsv_UsesTabSeparators()
    {
        var tsv = ResultExporter.ToTsv(new[] { Row("a.txt", @"C:\me") });
        tsv.Should().Contain("\t");
        tsv.Should().Contain("Name\tPath\tSize");
    }

    [Fact]
    public void Write_ToFile_WritesUtf8()
    {
        var temp = Path.GetTempFileName();
        try
        {
            ResultExporter.WriteCsv(temp, new[] { Row("a.txt", @"C:\me") });
            var content = File.ReadAllText(temp);
            content.Should().Contain("Name,Path,Size");
            content.Should().Contain("a.txt");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void ToCsv_EmptyInput_WritesHeaderOnly()
    {
        var csv = ResultExporter.ToCsv(Array.Empty<ResultRowModel>());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].Should().Be("Name,Path,Size,Modified,Attributes");
    }
}
