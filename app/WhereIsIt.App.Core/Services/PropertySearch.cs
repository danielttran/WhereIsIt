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
            var ext = Path.GetExtension(path);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return props.ReadPdf(path);
            if (!OoxmlExt.Contains(ext)) return false;
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

    // ── PDF /Info dictionary (best-effort heuristic) ─────────────────────

    private bool ReadPdf(string path)
    {
        // Best-effort: scan the raw bytes for the document-info keys and the
        // string value that follows. Catches PDFs whose /Info dictionary isn't
        // inside a compressed object stream (the common case for the title /
        // author fields); XMP-only or fully-compressed metadata is missed.
        const int Cap = 8 * 1024 * 1024;
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int len = (int)Math.Min(fs.Length, Cap);
            bytes = new byte[len];
            int read = 0;
            while (read < len) { int n = fs.Read(bytes, read, len - read); if (n <= 0) break; read += n; }
            if (read < 8) return false;
        }
        // Latin1 preserves a 1:1 byte→char mapping so indices stay byte-accurate.
        var text = System.Text.Encoding.Latin1.GetString(bytes);

        Title = PdfValue(text, "/Title");
        Author = PdfValue(text, "/Author");
        Subject = PdfValue(text, "/Subject");
        Keywords = PdfValue(text, "/Keywords");
        return Title is not null || Author is not null || Subject is not null || Keywords is not null;
    }

    private static string? PdfValue(string text, string key)
    {
        int k = text.IndexOf(key, StringComparison.Ordinal);
        while (k >= 0)
        {
            int i = k + key.Length;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
            if (i < text.Length && text[i] == '(') return PdfLiteral(text, i + 1);
            if (i < text.Length && text[i] == '<') return PdfHex(text, i + 1);
            // Not a string value here (e.g. /Title in a content stream) — keep looking.
            k = text.IndexOf(key, k + key.Length, StringComparison.Ordinal);
        }
        return null;
    }

    private static string? PdfLiteral(string text, int start)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char n = text[++i];
                sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => n });
            }
            else if (c == '(') { depth++; sb.Append(c); }
            else if (c == ')') { if (depth == 0) break; depth--; sb.Append(c); }
            else sb.Append(c);
        }
        return Finish(DecodePdfText(sb.ToString()));
    }

    private static string? PdfHex(string text, int start)
    {
        var hex = new System.Text.StringBuilder();
        for (int i = start; i < text.Length && text[i] != '>'; i++)
            if (Uri.IsHexDigit(text[i])) hex.Append(text[i]);
        if (hex.Length == 0) return null;
        if (hex.Length % 2 == 1) hex.Append('0');
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.ToString(i * 2, 2), 16);
        return Finish(DecodePdfBytes(bytes));
    }

    private static string DecodePdfText(string s)
    {
        // A literal string beginning with the UTF-16BE BOM (þÿ in Latin1) is UTF-16.
        if (s.Length >= 2 && s[0] == 'þ' && s[1] == 'ÿ')
        {
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        return s;
    }

    private static string DecodePdfBytes(byte[] b)
        => b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF
            ? System.Text.Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
            : System.Text.Encoding.Latin1.GetString(b);

    private static string? Finish(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? null : s;
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
