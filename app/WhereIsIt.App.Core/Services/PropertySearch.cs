using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace WhereIsIt.App.Services;

/// <summary>
/// Reads document core properties (author/title/subject/keywords/comment) from
/// Office Open XML files (.docx/.xlsx/.pptx and macro variants) — they're ZIP
/// containers with a <c>docProps/core.xml</c> Dublin-Core document. Used by the
/// document side of the property search functions.
/// </summary>
public sealed class DocumentProps
{
    public string? Title { get; private set; }
    public string? Author { get; private set; }
    public string? Subject { get; private set; }
    public string? Keywords { get; private set; }
    public string? Comment { get; private set; }

    public string? Get(MediaField f) => f switch
    {
        MediaField.Title => Title,
        MediaField.Author => Author,
        MediaField.Subject => Subject,
        MediaField.Keywords => Keywords,
        MediaField.Comment => Comment,
        _ => null,
    };

    private static readonly HashSet<string> OoxmlExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm",
    };

    public static bool TryRead(string path, out DocumentProps props)
    {
        props = new DocumentProps();
        try
        {
            if (!OoxmlExt.Contains(Path.GetExtension(path))) return false;
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("docProps/core.xml");
            if (entry is null) return false;
            using var s = entry.Open();
            var doc = XDocument.Load(s);

            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";

            props.Title = First(doc, dc + "title");
            props.Author = First(doc, dc + "creator");
            props.Subject = First(doc, dc + "subject");
            props.Comment = First(doc, dc + "description");
            props.Keywords = First(doc, cp + "keywords");

            return props.Title is not null || props.Author is not null || props.Subject is not null
                || props.Comment is not null || props.Keywords is not null;
        }
        catch { return false; }
    }

    private static string? First(XDocument doc, XName name)
    {
        var v = doc.Descendants(name).FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}

/// <summary>
/// Unified property matcher for the media/document search functions. A field is
/// resolved from the audio tags first, then the document core properties, so a
/// single <c>title:</c>/<c>comment:</c> works across music and Office files.
/// </summary>
public static class MediaProperties
{
    public static bool Match(IReadOnlyList<MediaFilter> filters, string path,
        bool caseSensitive, bool matchDiacritics)
    {
        AudioTags.TryRead(path, out var audio);   // out is always non-null
        DocumentProps? docs = null;
        bool docsTried = false;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var mf in filters)
        {
            string? val = audio.Get(mf.Field);
            if (val is null)
            {
                if (!docsTried) { DocumentProps.TryRead(path, out docs); docsTried = true; }
                val = docs?.Get(mf.Field);
            }
            if (val is null) return false;

            var hay = matchDiacritics ? val : QueryParser.RemoveDiacritics(val);
            var needle = matchDiacritics ? mf.Value : QueryParser.RemoveDiacritics(mf.Value);
            if (!hay.Contains(needle, cmp)) return false;
        }
        return true;
    }
}
