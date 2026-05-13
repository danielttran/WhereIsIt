using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

/// <summary>
/// Decorator that adds Everything-grade query parsing on top of any
/// <see cref="IEngineClient"/>. Parses the raw query with <see cref="QueryParser"/>,
/// translates the result into a string the inner engine can use to narrow its
/// candidate set, then post-filters the returned IDs against the full parsed
/// query (extensions, sizes, dates, attributes, path scope, boolean clauses).
/// </summary>
public sealed class FilteringEngineClient : IEngineClient, IDisposable
{
    private const int DisplayCap = 2000;
    private const int MaxScan    = 50_000;
    private const long MaxContentSearchBytes = 4L * 1024 * 1024;   // skip files larger than this for content:

    private readonly IEngineClient inner;
    private readonly Subject<IReadOnlyList<uint>> filteredResults = new();
    private readonly IDisposable resultsSubscription;

    private ParsedQuery currentParsed = ParsedQuery.Empty;
    private long currentSeq;

    public FilteringEngineClient(IEngineClient inner)
    {
        this.inner = inner;
        resultsSubscription = inner.ObserveResults.Subscribe(
            ids => _ = OnIdsAsync(ids, Volatile.Read(ref currentSeq)));
    }

    public IObservable<string> StatusChanges  => inner.StatusChanges;
    public IObservable<int>    MetricsChanges => inner.MetricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => filteredResults;

    public Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        var parsed = QueryParser.Parse(query);
        // Bump the sequence FIRST so any in-flight OnIdsAsync sees a fresh seq
        // and bails before publishing stale results. currentParsed update under
        // the same fence keeps the (seq, parsed) pair consistent for readers.
        Interlocked.Increment(ref currentSeq);
        currentParsed = parsed;
        var simplified = SimplifyForInnerEngine(parsed, query);
        return inner.SearchAsync(simplified, cancellationToken);
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
        => inner.SortAsync(key, descending, cancellationToken);

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
        => inner.GetRowAsync(id, cancellationToken);

    // ── post-filtering ──────────────────────────────────────────────────

