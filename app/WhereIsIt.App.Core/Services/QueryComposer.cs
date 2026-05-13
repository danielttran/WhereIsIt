using System;
using System.Collections.Generic;
using System.Linq;

namespace WhereIsIt.App.Services;

/// <summary>
/// Pure helpers for composing the query string from the quick-filter bar.
/// Filters in the <see cref="TypeFilters"/> set are mutually exclusive — applying
/// a new one drops the previous one. <see cref="AllFilter"/> means "no filter".
/// </summary>
public static class QueryComposer
{
    public const string AllFilter = "all:";

    public static readonly string[] TypeFilters =
        ["audio:", "video:", "doc:", "pic:", "exe:", "zip:", "code:", "folder:", "file:"];

    public static string ApplyFilter(string currentQuery, string filter)
    {
        var tokens = (currentQuery ?? string.Empty).Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var kept = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (IsTypeFilter(t)) continue;
            kept.Add(t);
        }

        if (!string.Equals(filter, AllFilter, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(filter))
        {
            kept.Add(filter);
        }

        return string.Join(' ', kept);
    }

    public static string ActiveFilter(string currentQuery)
    {
        if (string.IsNullOrWhiteSpace(currentQuery)) return AllFilter;
        var tokens = currentQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var match = tokens.FirstOrDefault(IsTypeFilter);
        return match is null ? AllFilter : NormaliseTypeFilter(match);
    }

    private static bool IsTypeFilter(string token)
    {
        foreach (var f in TypeFilters)
            if (token.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string NormaliseTypeFilter(string token)
    {
        foreach (var f in TypeFilters)
            if (token.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return f;
        return token;
    }
}
