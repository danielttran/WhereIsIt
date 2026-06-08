using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.Services;

public sealed class InProcEngineClient : IEngineClient, IDisposable
{
    // Never-matching regex used when a regex/wildcard pattern fails to
    // compile, so an invalid pattern yields no results (original behaviour)
    // instead of degrading to a literal-substring search.
    // \b\B can never both hold at one position, so this matches nothing.
    private static readonly Regex NeverMatch =
        new(@"\b\B", RegexOptions.CultureInvariant);

    // Bound every user-supplied regex/wildcard match so a catastrophic-
    // backtracking pattern can't wedge the background scan thread.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<string>> rootProvider;
    private readonly Subject<string> statusChanges = new();
    private readonly Subject<int> metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>> results = new();
    private volatile SearchState state = new(Array.Empty<FileSystemInfo>(), "name", false);
    private int searchGeneration;

    public InProcEngineClient()
        : this(GetDefaultRoots)
    {
    }

    public InProcEngineClient(Func<IReadOnlyList<string>> rootProvider)
    {
        this.rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    public IObservable<string> StatusChanges => statusChanges;
    public IObservable<int> MetricsChanges => metricsChanges;
    public IObservable<IReadOnlyList<uint>> ObserveResults => results;

    public async Task SearchAsync(string query, CancellationToken cancellationToken)
    {
        int generation = Interlocked.Increment(ref searchGeneration);
        statusChanges.OnNext("searching");
        var parsed = QueryParser.Parse(query);
        var found = await Task.Run(
            () => ScanFileSystem(parsed, rootProvider(), cancellationToken),
            cancellationToken);

        // A newer search already started; drop stale completion so we never
        // overwrite fresh results with older scan output.
        if (generation != Volatile.Read(ref searchGeneration)) return;

        FileSystemInfo[]? sorted = null;
        while (true)
        {
            // If another search started while we were preparing this state
            // update, this completion is stale and must not publish.
            if (generation != Volatile.Read(ref searchGeneration)) return;

            var snapshot = Volatile.Read(ref state);
            var working = found.ToArray();
            sorted = SortResults(working, snapshot.SortKey, snapshot.SortDescending);
            var next = snapshot with { Results = sorted };
            var observed = Interlocked.CompareExchange(ref state, next, snapshot);
            if (ReferenceEquals(observed, snapshot)) break;
        }

        var ids = Enumerable.Range(0, sorted.Length).Select(i => (uint)i).ToList();
        metricsChanges.OnNext(ids.Count);
        results.OnNext(ids);
        statusChanges.OnNext($"Found {ids.Count} results");
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        FileSystemInfo[] sorted;
        while (true)
        {
            var snapshot = Volatile.Read(ref state);
            var working = snapshot.Results.ToArray();
            sorted = SortResults(working, key, descending);
            var next = new SearchState(sorted, key, descending);
            var observed = Interlocked.CompareExchange(ref state, next, snapshot);
            if (ReferenceEquals(observed, snapshot)) break;
        }

        var ids = Enumerable.Range(0, sorted.Length).Select(i => (uint)i).ToList();
        results.OnNext(ids);
        return Task.CompletedTask;
    }

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref state).Results;
        if (id >= snapshot.Length)
            return Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));

        var fsi = snapshot[id];
        DateTimeOffset modified  = fsi.Exists ? new(fsi.LastWriteTimeUtc,  TimeSpan.Zero) : DateTimeOffset.UtcNow;
        DateTimeOffset created   = SafeTime(() => fsi.CreationTimeUtc);
        DateTimeOffset accessed  = SafeTime(() => fsi.LastAccessTimeUtc);

        var model = fsi is FileInfo fi
            ? new ResultRowModel(
                fi.Name,
                fi.DirectoryName ?? string.Empty,
                (ulong)(fi.Exists ? fi.Length : 0),
                modified,
                GetAttributeString(fi.Attributes))
            {
                CreatedUtc  = created,
                AccessedUtc = accessed,
            }
            : new ResultRowModel(
                fsi.Name,
                Path.GetDirectoryName(fsi.FullName) ?? string.Empty,
                0,
                modified,
                GetAttributeString(fsi.Attributes) + "D")
            {
                CreatedUtc  = created,
                AccessedUtc = accessed,
            };
        return Task.FromResult(model);
    }

    private static DateTimeOffset SafeTime(Func<DateTime> getter)
    {
        try { return new DateTimeOffset(getter(), TimeSpan.Zero); }
        catch { return default; }
    }

    private static FileSystemInfo[] ScanFileSystem(ParsedQuery parsed, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        if (parsed.IsEmpty) return [];

        // Pre-compute path-scope predicates once so the per-entry loop avoids
        // repeated ToLowerInvariant and TrimEnd allocations.
        string? childPrefixLower = parsed.ChildOfPath is null ? null
            : TrimTrailingSeparators(parsed.ChildOfPath).ToLowerInvariant() + Path.DirectorySeparatorChar;
        string? parentLower = parsed.ParentIsPath is null ? null
            : TrimTrailingSeparators(parsed.ParentIsPath).ToLowerInvariant();
        // Pre-compile clause regexes once — without this, every file with a
        // wildcard or regex query pays a Regex compile+JIT per row.
        var clauseRegexes = BuildClauseRegexes(parsed);

        var results = new List<FileSystemInfo>(256);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = 0,
            // ReturnSpecialDirectories defaults to false, which is what we want.
        };

        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;

            try
            {
                var rootInfo = new DirectoryInfo(root);
                // EnumerateFileSystemInfos returns FileSystemInfo objects with
                // attributes already cached from the directory entry — no
                // separate File.GetAttributes / Directory.Exists stat per item
                // like the old EnumerateFileSystemEntries path required.
                foreach (var info in rootInfo.EnumerateFileSystemInfos("*", opts))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!Matches(info, parsed, root, childPrefixLower, parentLower, clauseRegexes)) continue;
                    results.Add(info);
                }
            }
            catch (Exception) { }
        }

        return results.ToArray();
    }

    private static bool Matches(FileSystemInfo info, ParsedQuery q, string root, string? childPrefixLower, string? parentLower, Regex?[]? clauseRegexes)
    {
        FileAttributes attr;
        try { attr = info.Attributes; }
        catch { return false; }
        bool isDir = (attr & FileAttributes.Directory) != 0;

        if (q.FileOnly && isDir) return false;
        if (q.FolderOnly && !isDir) return false;

        if (q.Attribute is not null && !q.Attribute.Matches(attr)) return false;

        var name = info.Name;
        var fullPath = info.FullName;

        if (q.ExtWhitelist is { Length: > 0 })
        {
            if (isDir) return false;
            var ext = Path.GetExtension(name.AsSpan());
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            bool extMatch = false;
            foreach (var allowed in q.ExtWhitelist)
            {
                if (ext.Equals(allowed.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    extMatch = true;
                    break;
                }
            }
            if (!extMatch) return false;
        }

        if (q.Size is not null)
        {
            if (isDir) return false;
            ulong size;
            try { size = info is FileInfo fi ? (ulong)fi.Length : 0; }
            catch { return false; }
            if (!q.Size.Matches(size)) return false;
        }

        if (childPrefixLower is not null)
        {
            if (!fullPath.StartsWith(childPrefixLower, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (parentLower is not null)
        {
            var parent = Path.GetDirectoryName(fullPath.AsSpan());
            // Trim trailing separators on the span — no allocation.
            int end = parent.Length;
            while (end > 0 && (parent[end - 1] == Path.DirectorySeparatorChar
                            || parent[end - 1] == Path.AltDirectorySeparatorChar))
                end--;
            if (!parent[..end].Equals(parentLower.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (q.Modified is not null || q.Created is not null || q.Accessed is not null)
        {
            try
            {
                // FileSystemInfo carries the timestamps cached from the entry —
                // no extra stat call per attribute the way separate File.* did.
                if (q.Modified is not null && !q.Modified.Matches(info.LastWriteTime)) return false;
                if (q.Created  is not null && !q.Created.Matches(info.CreationTime)) return false;
                if (q.Accessed is not null && !q.Accessed.Matches(info.LastAccessTime)) return false;
            }
            catch { return false; }
        }

        if (q.Clauses.Count == 0) return true;

        var cmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Build the relative path only when a clause needs it (i.e. we have
        // any clauses). For a typical extension-only or size-only query the
        // clauses list is empty so we short-circuited above without paying for
        // GetRelativePath/Replace allocations.
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

        int rxIdx = 0;
        foreach (var clause in q.Clauses)
        {
            bool clauseHit = false;
            int altCount = clause.Alternatives.Count;
            int start = rxIdx;
            for (int a = 0; a < altCount; a++)
            {
                var alt = clause.Alternatives[a];
                var rx = clauseRegexes is not null && (start + a) < clauseRegexes.Length
                    ? clauseRegexes[start + a] : null;
                if (AlternativeMatches(alt, rx, q, name, relative, cmp))
                {
                    clauseHit = true;
                    break;
                }
            }
            rxIdx += altCount;
            if (clause.Negated)
            {
                if (clauseHit) return false;
            }
            else
            {
                if (!clauseHit) return false;
            }
        }

        return true;
    }

    private static Regex?[]? BuildClauseRegexes(ParsedQuery parsed)
    {
        if (parsed.Clauses.Count == 0) return null;

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
        if (!anyNeedsRegex) return null;

        int total = 0;
        foreach (var clause in parsed.Clauses) total += clause.Alternatives.Count;
        var arr = new Regex?[total];
        int idx = 0;
        // No RegexOptions.Compiled: rebuilt per search, so the JIT cost of
        // compiling outweighs the per-match saving on a throwaway pattern.
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
                    else if (alt.Contains('*') || alt.Contains('?'))
                        rx = new Regex("^" + Regex.Escape(alt).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", opts, RegexTimeout);
                }
                // Failed compile ⇒ a regex/wildcard was intended; never-match
                // rather than fall through to substring.
                catch (ArgumentException) { rx = NeverMatch; }
                arr[idx++] = rx;
            }
        }
        return arr;
    }

    private static bool AlternativeMatches(string alt, Regex? rx, ParsedQuery q, string name, string relative, StringComparison cmp)
    {
        if (rx is not null)
        {
            return rx.IsMatch(name) || rx.IsMatch(relative);
        }

        if (q.WholeWord)
            return MatchesWord(name, alt, cmp) || (q.MatchPath && MatchesWord(relative, alt, cmp));

        return name.Contains(alt, cmp) || (q.MatchPath && relative.Contains(alt, cmp));
    }

    private static bool MatchesWord(string haystack, string needle, StringComparison cmp)
    {
        if (needle.Length == 0) return false;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, cmp)) >= 0)
        {
            bool leftOk  = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            bool rightOk = idx + needle.Length >= haystack.Length
                        || !char.IsLetterOrDigit(haystack[idx + needle.Length]);
            if (leftOk && rightOk) return true;
            idx += needle.Length;
        }
        return false;
    }

    private static string TrimTrailingSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static IReadOnlyList<string> GetDefaultRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToArray();
        }

        var roots = new List<string> { "/" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            roots.Add(home);

        return roots;
    }

    private static FileSystemInfo[] SortResults(FileSystemInfo[] items, string key, bool descending)
    {
        // In-place sort with an inverted comparison for descending — avoids
        // the OrderBy(...).Reverse() pattern which materializes the entire
        // sequence twice and allocates a second array.
        int dir = descending ? -1 : 1;
        // Array.Sort is not stable, so size/modified ties get a deterministic
        // name tiebreaker — otherwise equal-key rows reshuffle on every
        // re-sort, which looks like a UI glitch.
        Comparison<FileSystemInfo> cmp = key switch
        {
            "size" => (a, b) =>
            {
                int c = dir * Compare(GetSize(a), GetSize(b));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            },
            "modified" => (a, b) =>
            {
                int c = dir * GetModified(a).CompareTo(GetModified(b));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            },
            "created" => (a, b) =>
            {
                int c = dir * GetTime(a, static f => f.CreationTimeUtc).CompareTo(GetTime(b, static f => f.CreationTimeUtc));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            },
            "accessed" => (a, b) =>
            {
                int c = dir * GetTime(a, static f => f.LastAccessTimeUtc).CompareTo(GetTime(b, static f => f.LastAccessTimeUtc));
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            },
            "extension" or "type" or "ext" => (a, b) =>
            {
                int c = dir * string.Compare(Ext(a), Ext(b), StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            },
            "path" => (a, b) => dir * string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        Array.Sort(items, cmp);
        return items;
    }

    private static long GetSize(FileSystemInfo f)
    {
        if (f is not FileInfo fi) return 0L;
        try { return fi.Length; }
        catch { return 0L; }
    }
    private static DateTime GetModified(FileSystemInfo f) { try { return f.LastWriteTimeUtc; } catch { return DateTime.MinValue; } }
    private static DateTime GetTime(FileSystemInfo f, Func<FileSystemInfo, DateTime> get)
    { try { return get(f); } catch { return DateTime.MinValue; } }
    private static string Ext(FileSystemInfo f)
        => f is FileInfo ? Path.GetExtension(f.Name) : string.Empty;
    private static int Compare(long a, long b) => a < b ? -1 : (a > b ? 1 : 0);

    private static string GetAttributeString(FileAttributes attr)
    {
        try
        {
            return string.Concat(
                attr.HasFlag(FileAttributes.ReadOnly) ? "R" : "",
                attr.HasFlag(FileAttributes.Hidden) ? "H" : "",
                attr.HasFlag(FileAttributes.System) ? "S" : "",
                attr.HasFlag(FileAttributes.Archive) ? "A" : "");
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        statusChanges.Dispose();
        metricsChanges.Dispose();
        results.Dispose();
    }

    private sealed record SearchState(FileSystemInfo[] Results, string SortKey, bool SortDescending);
}
