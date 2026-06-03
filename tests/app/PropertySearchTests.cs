using System;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class PropertySearchTests
{
    private const string CoreXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        "<dc:title>Annual Report</dc:title>" +
        "<dc:creator>Jane Doe</dc:creator>" +
        "<dc:subject>Finance</dc:subject>" +
        "<cp:keywords>budget 2026</cp:keywords>" +
        "<dc:description>year end</dc:description>" +
        "</cp:coreProperties>";

    private static string WriteDocx()
    {
        var path = Path.Combine(Path.GetTempPath(), "whereisit-doc-" + Guid.NewGuid().ToString("N") + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("docProps/core.xml");
        using var w = new StreamWriter(entry.Open());
        w.Write(CoreXml);
        return path;
    }

    [Fact]
    public void DocumentProps_ReadsCoreXml()
    {
        var path = WriteDocx();
        try
        {
            DocumentProps.TryRead(path, out var p).Should().BeTrue();
            p.Title.Should().Be("Annual Report");
            p.Author.Should().Be("Jane Doe");
            p.Subject.Should().Be("Finance");
            p.Keywords.Should().Be("budget 2026");
            p.Comment.Should().Be("year end");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MediaProperties_MatchesDocumentFields()
    {
        var path = WriteDocx();
        try
        {
            MediaProperties.Match([new MediaFilter(MediaField.Author, "doe")], path, false, true)
                .Should().BeTrue();
            MediaProperties.Match([new MediaFilter(MediaField.Title, "report")], path, false, true)
                .Should().BeTrue();
            MediaProperties.Match([new MediaFilter(MediaField.Subject, "sport")], path, false, true)
                .Should().BeFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DocumentProps_NonOoxml_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), "whereisit-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "plain");
        try { DocumentProps.TryRead(path, out _).Should().BeFalse(); }
        finally { File.Delete(path); }
    }
}
