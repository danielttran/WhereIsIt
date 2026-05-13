using System.Collections.Generic;
using System.Linq;
using WhereIsIt.App.ViewModels;

namespace WhereIsIt.App.Services;

/// <summary>
/// Decides what to persist on session end and what to restore on session
/// start, following Chrome's "Continue where you left off" behaviour: a
/// session with just one empty tab is not worth prompting about.
/// </summary>
public static class TabRestoreService
{
    /// <summary>Returns the tab queries to persist for the next launch.
    /// Returns an empty array when there is nothing worth restoring.</summary>
    public static string[] SnapshotForPersistence(IEnumerable<TabRecord> openTabs)
    {
        if (openTabs is null) return [];
        var queries = openTabs
            .Where(t => t is not null)
            .Select(t => t.Query ?? string.Empty)
            .ToArray();
        return WorthRestoring(queries) ? queries : [];
    }

    /// <summary>True when the last session left at least one non-empty tab
    /// (or more than one tab in total), i.e. there's something to restore.</summary>
    public static bool WorthRestoring(string[]? lastSession)
    {
        if (lastSession is null || lastSession.Length == 0) return false;
        if (lastSession.Length == 1 && string.IsNullOrWhiteSpace(lastSession[0])) return false;
        return true;
    }
}
