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

    // Optional run-metadata lookups (keyed by full path) backing the rc:/dr:
    // filters. Null when no RunCountService is wired (e.g. the HTTP frontend or
    // tests) — those filters then degrade to no-ops rather than excluding all.
    private readonly Func<string, int>? runCountLookup;
    private readonly Func<string, DateTimeOffset>? runDateLookup;

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
        // Compiled, anchored wildcard regexes for wfn:/wholefilename: — one per
        // listed pattern, all of which must match the whole filename.
        public Regex[]? WholeFilenameRegexes;
        public bool NeedsFullPath;         // true when any predicate needs the joined path
        // Pre-compiled per-function-leaf queries for grouped boolean queries that
        // OR/group functions (e.g. <ext:cs>|<ext:txt>). Keyed by the raw token.
        public Dictionary<string, CompiledQuery>? FuncLeaves;
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

    // User-supplied regex/wildcard patterns can backtrack catastrophically
    // (e.g. regex:(a+)+$). Cap every match so a pathological pattern can't
    // wedge the result-watcher thread; a timeout abandons the current scan.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public FilteringEngineClient(
        IEngineClient inner,
        Func<string, int>? runCountLookup = null,
        Func<string, DateTimeOffset>? runDateLookup = null)
    {
        this.inner = inner;
        this.runCountLookup = runCountLookup;
        this.runDateLookup = runDateLookup;
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
            || parsed.EmptyOnly
            || parsed.ChildCount is not null
            || parsed.ChildFileCount is not null
            || parsed.ChildFolderCount is not null
            || parsed.Depth is not null
            || parsed.Width is not null
            || parsed.Height is not null
            || parsed.MediaFilters.Count > 0
            || parsed.Duration is not null
            || parsed.SampleRate is not null
            || parsed.Channels is not null
            || parsed.Bitrate is not null
            || parsed.RunCount is not null
            || parsed.DateRun is not null
            || parsed.TermExpr is not null
            || parsed.MatchPath;

        if (parsed.WholeFilename is { Length: > 0 })
        {
            // No RegexOptions.Compiled: these patterns are rebuilt on every
            // keystroke, so JIT-compiling them each time costs more than it saves.
            var opts = (parsed.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase)
                     | RegexOptions.CultureInvariant;
            var rxs = new Regex[parsed.WholeFilename.Length];
            for (int i = 0; i < rxs.Length; i++)
            {
                try
                {
                    rxs[i] = new Regex(
                        "^" + Regex.Escape(parsed.WholeFilename[i])
                            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$", opts, RegexTimeout);
                }
                catch (ArgumentException) { rxs[i] = NeverMatch; }
            }
            c.WholeFilenameRegexes = rxs;
        }

        // Pre-compile regexes for each clause/alternative when needed. Done once
        // per parse instead of per row, and per-alternative instead of per row*alt.
        if (parsed.Clauses.Count > 0)
        {
            bool anyNeedsRegex = parsed.RegexMode;
            if (!anyNeedsRegex && parsed.Wildcards)
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
                         | RegexOptions.CultureInvariant;
                foreach (var clause in parsed.Clauses)
                {
                    foreach (var alt in clause.Alternatives)
                    {
                        Regex? rx = null;
                        try
                        {
                            if (parsed.RegexMode)
                                rx = new Regex(alt, opts, RegexTimeout);
                            else if (parsed.Wildcards && (alt.Contains('*') || alt.Contains('?')))
                                rx = new Regex("^" + Regex.Escape(alt).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", opts, RegexTimeout);
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

        // Pre-compile each distinct function leaf in a grouped boolean query so
        // the per-row evaluation reuses the normal Passes() filter logic without
        // re-parsing the token for every row.
        if (parsed.TermExpr is not null)
        {
            var tokens = new List<string>();
            BooleanQuery.CollectFunctionTokens(parsed.TermExpr, tokens);
            if (tokens.Count > 0)
            {
                var map = new Dictionary<string, CompiledQuery>(StringComparer.Ordinal);
                foreach (var tok in tokens)
                {
                    if (map.ContainsKey(tok)) continue;
                    map[tok] = Compile(QueryParser.Parse(tok));
                }
                c.FuncLeaves = map;
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

        // count: caps the visible rows; never exceed the safety DisplayCap.
        int cap = parsed.MaxResults is int m ? Math.Min(DisplayCap, m) : DisplayCap;

        var kept = new List<uint>(Math.Min(ids.Count, cap));
        // Dupe needs to inspect every kept row even after the cap, so the
        // bucketing list is separate. Key is the per-mode dedup string.
        List<(uint Id, string Key)>? bucketing = parsed.Dupe ? new() : null;
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
                bucketing?.Add((ids[i], DupeKeyFor(parsed.DupeMode, row)));
            }
            // A catastrophic-backtracking regex/wildcard will time out on every
            // row, so abandon the whole scan rather than paying the timeout per
            // row. The seq fence lets the next keystroke start fresh.
            catch (RegexMatchTimeoutException) { return; }
            catch { /* skip unreadable */ }
            if (!parsed.Dupe && kept.Count >= cap) break;
        }

        if (parsed.Dupe && bucketing is not null)
        {
            var buckets = new Dictionary<string, int>(bucketing.Count, StringComparer.Ordinal);
            foreach (var entry in bucketing)
                buckets[entry.Key] = buckets.TryGetValue(entry.Key, out var n) ? n + 1 : 1;
            kept = new List<uint>(bucketing.Count);
            foreach (var entry in bucketing)
            {
                if (buckets[entry.Key] >= 2) kept.Add(entry.Id);
                if (kept.Count >= cap) break;
            }
        }

        // Final guard before publishing — don't override a fresh result.
        if (Volatile.Read(ref currentSeq) == mySeq)
            filteredResults.OnNext(kept);
    }

    /// <summary>Builds the duplicate-grouping key for a row under the active
    /// <see cref="DupeKind"/>. Name-based keys are upper-cased so grouping is
    /// case-insensitive (matching Everything).</summary>
    private static string DupeKeyFor(DupeKind mode, ResultRowModel row) => mode switch
    {
        DupeKind.Size     => row.SizeBytes.ToString(),
        DupeKind.NamePart => Path.GetFileNameWithoutExtension(row.Name).ToUpperInvariant(),
        DupeKind.Attrib   => (row.Attributes ?? string.Empty).ToUpperInvariant(),
        _                 => row.Name.ToUpperInvariant() + " " + row.SizeBytes,
    };

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
        if (q.StartsWith is not null || q.EndsWith is not null
            || q.WholeFilename is not null) return true;
        if (q.RootOnly || q.EmptyOnly || q.NameLength is not null) return true;
        if (q.ChildCount is not null || q.ChildFileCount is not null
            || q.ChildFolderCount is not null) return true;
        if (q.Depth is not null) return true;
        if (q.Width is not null || q.Height is not null) return true;
        if (q.MediaFilters.Count > 0) return true;
        if (q.Duration is not null || q.SampleRate is not null || q.Channels is not null) return true;
        if (q.Bitrate is not null) return true;
        if (q.RunCount is not null || q.DateRun is not null) return true;
        if (q.TermExpr is not null) return true;
        if (q.MaxResults is not null) return true;
        // nodiacritics: changes how every clause matches, so the inner engine's
        // default (diacritic-sensitive) result set has to be re-filtered.
        if (!q.MatchDiacritics && q.Clauses.Count > 0) return true;
        // Any negated clause or |-alternatives need post-filtering because the
        // native engine treats those tokens as literal substrings.
        foreach (var c in q.Clauses)
        {
            if (c.Negated) return true;
            if (c.Alternatives.Count > 1) return true;
            // nowildcards: must enforce a literal interpretation of * / ? that
            // the inner engine would otherwise expand as wildcards.
            if (!q.Wildcards)
                foreach (var alt in c.Alternatives)
                    if (alt.Contains('*') || alt.Contains('?')) return true;
        }
        return false;
    }

    private bool Passes(ResultRowModel row, CompiledQuery compiled)
    {
        var q = compiled.Parsed;
        var attrLetters = row.Attributes ?? string.Empty;
        bool isDir = attrLetters.Contains('D');

        if (q.FileOnly   &&  isDir) return false;
        if (q.FolderOnly && !isDir) return false;

        // Name-only predicates (startwith:/endwith:/wfn:/len:) — cheap, no
        // path build or stat, so test them before the heavier filters.
        var nameCmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Folded form of the name for nodiacritics: matching, computed once.
        string matchName = q.MatchDiacritics ? row.Name : QueryParser.RemoveDiacritics(row.Name);
        if (q.StartsWith is not null)
            foreach (var p in q.StartsWith)
                if (!matchName.StartsWith(q.MatchDiacritics ? p : QueryParser.RemoveDiacritics(p), nameCmp)) return false;
        if (q.EndsWith is not null)
            foreach (var s in q.EndsWith)
                if (!matchName.EndsWith(q.MatchDiacritics ? s : QueryParser.RemoveDiacritics(s), nameCmp)) return false;
        if (compiled.WholeFilenameRegexes is not null)
            foreach (var rx in compiled.WholeFilenameRegexes)
                if (!rx.IsMatch(row.Name)) return false;
        if (q.NameLength is not null && !q.NameLength.Matches((ulong)row.Name.Length))
            return false;

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

        if (q.RootOnly && !IsRootParent(row.ParentPath)) return false;

        if (q.EmptyOnly)
        {
            if (isDir)
            {
                try
                {
                    using var e = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
                    if (e.MoveNext()) return false;
                }
                catch { return false; }
            }
            else if (row.SizeBytes != 0) return false;
        }

        if (q.ChildCount is not null || q.ChildFileCount is not null || q.ChildFolderCount is not null)
        {
            if (!isDir) return false;
            if (!ChildCountsMatch(q, fullPath)) return false;
        }

        if (q.Depth is not null && !q.Depth.Matches((ulong)QueryParser.FolderDepth(fullPath)))
            return false;

        if (q.Width is not null || q.Height is not null)
        {
            if (isDir) return false;
            if (!ImageDimensions.TryRead(fullPath, out int w, out int h)) return false;
            if (q.Width is not null && !q.Width.Matches((ulong)w)) return false;
            if (q.Height is not null && !q.Height.Matches((ulong)h)) return false;
        }

        if (q.MediaFilters.Count > 0)
        {
            if (isDir) return false;
            if (!AudioTags.Match(q.MediaFilters, fullPath, q.CaseSensitive, q.MatchDiacritics)) return false;
        }

        if (q.Duration is not null || q.SampleRate is not null || q.Channels is not null)
        {
            if (isDir) return false;
            if (!AudioTags.MatchStream(fullPath, q.Duration, q.SampleRate, q.Channels)) return false;
        }

        if (q.Bitrate is not null)
        {
            if (isDir) return false;
            if (!AudioTags.MatchBitrate(fullPath, row.SizeBytes, q.Bitrate)) return false;
        }

        // Run-metadata filters degrade to no-ops when no lookup is wired, so a
        // missing RunCountService never silently empties the result set.
        if (q.RunCount is not null && runCountLookup is not null)
        {
            int rc = runCountLookup(fullPath);
            if (rc < 0) rc = 0;
            if (!q.RunCount.Matches((ulong)rc)) return false;
        }
        if (q.DateRun is not null && runDateLookup is not null)
        {
            var dr = runDateLookup(fullPath);
            if (dr == default) return false;            // never opened ⇒ no run date
            if (!q.DateRun.Matches(dr.LocalDateTime)) return false;
        }

        if (q.TermExpr is null && q.Clauses.Count == 0) return true;
        var cmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Folded path for nodiacritics: substring matching, computed once.
        string matchFull = (q.MatchDiacritics || !q.MatchPath || fullPath.Length == 0)
            ? fullPath : QueryParser.RemoveDiacritics(fullPath);

        // Grouped boolean query (< >): evaluate the expression tree instead of
        // the flat clause list. Term leaves match via the substring rules;
        // function leaves reuse Passes() against their pre-compiled sub-query.
        if (q.TermExpr is not null)
            return BooleanQuery.Eval(q.TermExpr, leaf => leaf switch
            {
                BoolTerm t => MatchAnyAlternative(t.Alternatives, q, row.Name, matchName, fullPath, matchFull, cmp),
                BoolFunc f => compiled.FuncLeaves is not null
                              && compiled.FuncLeaves.TryGetValue(f.Token, out var fc)
                              && Passes(row, fc),
                _ => true,
            });

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
                if (AlternativeMatches(alt, rx, q, row.Name, matchName, fullPath, matchFull, cmp))
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

    /// <summary>True when any of a boolean term's alternatives matches the row
    /// (substring/diacritic/match-path rules; grouped terms aren't wildcarded).</summary>
    private static bool MatchAnyAlternative(
        IReadOnlyList<string> alts, ParsedQuery q,
        string name, string matchName, string fullPath, string matchFull, StringComparison cmp)
    {
        foreach (var alt in alts)
            if (AlternativeMatches(alt, null, q, name, matchName, fullPath, matchFull, cmp))
                return true;
        return false;
    }

    private static bool AlternativeMatches(
        string alt,
        Regex? rx,
        ParsedQuery q,
        string name,
        string matchName,
        string fullPath,
        string matchFull,
        StringComparison cmp)
    {
        // Regex/wildcard patterns match against the raw (unfolded) strings —
        // folding only applies to plain substring matching.
        if (rx is not null)
        {
            if (rx.IsMatch(name)) return true;
            return q.MatchPath && fullPath.Length > 0 && rx.IsMatch(fullPath);
        }
        var needle = q.MatchDiacritics ? alt : QueryParser.RemoveDiacritics(alt);
        if (matchName.Contains(needle, cmp)) return true;
        return q.MatchPath && matchFull.Length > 0 && matchFull.Contains(needle, cmp);
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
        // nodiacritics: folding produces a *superset* of the diacritic-sensitive
        // matches, and post-filtering can only narrow the inner engine's output.
        // So the inner engine must fold too (its QueryEngine honours this flag);
        // the C# post-filter then re-applies the same fold for exactness.
        if (!q.MatchDiacritics) parts.Add("diacritics:false");

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

    /// <summary>True when <paramref name="parent"/> is a volume root
    /// (<c>C:\</c>) or a UNC share root (<c>\\server\share</c>).</summary>
    private static bool IsRootParent(string? parent)
    {
        if (string.IsNullOrEmpty(parent)) return true;
        var p = parent.AsSpan().TrimEnd('\\').TrimEnd('/');
        if (p.Length == 2 && char.IsLetter(p[0]) && p[1] == ':') return true;
        if (p.Length > 2 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/'))
        {
            int sep = 0;
            for (int i = 2; i < p.Length; i++)
                if (p[i] == '\\' || p[i] == '/') sep++;
            return sep == 1;
        }
        return false;
    }

    /// <summary>Tests a folder's immediate child counts against the active
    /// <c>childcount:</c>/<c>childfilecount:</c>/<c>childfoldercount:</c> filters.
    /// Enumerates the directory once and only stats entry types when a
    /// file/folder split is actually requested.</summary>
    private static bool ChildCountsMatch(ParsedQuery q, string fullPath)
    {
        try
        {
            bool needSplit = q.ChildFileCount is not null || q.ChildFolderCount is not null;
            ulong total = 0, files = 0, folders = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
            {
                total++;
                if (!needSplit) continue;
                if (Directory.Exists(entry)) folders++;
                else files++;
            }
            if (q.ChildCount is not null && !q.ChildCount.Matches(total)) return false;
            if (q.ChildFileCount is not null && !q.ChildFileCount.Matches(files)) return false;
            if (q.ChildFolderCount is not null && !q.ChildFolderCount.Matches(folders)) return false;
            return true;
        }
        catch { return false; }
    }

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
                // Carry only the last (needle.Length - 1) chars so matches that
                // straddle chunk boundaries aren't missed. A 0/1-char needle can
                // never straddle; carrying nothing avoids the unbounded growth
                // that "carry = chunk" would cause on a large no-match file.
                int keep = needle.Length - 1;
                carry = keep <= 0 ? string.Empty
                      : chunk.Length > keep ? chunk[^keep..]
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
