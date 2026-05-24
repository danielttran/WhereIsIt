using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

public static class ResultExporter
{
    private static readonly string[] Headers = ["Name", "Path", "Size", "Modified", "Attributes"];

    // Everything File List (.efu) column order — must match voidtools exactly
    // so files round-trip with Everything itself.
    private static readonly string[] EfuHeaders =
        ["Filename", "Size", "Date Modified", "Date Created", "Attributes"];

    public static string ToCsv(IEnumerable<ResultRowModel> rows) => Build(rows, ',');

    public static string ToTsv(IEnumerable<ResultRowModel> rows) => Build(rows, '\t');

    public static void WriteCsv(string path, IEnumerable<ResultRowModel> rows)
        => File.WriteAllText(path, ToCsv(rows), new UTF8Encoding(false));

    public static void WriteTsv(string path, IEnumerable<ResultRowModel> rows)
        => File.WriteAllText(path, ToTsv(rows), new UTF8Encoding(false));

    // ── Everything File List (.efu) ─────────────────────────────────────────

    public static void WriteEfu(string path, IEnumerable<ResultRowModel> rows)
        => File.WriteAllText(path, ToEfu(rows), new UTF8Encoding(false));

    /// <summary>
    /// Serialises rows to the Everything File List format: a CSV whose columns
    /// are <c>Filename,Size,Date Modified,Date Created,Attributes</c>. Filename
    /// is the full path; dates are Win32 FILETIME ticks (100 ns since 1601 UTC);
    /// Attributes is the decimal Win32 attribute mask.
    /// </summary>
    public static string ToEfu(IEnumerable<ResultRowModel> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, ',', EfuHeaders);
        foreach (var r in rows)
        {
            var full = string.IsNullOrEmpty(r.ParentPath)
                ? r.Name : Path.Combine(r.ParentPath, r.Name);
            bool isDir = (r.Attributes ?? string.Empty).Contains('D');
            AppendRow(sb, ',',
            [
                full,
                isDir ? "0" : r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                ToFileTime(r.ModifiedUtc).ToString(CultureInfo.InvariantCulture),
                ToFileTime(r.CreatedUtc).ToString(CultureInfo.InvariantCulture),
                LettersToAttributeMask(r.Attributes).ToString(CultureInfo.InvariantCulture),
            ]);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<ResultRowModel> ReadEfu(string path)
        => ParseEfu(File.ReadAllText(path));

    /// <summary>Parses Everything File List text back into rows. Unknown or
    /// malformed lines are skipped rather than throwing.</summary>
    public static IReadOnlyList<ResultRowModel> ParseEfu(string text)
    {
        var rows = new List<ResultRowModel>();
        using var reader = new StringReader(text);
        string? line;
        bool first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var fields = ParseCsvLine(line);
            if (first)
            {
                first = false;
                // Skip the header row when present; otherwise treat as data.
                if (fields.Count > 0 &&
                    fields[0].Equals("Filename", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (fields.Count < 1) continue;

            var full = fields[0];
            if (string.IsNullOrWhiteSpace(full)) continue;
            ulong size  = fields.Count > 1 && ulong.TryParse(fields[1], out var s) ? s : 0;
            var modified = fields.Count > 2 ? FromFileTime(fields[2]) : default;
            var created  = fields.Count > 3 ? FromFileTime(fields[3]) : default;
            int mask = fields.Count > 4 && int.TryParse(fields[4], out var mk) ? mk : 0;

            var name = Path.GetFileName(full);
            var parent = Path.GetDirectoryName(full) ?? string.Empty;
            if (name.Length == 0) { name = full; parent = string.Empty; }

            rows.Add(new ResultRowModel(
                name, parent, size, modified, MaskToLetters(mask))
            {
                CreatedUtc = created,
            });
        }
        return rows;
    }

    private static long ToFileTime(DateTimeOffset dto)
    {
        if (dto == default) return 0;
        try { return dto.UtcDateTime.ToFileTimeUtc(); }
        catch { return 0; }
    }

    private static DateTimeOffset FromFileTime(string raw)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || ticks <= 0)
            return default;
        try { return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero); }
        catch { return default; }
    }

    private const int FaReadOnly = 0x1, FaHidden = 0x2, FaSystem = 0x4,
                      FaDirectory = 0x10, FaArchive = 0x20;

    private static int LettersToAttributeMask(string? letters)
    {
        int m = 0;
        foreach (var c in letters ?? string.Empty)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'R': m |= FaReadOnly;  break;
                case 'H': m |= FaHidden;    break;
                case 'S': m |= FaSystem;    break;
                case 'A': m |= FaArchive;   break;
                case 'D': m |= FaDirectory; break;
            }
        }
        return m;
    }

    private static string MaskToLetters(int mask)
    {
        var sb = new StringBuilder(5);
        if ((mask & FaReadOnly)  != 0) sb.Append('R');
        if ((mask & FaHidden)    != 0) sb.Append('H');
        if ((mask & FaSystem)    != 0) sb.Append('S');
        if ((mask & FaArchive)   != 0) sb.Append('A');
        if ((mask & FaDirectory) != 0) sb.Append('D');
        return sb.ToString();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string Build(IEnumerable<ResultRowModel> rows, char sep)
    {
        var sb = new StringBuilder();
        AppendRow(sb, sep, Headers);
        foreach (var r in rows)
        {
            // guardFormulas: true — these CSV/TSV files are meant to be opened
            // in spreadsheet apps, so neutralise formula-injection payloads.
            AppendRow(sb, sep,
            [
                r.Name,
                r.ParentPath,
                r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                r.ModifiedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Attributes ?? string.Empty,
            ], guardFormulas: true);
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, char sep, string[] fields, bool guardFormulas = false)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(Escape(fields[i], sep, guardFormulas));
        }
        sb.Append('\n');
    }

    // Characters that a spreadsheet (Excel / Sheets / LibreOffice) treats as the
    // start of a formula when they lead a cell. A filename like "=cmd|'/c calc'!A1"
    // would otherwise execute on open.
    private static bool IsFormulaLead(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    private static string Escape(string s, char sep, bool guardFormulas = false)
    {
        if (s is null) return string.Empty;
        // Defuse CSV formula injection by prefixing an apostrophe so the cell is
        // shown as literal text. Only for human-facing CSV/TSV — the EFU writer
        // passes guardFormulas:false so column 0 (a full path) round-trips
        // byte-for-byte with Everything.
        if (guardFormulas && s.Length > 0 && IsFormulaLead(s[0]))
            s = "'" + s;
        bool quote = s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!quote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
