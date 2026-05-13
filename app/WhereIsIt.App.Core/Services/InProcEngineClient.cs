using System;
using System.Collections.Concurrent;
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
    private readonly Func<IReadOnlyList<string>> rootProvider;
    private readonly Subject<string> statusChanges = new();
    private readonly Subject<int> metricsChanges = new();
    private readonly Subject<IReadOnlyList<uint>> results = new();

    private FileSystemInfo[] currentResults = [];
    private string sortKey = "name";
    private bool sortDescending;

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
        statusChanges.OnNext("searching");
        var parsed = QueryParser.Parse(query);
        var found = await Task.Run(
            () => ScanFileSystem(parsed, rootProvider(), cancellationToken),
            cancellationToken);
        currentResults = SortResults(found, sortKey, sortDescending);
        var ids = Enumerable.Range(0, currentResults.Length).Select(i => (uint)i).ToList();
        metricsChanges.OnNext(ids.Count);
        results.OnNext(ids);
        statusChanges.OnNext($"Found {ids.Count} results");
    }

    public Task SortAsync(string key, bool descending, CancellationToken cancellationToken)
    {
        sortKey = key;
        sortDescending = descending;
        currentResults = SortResults(currentResults, key, descending);
        var ids = Enumerable.Range(0, currentResults.Length).Select(i => (uint)i).ToList();
        results.OnNext(ids);
        return Task.CompletedTask;
    }

    public Task<ResultRowModel> GetRowAsync(uint id, CancellationToken cancellationToken)
    {
        if (id >= currentResults.Length)
            return Task.FromResult(new ResultRowModel("?", "?", 0, DateTimeOffset.UtcNow, ""));

        var fsi = currentResults[id];
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

        var bag = new ConcurrentBag<FileSystemInfo>();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = 0,
        };

        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", opts))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (!Matches(path, parsed, root)) continue;
                    bag.Add(Directory.Exists(path)
                        ? (FileSystemInfo)new DirectoryInfo(path)
                        : new FileInfo(path));
                }
            }
            catch (Exception) { }
        }

        return [.. bag];
    }

    private static bool Matches(string fullPath, ParsedQuery q, string root)
    {
        FileAttributes attr;
        try { attr = File.GetAttributes(fullPath); }
        catch { return false; }
        bool isDir = attr.HasFlag(FileAttributes.Directory);

        if (q.FileOnly && isDir) return false;
        if (q.FolderOnly && !isDir) return false;

        if (q.Attribute is not null && !q.Attribute.Matches(attr)) return false;

        var name = Path.GetFileName(fullPath);

        if (q.ExtWhitelist is { Length: > 0 })
        {
            if (isDir) return false;
            var ext = Path.GetExtension(name);
            if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
            bool extMatch = false;
            foreach (var allowed in q.ExtWhitelist)
            {
                if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
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
            try { size = (ulong)new FileInfo(fullPath).Length; }
            catch { return false; }
            if (!q.Size.Matches(size)) return false;
        }

        if (q.ChildOfPath is not null)
        {
            var prefix = NormalisePathPrefix(q.ChildOfPath);
            var lowerFull = fullPath.ToLowerInvariant();
            if (!lowerFull.StartsWith(prefix)) return false;
        }
        if (q.ParentIsPath is not null)
        {
            var target = TrimTrailingSeparators(q.ParentIsPath).ToLowerInvariant();
            var parent = (Path.GetDirectoryName(fullPath) ?? "").ToLowerInvariant();
            if (TrimTrailingSeparators(parent) != target) return false;
        }

        if (q.Modified is not null || q.Created is not null || q.Accessed is not null)
        {
            try
            {
                if (q.Modified is not null && !q.Modified.Matches(File.GetLastWriteTime(fullPath))) return false;
                if (q.Created  is not null && !q.Created.Matches(File.GetCreationTime(fullPath))) return false;
                if (q.Accessed is not null && !q.Accessed.Matches(File.GetLastAccessTime(fullPath))) return false;
            }
            catch { return false; }
        }

        if (q.Clauses.Count == 0) return true;

        var cmp = q.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');

        foreach (var clause in q.Clauses)
        {
            bool clauseHit = false;
            foreach (var alt in clause.Alternatives)
            {
                if (AlternativeMatches(alt, q, name, relative, cmp))
                {
                    clauseHit = true;
                    break;
                }
            }
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

    private static bool AlternativeMatches(string alt, ParsedQuery q, string name, string relative, StringComparison cmp)
    {
        if (q.RegexMode)
        {
            try
            {
                var opts = (q.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant;
                return Regex.IsMatch(name, alt, opts) || Regex.IsMatch(relative, alt, opts);
            }
            catch (ArgumentException) { return false; }
        }

        if (alt.Contains('*') || alt.Contains('?'))
        {
            var rx = WildcardToRegex(alt, q.CaseSensitive);
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

    private static string NormalisePathPrefix(string path)
    {
        var p = TrimTrailingSeparators(path).ToLowerInvariant();
        return p + Path.DirectorySeparatorChar;
    }

    private static string TrimTrailingSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static Regex WildcardToRegex(string wildcard, bool caseSensitive)
    {
        var pattern = "^" + Regex.Escape(wildcard)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(pattern, opts | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

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
        IEnumerable<FileSystemInfo> sorted = key switch
        {
            "size" => items.OrderBy(f => f is FileInfo fi ? (long)fi.Length : 0L),
            "modified" => items.OrderBy(f => { try { return f.LastWriteTimeUtc; } catch { return DateTime.MinValue; } }),
            _ => items.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        if (descending) sorted = sorted.Reverse();
        return sorted.ToArray();
    }

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
}
