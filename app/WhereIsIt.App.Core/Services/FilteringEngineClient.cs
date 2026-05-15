using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Subjects;
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
    // Bounded so a single keystroke can't pay an unbounded post-filter cost.
    // The seq fence guarantees stale work bails on the next search, but a
    // *late* keystroke still pays full MaxScan latency; keep that latency tight.
    private const int MaxScan    = 5_000;
    private const long MaxContentSearchBytes = 4L * 1024 * 1024;   // skip files larger than this for content:

    private readonly IEngineClient inner;
    private readonly Subject<IReadOnlyList<uint>> filteredResults = new();
    private readonly IDisposable resultsSubscription;

    // Per-parse derived state, computed once in SearchAsync and read on the
    // per-row hot path. Computing these inside Passes() would allocate one
    // lowercased path/prefix per row (DisplayCap = 2000) on every keystroke.
    private sealed class CompiledQuery
    {
        public ParsedQuery Parsed = ParsedQuery.Empty;
        public string? ChildOfLower;       // already trimmed + ToLowerInvariant + trailing sep
        public string? ParentIsLower;      // already trimmed + ToLowerInvariant
        // One slot per (clause, alternative) flattened. Null slot means
        // "match as a substring" rather than via a compiled Regex.
        public Regex?[]? ClauseRegexes;
        public bool NeedsFullPath;         // true when any predicate needs the joined path
    }

    private CompiledQuery currentCompiled = new();
    private long currentSeq;

    // A regex that never matches anything. Used as the compiled slot when a
    // regex/wildcard pattern fails to compile, so an invalid pattern yields
    // "no match" — exactly the original behaviour — rather than silently
    // falling back to a literal-substring search.
    // \b\B can never both hold at one position, so this matches nothing.
    // (Both tokens are well-formed, so the pattern itself always compiles.)
    private static readonly Regex NeverMatch =
        new(@"\b\B", RegexOptions.CultureInvariant);

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
        // Publish derived state BEFORE bumping the sequence. The Interlocked.Increment
        // is a release fence — any reader that observes the new seq via
        // Volatile.Read is guaranteed to see the matching parsed write that
        // happened immediately before it.
        currentCompiled = Compile(parsed);
        Interlocked.Increment(ref currentSeq);
        var simplified = SimplifyForInnerEngine(parsed, query);
        return inner.SearchAsync(simplified, cancellationToken);
    }

    private static CompiledQuery Compile(ParsedQuery parsed)
    {
        var c = new CompiledQuery { Parsed = parsed };

        if (parsed.ChildOfPath is not null)
        {
            var trimmed = parsed.ChildOfPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            c.ChildOfLower = trimmed.ToLowerInvariant() + Path.DirectorySeparatorChar;
        }
        if (parsed.ParentIsPath is not null)
        {
            c.ParentIsLower = parsed.ParentIsPath
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
        }

        c.NeedsFullPath = parsed.Attribute is not null
            || parsed.Created is not null
            || parsed.Accessed is not null
            || parsed.ChildOfPath is not null
            || parsed.ContentSearch is not null
            || parsed.MatchPath;

        // Pre-compile regexes for each clause/alternative when needed. Done once
        // per parse instead of per row, and per-alternative instead of per row*alt.
        if (parsed.Clauses.Count > 0)
        {
            bool anyNeedsRegex = parsed.RegexMode;
            if (!anyNeedsRegex)
            {
                foreach (var clause in parsed.Clauses)
                {
                    foreach (var alt in clause.Alternatives)
                    {
                        if (alt.Contains('*') || alt.Contains('?')) { anyNeedsRegex = true; break; }
                    }
                    if (anyNeedsRegex) break;
                }
            }
            if (anyNeedsRegex)
            {
                int total = 0;
                foreach (var clause in parsed.Clauses) total += clause.Alternatives.Count;
                var arr = new Regex?[total];
                int idx = 0;
                var opts = (parsed.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                         | RegexOptions.CultureInvariant
                         | RegexOptions.Compiled;
                foreach (var clause in parsed.Clauses)
                {
                    foreach (var alt in clause.Alternatives)
                    {
                        Regex? rx = null;
                        try
                        {
                            if (parsed.RegexMode)
                                rx = new Regex(alt, opts);
                            else if (alt.Contains('*') || alt.Contains('?'))
                                rx = new Regex("^" + Regex.Escape(alt).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", opts);
                        }
                        // A failed compile here means a regex/wildcard WAS
                        // intended (the substring case never enters a Regex
                        // ctor) — fall back to never-match, not substring.
                        catch (ArgumentException) { rx = NeverMatch; }
                        arr[idx++] = rx;
                    }
                }
                c.ClauseRegexes = arr;
            }
        }
        return c;
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
        => inner.SortAsync(key, descending, cancellationToken);

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
        => inner.GetRowAsync(id, cancellationToken);

    // ── post-filtering ──────────────────────────────────────────────────

    private async Task OnIdsAsync(IReadOnlyList<uint> ids, long mySeq)
    {
        // Read seq FIRST so the volatile read acts as a memory fence for the
        // subsequent compiled read — without this ordering, on weaker memory
        // models we could see a fresh seq match while reading a stale
        // CompiledQuery from before SearchAsync committed it.
        if (Volatile.Read(ref currentSeq) != mySeq) return;
        var compiled = currentCompiled;
        var parsed   = compiled.Parsed;

        if (!NeedsPostFiltering(parsed))
        {
            if (Volatile.Read(ref currentSeq) == mySeq)
                filteredResults.OnNext(ids);
            return;
        }

        var kept = new List<uint>(Math.Min(ids.Count, DisplayCap));
        // Dupe needs to inspect every kept row even after DisplayCap, so the
        // bucketing list is separate. NameKey is the case-insensitive dedup key
        // — we pass an OrdinalIgnoreCase dictionary later instead of forcing
        // ToLowerInvariant on every row name here.
        List<(uint Id, string Name, ulong Size)>? bucketing = parsed.Dupe ? new() : null;
        int scanLimit = Math.Min(ids.Count, MaxScan);
        for (int i = 0; i < scanLimit; i++)
        {
            // Mid-loop cancellation: stop scanning if a newer search arrived.
            if (Volatile.Read(ref currentSeq) != mySeq) return;
            try
            {
                var row = await inner.GetRowAsync(ids[i], default);
                if (!Passes(row, compiled)) continue;
                kept.Add(ids[i]);
                bucketing?.Add((ids[i], row.Name, row.SizeBytes));
            }
            catch { /* skip unreadable */ }
            if (!parsed.Dupe && kept.Count >= DisplayCap) break;
        }

        if (parsed.Dupe && bucketing is not null)
        {
            var buckets = new Dictionary<(string Name, ulong Size), int>(
                bucketing.Count, NameSizeComparer.Instance);
            foreach (var entry in bucketing)
            {
                var key = (entry.Name, entry.Size);
                buckets[key] = buckets.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            kept = new List<uint>(bucketing.Count);
            foreach (var entry in bucketing)
            {
                if (buckets[(entry.Name, entry.Size)] >= 2) kept.Add(entry.Id);
                if (kept.Count >= DisplayCap) break;
            }
        }

        // Final guard before publishing — don't override a fresh result.
        if (Volatile.Read(ref currentSeq) == mySeq)
            filteredResults.OnNext(kept);
    }

    private sealed class NameSizeComparer : IEqualityComparer<(string Name, ulong Size)>
    {
        public static readonly NameSizeComparer Instance = new();
        public bool Equals((string Name, ulong Size) a, (string Name, ulong Size) b)
            => a.Size == b.Size && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Name, ulong Size) k)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Name), k.Size);
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

    private static bool Passes(ResultRowModel row, CompiledQuery compiled)
    {
        var q = compiled.Parsed;
        var attrLetters = row.Attributes ?? string.Empty;
        bool isDir = attrLetters.Contains('D');

        if (q.FileOnly   &&  isDir) return false;
        if (q.FolderOnly && !isDir) return false;

        if (q.ExtWhitelist is { Length: > 0 })
        {
            if (isDir) return false;
            var ext = Path.GetExtension(row.Name.AsSpan());
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            bool match = false;
            foreach (var a in q.ExtWhitelist)
            {
                if (ext.Equals(a.AsSpan(), StringComparison.OrdinalIgnoreCase)) { match = true; break; }
            }
            if (!match) return false;
        }

        if (q.Size is not null)
        {
            if (isDir) return false;
            if (!q.Size.Matches(row.SizeBytes)) return false;
        }

        if (q.Modified is not null && !q.Modified.Matches(row.ModifiedUtc.LocalDateTime)) return false;

        // Build the full path exactly once when any downstream predicate needs
        // it. NeedsFullPath is computed in Compile() so we don't allocate a
        // closure / lambda per row.
        string fullPath = compiled.NeedsFullPath
            ? (string.IsNullOrEmpty(row.ParentPath) ? row.Name : Path.Combine(row.ParentPath, row.Name))
            : string.Empty;

        if (q.Attribute is not null)
        {
            FileAttributes liveAttr;
            try { liveAttr = File.GetAttributes(fullPath); }
            catch { return false; }
            if (!q.Attribute.Matches(liveAttr)) return false;
        }

        if (q.Created is not null || q.Accessed is not null)
        {
            try
            {
                if (q.Created  is not null && !q.Created.Matches(File.GetCreationTime(fullPath))) return false;
                if (q.Accessed is not null && !q.Accessed.Matches(File.GetLastAccessTime(fullPath))) return false;
            }
            catch { return false; }
        }

        if (compiled.ChildOfLower is not null)
        {
            // OrdinalIgnoreCase StartsWith against a pre-lowered prefix —
            // avoids the per-row fullPath.ToLowerInvariant() allocation the
            // previous version did.
            if (!fullPath.StartsWith(compiled.ChildOfLower, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (compiled.ParentIsLower is not null)
        {
            var parent = (row.ParentPath ?? string.Empty).AsSpan();
            // Manual TrimEnd over the two separator chars — avoids a heap
            // allocation and avoids a stackalloc / array for two characters.
            int end = parent.Length;
            while (end > 0 && (parent[end - 1] == Path.DirectorySeparatorChar
                            || parent[end - 1] == Path.AltDirectorySeparatorChar))
                end--;
            if (!parent[..end].Equals(compiled.ParentIsLower.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (q.ContentSearch is not null)
        {
            if (isDir) return false;
            if (row.SizeBytes > MaxContentSearchBytes) return false;
            if (!FileContains(fullPath, q.ContentSearch, q.CaseSensitive)) return false;
        }

        if (q.Clauses.Count == 0) return true;
        var cmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int rxIdx = 0;
        var regexes = compiled.ClauseRegexes;
        foreach (var clause in q.Clauses)
        {
            bool hit = false;
            int altCount = clause.Alternatives.Count;
            int clauseStart = rxIdx;
            for (int a = 0; a < altCount; a++)
            {
                var alt = clause.Alternatives[a];
                var rx = regexes is not null && (clauseStart + a) < regexes.Length
                    ? regexes[clauseStart + a]
                    : null;
                if (AlternativeMatches(alt, rx, q, row.Name, fullPath, cmp))
                {
                    hit = true;
                    break;
                }
            }
            rxIdx += altCount; // advance past the whole clause regardless of early-out
            if (clause.Negated ? hit : !hit) return false;
        }
        return true;
    }

    private static bool AlternativeMatches(
        string alt,
        Regex? rx,
        ParsedQuery q,
        string name,
        string fullPath,
        StringComparison cmp)
    {
        if (rx is not null)
        {
            if (rx.IsMatch(name)) return true;
            return q.MatchPath && fullPath.Length > 0 && rx.IsMatch(fullPath);
        }
        if (name.Contains(alt, cmp)) return true;
        return q.MatchPath && fullPath.Length > 0 && fullPath.Contains(alt, cmp);
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
