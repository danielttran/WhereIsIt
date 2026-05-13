using System.Collections.Generic;

namespace WhereIsIt.App.Services;

public sealed class CommandLineArgs
{
    public string? Query     { get; init; }
    public string? ScopeRoot { get; init; }

    public static CommandLineArgs Parse(string[] args)
    {
        string? query = null;
        string? scope = null;
        var bare = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-s":
                case "--search":
                    if (i + 1 < args.Length) query = args[++i];
                    break;
                case "-p":
                case "--path":
                    if (i + 1 < args.Length) scope = args[++i];
                    break;
                default:
                    bare.Add(a);
                    break;
            }
        }

        if (query is null && bare.Count > 0)
            query = string.Join(' ', bare);

        return new CommandLineArgs { Query = query, ScopeRoot = scope };
    }
}