    private async Task OnIdsAsync(IReadOnlyList<uint> ids, long mySeq)
    {
        var parsed = currentParsed;
        // Bail if a newer search has already started — the inner engine may
        // emit IDs from the previous query asynchronously after we've moved on.
        if (Volatile.Read(ref currentSeq) != mySeq) return;

        if (!NeedsPostFiltering(parsed))
        {
            if (Volatile.Read(ref currentSeq) == mySeq)
                filteredResults.OnNext(ids);
            return;
        }

        var kept = new List<uint>(Math.Min(ids.Count, DisplayCap));
        List<(uint Id, string NameKey, ulong Size)>? bucketing = parsed.Dupe ? new() : null;
        int scanLimit = Math.Min(ids.Count, MaxScan);
        for (int i = 0; i < scanLimit; i++)
        {
            // Mid-loop cancellation: stop scanning if a newer search arrived.
            if (Volatile.Read(ref currentSeq) != mySeq) return;
            try
            {
                var row = await inner.GetRowAsync(ids[i], default);
                if (!Passes(row, parsed)) continue;
                kept.Add(ids[i]);
                bucketing?.Add((ids[i], row.Name.ToLowerInvariant(), row.SizeBytes));
            }
            catch { /* skip unreadable */ }
            if (!parsed.Dupe && kept.Count >= DisplayCap) break;
        }

        if (parsed.Dupe && bucketing is not null)
        {
            var buckets = new Dictionary<(string, ulong), int>();
            foreach (var entry in bucketing)
            {
                var key = (entry.NameKey, entry.Size);
                buckets[key] = buckets.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            kept = new List<uint>(bucketing.Count);
            foreach (var entry in bucketing)
            {
                if (buckets[(entry.NameKey, entry.Size)] >= 2) kept.Add(entry.Id);
                if (kept.Count >= DisplayCap) break;
            }
        }

        // Final guard before publishing — don't override a fresh result.
        if (Volatile.Read(ref currentSeq) == mySeq)
            filteredResults.OnNext(kept);
    }

    private static bool NeedsPostFiltering(ParsedQuery q)
    {
        if (q.ExtWhitelist is not null) return true;
        if (q.Size is not null) return true;
        if (q.Modified is not null || q.Created is not null || q.Accessed is not null) return true;
        if (q.Attribute is not null) return true;
        if (q.ChildOfPath is not null || q.ParentIsPath is not null) return true;
        if (q.FileOnly || q.FolderOnly) return true;
        if (q.Dupe) return true;
        if (q.ContentSearch is not null) return true;
        // Any negated clause or |-alternatives need post-filtering because the
        // native engine treats those tokens as literal substrings.
        foreach (var c in q.Clauses)
        {
            if (c.Negated) return true;
            if (c.Alternatives.Count > 1) return true;
        }
        return false;
    }

    private static bool Passes(ResultRowModel row, ParsedQuery q)
    {
        var fullPath = string.IsNullOrEmpty(row.ParentPath)
            ? row.Name
            : Path.Combine(row.ParentPath, row.Name);
        var attrLetters = row.Attributes ?? string.Empty;
        bool isDir = attrLetters.Contains('D');

        if (q.FileOnly   &&  isDir) return false;
        if (q.FolderOnly && !isDir) return false;

        if (q.ExtWhitelist is { Length: > 0 })
        {
            if (isDir) return false;
            var ext = Path.GetExtension(row.Name);
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            bool match = false;
            foreach (var a in q.ExtWhitelist)
                if (string.Equals(ext, a, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
            if (!match) return false;
        }

        if (q.Size is not null)
        {
            if (isDir) return false;
            if (!q.Size.Matches(row.SizeBytes)) return false;
        }

        if (q.Attribute is not null)
        {
            FileAttributes liveAttr;
            try { liveAttr = File.GetAttributes(fullPath); }
            catch { return false; }
            if (!q.Attribute.Matches(liveAttr)) return false;
        }

        if (q.Modified is not null && !q.Modified.Matches(row.ModifiedUtc.LocalDateTime)) return false;

        if (q.Created is not null || q.Accessed is not null)
        {
            try
            {
                if (q.Created  is not null && !q.Created.Matches(File.GetCreationTime(fullPath))) return false;
                if (q.Accessed is not null && !q.Accessed.Matches(File.GetLastAccessTime(fullPath))) return false;
            }
            catch { return false; }
        }

        if (q.ChildOfPath is not null)
        {
            var prefix = q.ChildOfPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fp = fullPath.ToLowerInvariant();
            if (!fp.StartsWith(prefix.ToLowerInvariant() + Path.DirectorySeparatorChar,
                               StringComparison.Ordinal)) return false;
        }
        if (q.ParentIsPath is not null)
        {
            var target = q.ParentIsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = (row.ParentPath ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(parent, target, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (q.ContentSearch is not null)
        {
            if (isDir) return false;
            if (row.SizeBytes > MaxContentSearchBytes) return false;
            if (!FileContains(fullPath, q.ContentSearch, q.CaseSensitive)) return false;
        }

        if (q.Clauses.Count == 0) return true;
        var cmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var clause in q.Clauses)
        {
            bool hit = false;
            foreach (var alt in clause.Alternatives)
            {
                if (AlternativeMatches(alt, q, row.Name, fullPath, cmp))
                {
                    hit = true;
                    break;
                }
            }
            if (clause.Negated ? hit : !hit) return false;
        }
        return true;
    }

    private static bool AlternativeMatches(string alt, ParsedQuery q, string name, string fullPath, StringComparison cmp)
    {
        if (q.RegexMode)
        {
            try
            {
                var ropts = (q.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                          | RegexOptions.CultureInvariant;
                return Regex.IsMatch(name, alt, ropts)
                    || (q.MatchPath && Regex.IsMatch(fullPath, alt, ropts));
            }
            catch (ArgumentException) { return false; }
        }
        if (alt.Contains('*') || alt.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(alt).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            var opts = (q.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                     | RegexOptions.CultureInvariant;
            try
            {
                var rx = new Regex(pattern, opts);
                return rx.IsMatch(name) || (q.MatchPath && rx.IsMatch(fullPath));
            }
            catch (ArgumentException) { return false; }
        }
        return name.Contains(alt, cmp) || (q.MatchPath && fullPath.Contains(alt, cmp));
    }

    // ── query simplification (rewrite for the inner engine) ──────────────

    private static string SimplifyForInnerEngine(ParsedQuery q, string rawQuery)
    {
        if (q.IsEmpty) return string.Empty;

        var parts = new List<string>();

        if (q.CaseSensitive) parts.Add("case:true");
        if (q.RegexMode)     parts.Add("regex:true");
        if (q.WholeWord)     parts.Add("word:true");
        if (q.MatchPath)     parts.Add("matchpath:true");

        // Lossy hint to the inner engine — pass the first alternative of each
        // positive clause so it can narrow the candidate set.
        foreach (var clause in q.Clauses)
        {
            if (clause.Negated || clause.Alternatives.Count == 0) continue;
            var alt = clause.Alternatives[0];
            // Modifier-prefixed tokens have already been consumed; if the user
            // typed a clause that itself looks like "ext:cs" (it won't, but defensive),
            // skip it so the inner engine doesn't try to substring-match.
            if (alt.Contains(':')) continue;
            parts.Add(alt);
        }

        // Path scope hint — the native engine has a fast path for "DIR\*"
        // queries (BFS over the children index), so this both narrows and
        // accelerates results.
        if (q.ChildOfPath is not null)
            parts.Add(TrimSep(q.ChildOfPath) + Path.DirectorySeparatorChar + "*");
        else if (q.ParentIsPath is not null)
            parts.Add(TrimSep(q.ParentIsPath) + Path.DirectorySeparatorChar + "*");

        // Single-extension hint — fits nicely into the engine's wildcard syntax.
        if (q.ExtWhitelist is { Length: 1 })
            parts.Add("*." + q.ExtWhitelist[0]);

        return parts.Count == 0 ? "*" : string.Join(' ', parts);
    }

    private static string TrimSep(string p)
        => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool FileContains(string path, string needle, bool caseSensitive)
    {
        try
        {
            // Stream-read in modest chunks; rely on stream-reader's encoding
            // detection. Good enough for text-shaped files; binary will mostly
            // miss harmlessly.
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var buf = new char[64 * 1024];
            var carry = string.Empty;
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                var chunk = carry + new string(buf, 0, read);
                if (chunk.Contains(needle, cmp)) return true;
                // Carry the last (needle.Length - 1) chars so matches that straddle
                // chunk boundaries aren't missed.
                carry = needle.Length > 1 && chunk.Length >= needle.Length - 1
                    ? chunk[^(needle.Length - 1)..]
                    : chunk;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        resultsSubscription.Dispose();
        filteredResults.Dispose();
        if (inner is IDisposable d) d.Dispose();
    }
}
