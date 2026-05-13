using System;
using System.Collections.Generic;

namespace WhereIsIt.App.Services;

public readonly record struct SearchModifiers(
    bool CaseSensitive = false,
    bool Regex         = false,
    bool WholeWord     = false,
    bool MatchPath     = false);

/// <summary>
/// Pure helpers that translate the boolean toggle state on the UI's modifier
/// buttons into the leading <c>case:</c>/<c>regex:</c>/<c>word:</c>/<c>path:</c>
/// tokens that <see cref="QueryParser"/> already understands.
/// </summary>
public static class SearchModifiersComposer
{
    // Ordered for deterministic output. Each entry is the bare keyword token
    // (without value); QueryParser accepts each as a standalone flag.
    private static readonly (string Token, Func<SearchModifiers, bool> Selector)[] Map =
    [
        ("case:",  m => m.CaseSensitive),
        ("regex:", m => m.Regex),
        ("word:",  m => m.WholeWord),
        ("path:",  m => m.MatchPath),
    ];

    public static string Apply(string query, SearchModifiers modifiers)
    {
        var stripped = StripModifiers(query ?? string.Empty);
        var prefixTokens = new List<string>(Map.Length);
        foreach (var (tok, sel) in Map)
            if (sel(modifiers)) prefixTokens.Add(tok);

        if (prefixTokens.Count == 0) return stripped;
        return string.IsNullOrEmpty(stripped)
            ? string.Join(' ', prefixTokens)
            : string.Join(' ', prefixTokens) + " " + stripped;
    }

    public static SearchModifiers Detect(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return default;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool caseS = false, regex = false, ww = false, mp = false;
        foreach (var t in tokens)
        {
            var lower = t.ToLowerInvariant();
            if (lower == "case:") caseS = true;
            else if (lower == "regex:") regex = true;
            else if (lower == "word:" || lower == "ww:") ww = true;
            else if (lower == "path:" || lower == "matchpath:") mp = true;
        }
        return new SearchModifiers(caseS, regex, ww, mp);
    }

    public static string StripModifiers(string query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            var lower = t.ToLowerInvariant();
            if (lower is "case:" or "regex:" or "word:" or "ww:" or "path:" or "matchpath:")
                continue;
            kept.Add(t);
        }
        return string.Join(' ', kept);
    }
}
