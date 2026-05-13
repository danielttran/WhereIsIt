using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

public static class ResultExporter
{
    private static readonly string[] Headers = ["Name", "Path", "Size", "Modified", "Attributes"];

    public static string ToCsv(IEnumerable<ResultRowModel> rows) => Build(rows, ',');

    public static string ToTsv(IEnumerable<ResultRowModel> rows) => Build(rows, '\t');

    public static void WriteCsv(string path, IEnumerable<ResultRowModel> rows)
        => File.WriteAllText(path, ToCsv(rows), new UTF8Encoding(false));

    public static void WriteTsv(string path, IEnumerable<ResultRowModel> rows)
        => File.WriteAllText(path, ToTsv(rows), new UTF8Encoding(false));

    private static string Build(IEnumerable<ResultRowModel> rows, char sep)
    {
        var sb = new StringBuilder();
        AppendRow(sb, sep, Headers);
        foreach (var r in rows)
        {
            AppendRow(sb, sep,
            [
                r.Name,
                r.ParentPath,
                r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                r.ModifiedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                r.Attributes ?? string.Empty,
            ]);
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, char sep, string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(Escape(fields[i], sep));
        }
        sb.Append('\n');
    }

    private static string Escape(string s, char sep)
    {
        if (s is null) return string.Empty;
        bool quote = s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!quote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
