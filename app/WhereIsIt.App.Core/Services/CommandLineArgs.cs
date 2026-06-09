using System.Collections.Generic;

namespace WhereIsIt.App.Services;

public sealed class CommandLineArgs
{
    public string? Query     { get; init; }
    public string? ScopeRoot { get; init; }

    /// <summary>True when --headless was supplied. The app should not show a
    /// window — it runs the query, prints results, and exits with 0/non-zero.</summary>
    public bool Headless { get; init; }

    /// <summary>Result-row cap printed in headless mode. 0 = print everything
    /// the engine returns (still bounded by the engine's own display cap).</summary>
    public int MaxResults { get; init; }

    /// <summary>Print only the file name, not the full path. Mirrors `es.exe`.</summary>
    public bool NameOnly { get; init; }

    /// <summary>Headless timeout in seconds — how long to wait for results to
    /// stabilise before printing. Defaults to 10 s; tune up for large drives.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>--minimized starts the window hidden in the tray. Combined with
    /// --start-on-launch this is the "dock at taskbar" idiom Everything uses
    /// so the engine warms its index without stealing focus on every login.</summary>
    public bool StartMinimised { get; init; }

    /// <summary>Optional explicit output file for headless mode. WinExe binaries
    /// don't reliably keep stdout attached when launched from PowerShell `&`, so
    /// this flag is the canonical way to capture headless results from scripts:
    /// `WhereIsIt.App.exe --headless --query "*.md" --output results.txt`.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Force-enable the localhost HTTP search server for this launch
    /// (independent of <c>settings.json</c>). Lets the audit harness query the
    /// running engine without poking the settings file. Always 127.0.0.1.</summary>
    public bool EnableHttp { get; init; }

    public static CommandLineArgs Parse(string[] args)
    {
        string? query = null;
        string? scope = null;
        string? outputPath = null;
        bool   headless = false;
        bool   nameOnly = false;
        bool   startMin = false;
        bool   enableHttp = false;
        int    maxResults = 0;
        int    timeout = 10;
        var bare = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-s":
                case "--search":
                case "-q":
                case "--query":
                    if (i + 1 < args.Length) query = args[++i];
                    break;
                case "-p":
                case "--path":
                    if (i + 1 < args.Length) scope = args[++i];
                    break;
                case "--headless":
                case "--no-ui":
                    headless = true;
                    break;
                case "--name-only":
                case "-n":
                    nameOnly = true;
                    break;
                case "--max":
                case "--max-results":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var m)) maxResults = m;
                    break;
                case "--timeout":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var t)) timeout = t;
                    break;
                case "--minimized":
                case "--minimised":
                    startMin = true;
                    break;
                case "--output":
                case "-o":
                    if (i + 1 < args.Length) outputPath = args[++i];
                    break;
                case "--enable-http":
                    enableHttp = true;
                    break;
                default:
                    bare.Add(a);
                    break;
            }
        }

        if (query is null && bare.Count > 0)
            query = string.Join(' ', bare);

        return new CommandLineArgs
        {
            Query = query,
            ScopeRoot = scope,
            Headless = headless,
            MaxResults = maxResults,
            NameOnly = nameOnly,
            TimeoutSeconds = timeout > 0 ? timeout : 10,
            StartMinimised = startMin,
            OutputPath = outputPath,
            EnableHttp = enableHttp,
        };
    }
}
