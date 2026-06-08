using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;

// es — a command-line search front-end for WhereIsIt, modelled on voidtools'
// es.exe. It runs a one-shot search through the same engine + query parser the
// GUI uses and prints results to stdout. Unlike Everything's es.exe it does not
// talk to a running instance over IPC; it searches directly (in-proc), so the
// full Everything-style query syntax works but results are computed fresh.

var options = EsOptions.Parse(args);
if (options.ShowHelp)
{
    EsOptions.PrintHelp();
    return 0;
}
if (string.IsNullOrWhiteSpace(options.Query))
{
    Console.Error.WriteLine("es: no search specified. Use 'es -h' for help.");
    return 1;
}

string[]? scope = null;
try
{
    var settings = new AppSettingsService().Load();
    if (settings.ScopeRoots.Length > 0) scope = settings.ScopeRoots;
}
catch { /* settings are optional; fall back to default roots */ }

using var inner = scope is null
    ? new InProcEngineClient()
    : new InProcEngineClient(() => scope);
using var engine = new FilteringEngineClient(inner);

var resultsReady = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
using var sub = engine.ObserveResults.Subscribe(ids => resultsReady.TrySetResult(ids));

await engine.SearchAsync(options.Query, CancellationToken.None);

var completed = await Task.WhenAny(resultsReady.Task, Task.Delay(TimeSpan.FromSeconds(60)));
IReadOnlyList<uint> ids = completed == resultsReady.Task ? resultsReady.Task.Result : Array.Empty<uint>();

var rows = new List<ResultRowModel>(ids.Count);
foreach (var id in ids)
    rows.Add(await engine.GetRowAsync(id, CancellationToken.None));

rows = SortRows(rows, options.Sort, options.Descending);
if (options.MaxResults is int max && max >= 0 && rows.Count > max)
    rows = rows.Take(max).ToList();

WriteOutput(rows, options);
return 0;

// ── output ──────────────────────────────────────────────────────────────

static List<ResultRowModel> SortRows(List<ResultRowModel> rows, string key, bool desc)
{
    Comparison<ResultRowModel> cmp = key switch
    {
        "size" => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
        "date-modified" or "modified" or "dm" => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
        "date-created" or "created" or "dc" => (a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc),
        "path" => (a, b) => string.Compare(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase),
        "extension" or "ext" => (a, b) => string.Compare(
            Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
        _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
    };
    rows.Sort((a, b) =>
    {
        int c = cmp(a, b);
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return desc ? -c : c;
    });
    return rows;
}

static string FullPath(ResultRowModel r)
    => string.IsNullOrEmpty(r.ParentPath) ? r.Name : Path.Combine(r.ParentPath, r.Name);

static void WriteOutput(List<ResultRowModel> rows, EsOptions o)
{
    switch (o.Export)
    {
        case ExportFormat.Csv when o.ExportPath is not null:
            ResultExporter.WriteCsv(o.ExportPath, rows); return;
        case ExportFormat.Tsv when o.ExportPath is not null:
            ResultExporter.WriteTsv(o.ExportPath, rows); return;
        case ExportFormat.Efu when o.ExportPath is not null:
            ResultExporter.WriteEfu(o.ExportPath, rows); return;
        case ExportFormat.Csv: Console.Out.Write(ResultExporter.ToCsv(rows)); return;
        case ExportFormat.Tsv: Console.Out.Write(ResultExporter.ToTsv(rows)); return;
        case ExportFormat.Efu: Console.Out.Write(ResultExporter.ToEfu(rows)); return;
    }

    foreach (var r in rows)
    {
        if (o.NameOnly) Console.Out.WriteLine(r.Name);
        else if (o.ShowSize)
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,15}  {1}", r.SizeBytes, FullPath(r)));
        else Console.Out.WriteLine(FullPath(r));
    }
}

enum ExportFormat { None, Csv, Tsv, Efu }

sealed class EsOptions
{
    public string Query { get; private set; } = string.Empty;
    public int? MaxResults { get; private set; }
    public string Sort { get; private set; } = "name";
    public bool Descending { get; private set; }
    public bool NameOnly { get; private set; }
    public bool ShowSize { get; private set; }
    public ExportFormat Export { get; private set; }
    public string? ExportPath { get; private set; }
    public bool ShowHelp { get; private set; }

    public static EsOptions Parse(string[] args)
    {
        var o = new EsOptions();
        var terms = new List<string>();
        var modifiers = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
                case "-n" or "-max-results": if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) o.MaxResults = n; break;
                case "-sort" or "-s": if (i + 1 < args.Length) o.Sort = args[++i].ToLowerInvariant(); break;
                case "-sort-ascending": o.Descending = false; break;
                case "-sort-descending": o.Descending = true; break;
                case "-name" or "-filename-column": o.NameOnly = true; break;
                case "-size": o.ShowSize = true; break;
                case "-csv": o.Export = ExportFormat.Csv; break;
                case "-tsv": o.Export = ExportFormat.Tsv; break;
                case "-efu": o.Export = ExportFormat.Efu; break;
                case "-export-csv": o.Export = ExportFormat.Csv; if (i + 1 < args.Length) o.ExportPath = args[++i]; break;
                case "-export-tsv": o.Export = ExportFormat.Tsv; if (i + 1 < args.Length) o.ExportPath = args[++i]; break;
                case "-export-efu": o.Export = ExportFormat.Efu; if (i + 1 < args.Length) o.ExportPath = args[++i]; break;
                case "-case" or "-i": modifiers.Add("case:"); break;
                case "-regex" or "-r": modifiers.Add("regex:"); break;
                case "-w" or "-ww" or "-whole-word" or "-whole-words": modifiers.Add("wholeword:"); break;
                case "-p" or "-match-path": modifiers.Add("matchpath:"); break;
                case "-a" or "-diacritics" or "-match-diacritics": modifiers.Add("diacritics:"); break;
                case "-nd" or "-no-diacritics": modifiers.Add("nodiacritics:"); break;
                default: terms.Add(a); break;
            }
        }

        // Compose modifiers + terms into a single engine query string.
        modifiers.AddRange(terms);
        o.Query = string.Join(' ', modifiers);
        return o;
    }

    public static void PrintHelp()
    {
        Console.Out.WriteLine(
            """
            es — command-line search for WhereIsIt (Everything-compatible syntax).

            Usage: es [options] <search>

            Options:
              -n, -max-results <N>     Limit to the first N results.
              -s, -sort <key>          Sort by name|path|size|date-modified|date-created|extension.
              -sort-ascending|-sort-descending
              -name                    Print file names only (no path).
              -size                    Prefix each line with the size in bytes.
              -csv | -tsv | -efu       Write results to stdout in that format.
              -export-csv|-export-tsv|-export-efu <file>   Write results to a file.
              -case, -regex, -w, -p, -a, -nd   Search modifiers (case/regex/whole-word/
                                       match-path/match-diacritics/no-diacritics).
              -h, --help               Show this help.

            The <search> accepts the full WhereIsIt/Everything query syntax, e.g.
              es ext:cs size:>1mb dm:thisweek
              es "<report log>|<temp data>"
              es artist:beatles -sort size -n 50
            """);
    }
}
