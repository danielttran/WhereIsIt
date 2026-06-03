using System;
using System.Collections.Generic;
using System.Text;

namespace WhereIsIt.App.Services;

public sealed record ParsedQuery
{
    public static readonly ParsedQuery Empty = new();

    public bool CaseSensitive { get; init; }
    public bool RegexMode { get; init; }
    public bool WholeWord { get; init; }
    public bool MatchPath { get; init; }
    public bool FileOnly { get; init; }
    public bool FolderOnly { get; init; }

    /// <summary><c>wildcards:</c>/<c>nowildcards:</c> — when false, <c>*</c> and
    /// <c>?</c> in a term are matched literally instead of as wildcards. Defaults
    /// to true, matching Everything's default. Ignored when <see cref="RegexMode"/>
    /// is on (regex always interprets its own metacharacters).</summary>
    public bool Wildcards { get; init; } = true;

    /// <summary><c>diacritics:</c>/<c>nodiacritics:</c> — when false, accented
    /// characters fold to their base letter for matching (so <c>e</c> matches
    /// <c>é</c>). Defaults to true (diacritic-sensitive). Folding applies to the
    /// substring and whole-word match paths.</summary>
    public bool MatchDiacritics { get; init; } = true;
    public string[]? ExtWhitelist { get; init; }
    public SizeRange? Size { get; init; }
    public DateRange? Modified { get; init; }
    public DateRange? Created  { get; init; }
    public DateRange? Accessed { get; init; }
    /// <summary><c>dr:</c> — date the file was last run/opened from WhereIsIt.</summary>
    public DateRange? DateRun { get; init; }
    /// <summary><c>rc:</c>/<c>runcount:</c> — how many times the file has been
    /// opened from WhereIsIt.</summary>
    public SizeRange? RunCount { get; init; }
    public AttributeFilter? Attribute { get; init; }
    public string? ChildOfPath  { get; init; }
    public string? ParentIsPath { get; init; }

    /// <summary>Everything-style duplicate grouping mode. <see cref="DupeKind.None"/>
    /// when no <c>dupe:</c>/<c>sizedupe:</c>/… modifier was present.</summary>
    public DupeKind DupeMode { get; init; }

    /// <summary>Back-compat shim: true when any duplicate mode is active.</summary>
    public bool Dupe => DupeMode != DupeKind.None;

    /// <summary><c>startwith:</c> — filename must start with every listed prefix.</summary>
    public string[]? StartsWith { get; init; }
    /// <summary><c>endwith:</c> — filename must end with every listed suffix.</summary>
    public string[]? EndsWith { get; init; }
    /// <summary><c>wfn:</c>/<c>wholefilename:</c> — the whole filename must match
    /// each pattern (wildcards allowed).</summary>
    public string[]? WholeFilename { get; init; }
    /// <summary><c>root:</c> — only entries directly under a volume/share root.</summary>
    public bool RootOnly { get; init; }
    /// <summary><c>len:</c> — filename-length constraint (units ignored).</summary>
    public SizeRange? NameLength { get; init; }
    /// <summary><c>empty:</c> — zero-byte files and childless folders.</summary>
    public bool EmptyOnly { get; init; }
    /// <summary><c>count:</c> — cap the number of returned rows.</summary>
    public int? MaxResults { get; init; }

    /// <summary><c>childcount:</c> — folders whose immediate child count matches
    /// (files + folders). Applies to folders only.</summary>
    public SizeRange? ChildCount { get; init; }
    /// <summary><c>childfilecount:</c> — folders whose immediate file count matches.</summary>
    public SizeRange? ChildFileCount { get; init; }
    /// <summary><c>childfoldercount:</c> — folders whose immediate sub-folder count matches.</summary>
    public SizeRange? ChildFolderCount { get; init; }
    /// <summary><c>depth:</c>/<c>parents:</c> — folder depth, defined as the number
    /// of path separators in the full path (a volume-root entry is depth 1).</summary>
    public SizeRange? Depth { get; init; }
    /// <summary><c>width:</c> / <c>dimensions:</c> — image pixel width.</summary>
    public SizeRange? Width { get; init; }
    /// <summary><c>height:</c> / <c>dimensions:</c> — image pixel height.</summary>
    public SizeRange? Height { get; init; }
    /// <summary><c>orientation:</c> — EXIF orientation value (1–8).</summary>
    public SizeRange? Orientation { get; init; }
    /// <summary>Audio tag filters (<c>artist:</c>/<c>album:</c>/<c>title:</c>/
    /// <c>year:</c>/<c>genre:</c>/<c>track:</c>/<c>comment:</c>), matched as
    /// case-insensitive substrings against ID3 tags.</summary>
    public IReadOnlyList<MediaFilter> MediaFilters { get; init; } = [];
    /// <summary><c>duration:</c> — playback length in seconds (also accepts H:M:S).</summary>
    public SizeRange? Duration { get; init; }
    /// <summary><c>samplerate:</c> — audio sample rate in Hz.</summary>
    public SizeRange? SampleRate { get; init; }
    /// <summary><c>channels:</c> — audio channel count.</summary>
    public SizeRange? Channels { get; init; }
    /// <summary><c>bitrate:</c> — average audio bitrate in kbps (file size × 8 ÷ duration).</summary>
    public SizeRange? Bitrate { get; init; }

    public string? ContentSearch { get; init; }
    public IReadOnlyList<SearchClause> Clauses { get; init; } = [];

    /// <summary>Boolean expression tree for grouped (<c>&lt; &gt;</c>) queries.
    /// Non-null only when the query used grouping; when set it supersedes
    /// <see cref="Clauses"/> for term matching and the engines evaluate it
    /// instead. Filters (ext:/size:/…) still apply globally.</summary>
    public BoolExpr? TermExpr { get; init; }

    public bool IsEmpty => Clauses.Count == 0 && ExtWhitelist == null
        && !FileOnly && !FolderOnly && Size == null
        && Modified == null && Created == null && Accessed == null
        && Attribute == null && ChildOfPath == null && ParentIsPath == null
        && DupeMode == DupeKind.None && ContentSearch == null
        && StartsWith == null && EndsWith == null && WholeFilename == null
        && !RootOnly && NameLength == null && !EmptyOnly && MaxResults == null
        && ChildCount == null && ChildFileCount == null && ChildFolderCount == null
        && DateRun == null && RunCount == null && TermExpr == null && Depth == null
        && Width == null && Height == null && MediaFilters.Count == 0
        && Duration == null && SampleRate == null && Channels == null && Bitrate == null
        && Orientation == null;
}

/// <summary>Everything-compatible duplicate-grouping keys.</summary>
public enum DupeKind
{
    None,
    /// <summary><c>dupe:</c> — same filename (case-insensitive) AND same size.</summary>
    NameSize,
    /// <summary><c>sizedupe:</c> — same size.</summary>
    Size,
    /// <summary><c>namepartdupe:</c> — same name excluding extension.</summary>
    NamePart,
    /// <summary><c>attribdupe:</c> — same attribute set.</summary>
    Attrib,
}

public sealed record SearchClause(bool Negated, IReadOnlyList<string> Alternatives);

public sealed class SizeRange
{
    public SizeOp Op { get; }
    public ulong Low { get; }
    public ulong High { get; }

    public SizeRange(SizeOp op, ulong low, ulong high = 0)
    {
        Op = op; Low = low; High = high;
    }

    public bool Matches(ulong bytes) => Op switch
    {
        SizeOp.Equal          => bytes == Low,
        SizeOp.LessThan       => bytes < Low,
        SizeOp.LessOrEqual    => bytes <= Low,
        SizeOp.GreaterThan    => bytes > Low,
        SizeOp.GreaterOrEqual => bytes >= Low,
        SizeOp.Between        => bytes >= Low && bytes <= High,
        _ => true
    };
}

public enum SizeOp { Equal, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual, Between }

/// <summary>
/// Everything-style attribute filter. Each letter (r/h/s/a/d) must be present
/// when <see cref="Negated"/> is false, or absent when negated.
/// </summary>
public sealed class AttributeFilter
{
    public System.IO.FileAttributes Required { get; }
    public bool Negated { get; }

    public AttributeFilter(System.IO.FileAttributes required, bool negated)
    {
        Required = required;
        Negated  = negated;
    }

    public bool Matches(System.IO.FileAttributes actual)
    {
        var hasAll = (actual & Required) == Required;
        return Negated ? !hasAll : hasAll;
    }

    public static AttributeFilter? Parse(string spec)
    {
        spec = spec.Trim().ToLowerInvariant();
        if (spec.Length == 0) return null;
        bool negated = false;
        if (spec[0] == '-') { negated = true; spec = spec[1..]; }
        var attr = System.IO.FileAttributes.Normal & 0; // start at 0
        foreach (var c in spec)
        {
            switch (c)
            {
                case 'r': attr |= System.IO.FileAttributes.ReadOnly; break;
                case 'h': attr |= System.IO.FileAttributes.Hidden;   break;
                case 's': attr |= System.IO.FileAttributes.System;   break;
                case 'a': attr |= System.IO.FileAttributes.Archive;  break;
                case 'd': attr |= System.IO.FileAttributes.Directory; break;
                default: return null;
            }
        }
        return attr == 0 ? null : new AttributeFilter(attr, negated);
    }
}

public sealed class DateRange
{
    public DateTime Min { get; }   // inclusive
    public DateTime Max { get; }   // inclusive

    public DateRange(DateTime min, DateTime max)
    {
        Min = min;
        Max = max;
    }

    public bool Matches(DateTime t) => t >= Min && t <= Max;
}

public static class QueryParser
{
    public static readonly string[] AudioExts =
        ["mp3","flac","aac","wav","ogg","m4a","wma","opus","aiff","alac","ape","mid","midi","oga"];
    public static readonly string[] VideoExts =
        ["mp4","mkv","avi","mov","wmv","flv","webm","m4v","mpg","mpeg","ts","m2ts","mts","3gp","vob","ogv"];
    public static readonly string[] DocExts =
        ["doc","docx","xls","xlsx","ppt","pptx","pdf","txt","rtf","odt","ods","odp","md","csv","epub","html","htm"];
    public static readonly string[] PicExts =
        ["jpg","jpeg","png","gif","bmp","tif","tiff","webp","heic","svg","ico","raw","cr2","nef","psd","avif","dng"];
    public static readonly string[] ExeExts =
        ["exe","dll","msi","bat","cmd","ps1","com","scr","vbs","wsf"];
    public static readonly string[] ZipExts =
        ["zip","7z","rar","tar","gz","xz","bz2","zst","cab","iso","tgz","jar"];
    public static readonly string[] CodeExts =
        ["cs","c","cpp","cc","cxx","h","hpp","hh","hxx","py","js","jsx","ts","tsx",
         "go","rs","java","kt","swift","rb","php","pl","sh","ps1","sql","r","scala",
         "lua","dart","ex","exs","fs","fsx","clj","cljs","hs","ml","mli","jl","nim",
         "vb","asm","s","json","xml","yaml","yml","toml","ini"];

    public static ParsedQuery Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return ParsedQuery.Empty;

        var tokens = NormaliseBooleans(Tokenize(query.Trim()));

        bool caseSensitive = false;
        bool regexMode = false;
        bool wholeWord = false;
        bool matchPath = false;
        bool fileOnly = false;
        bool folderOnly = false;
        string[]? extWhitelist = null;
        SizeRange? sizeRange = null;
        DateRange? dmRange = null;
        DateRange? dcRange = null;
        DateRange? daRange = null;
        AttributeFilter? attribFilter = null;
        string? childOfPath  = null;
        string? parentIsPath = null;
        DupeKind dupeMode    = DupeKind.None;
        string? contentSearch = null;
        var startsWith  = new List<string>();
        var endsWith    = new List<string>();
        var wholeName   = new List<string>();
        bool rootOnly   = false;
        SizeRange? nameLen = null;
        bool emptyOnly  = false;
        int? maxResults = null;
        bool wildcards  = true;
        bool matchDiacritics = true;
        SizeRange? childCount = null;
        SizeRange? childFileCount = null;
        SizeRange? childFolderCount = null;
        SizeRange? depth = null;
        SizeRange? widthRange = null;
        SizeRange? heightRange = null;
        SizeRange? orientationRange = null;
        var mediaFilters = new List<MediaFilter>();
        SizeRange? durationRange = null;
        SizeRange? sampleRateRange = null;
        SizeRange? channelsRange = null;
        SizeRange? bitrateRange = null;
        DateRange? drRange = null;
        SizeRange? runCount = null;
        var clauses = new List<SearchClause>();

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token)) continue;
            var lower = token.ToLowerInvariant();

            // ── modifier: case ────────────────────────────────────────────
            if (lower == "case:" || lower == "case:true") { caseSensitive = true; continue; }
            if (lower == "nocase:" || lower == "case:false") { caseSensitive = false; continue; }
            if (lower.StartsWith("case:") && lower.Length > 5 && lower != "case:true" && lower != "case:false")
            {
                caseSensitive = true;
                AddTerm(clauses, token[5..], false);
                continue;
            }

            // ── modifier: regex ───────────────────────────────────────────
            if (lower == "regex:" || lower == "regex:true") { regexMode = true; continue; }
            if (lower == "noregex:" || lower == "regex:false") { regexMode = false; continue; }
            if (lower.StartsWith("regex:") && lower.Length > 6 && lower != "regex:true" && lower != "regex:false")
            {
                regexMode = true;
                // Support legacy "regex:true <pattern>" as one token (from old InProcEngineClient)
                var rest = token[6..];
                if (rest.StartsWith("true ", StringComparison.OrdinalIgnoreCase)) rest = rest[5..].TrimStart();
                if (!string.IsNullOrEmpty(rest)) AddTerm(clauses, rest, false);
                continue;
            }

            // ── modifier: whole word ──────────────────────────────────────
            if (lower == "word:" || lower == "ww:" || lower == "wholeword:" || lower == "word:true") { wholeWord = true; continue; }
            if (lower == "noword:" || lower == "noww:" || lower == "word:false") { wholeWord = false; continue; }
            if (lower.StartsWith("word:") && lower.Length > 5 && lower != "word:true" && lower != "word:false")
            {
                wholeWord = true;
                AddTerm(clauses, token[5..], false);
                continue;
            }
            if (lower.StartsWith("ww:") && lower.Length > 3 && lower != "ww:true" && lower != "ww:false")
            {
                wholeWord = true;
                AddTerm(clauses, token[3..], false);
                continue;
            }

            // ── modifier: match path ──────────────────────────────────────
            if (lower == "path:" || lower == "matchpath:" || lower == "matchpath:true") { matchPath = true; continue; }
            if (lower == "nopath:" || lower == "matchpath:false") { matchPath = false; continue; }
            // path:VALUE / matchpath:VALUE — enable match-path AND search VALUE,
            // mirroring case:/word:. Without this, "path:report" fell through to a
            // literal substring search for the token "path:report".
            if (lower.StartsWith("path:") && lower.Length > 5)
            {
                matchPath = true;
                AddTerm(clauses, token[5..], false);
                continue;
            }
            if (lower.StartsWith("matchpath:") && lower.Length > 10
                && lower != "matchpath:true" && lower != "matchpath:false")
            {
                matchPath = true;
                AddTerm(clauses, token[10..], false);
                continue;
            }

            // ── modifier: wildcards ───────────────────────────────────────
            if (lower == "wildcards:" || lower == "wildcards:true") { wildcards = true; continue; }
            if (lower == "nowildcards:" || lower == "wildcards:false") { wildcards = false; continue; }
            if (lower.StartsWith("wildcards:") && lower.Length > 10)
            {
                wildcards = true;
                AddTerm(clauses, token[10..], false);
                continue;
            }

            // ── modifier: diacritics ──────────────────────────────────────
            if (lower == "diacritics:" || lower == "diacritics:true") { matchDiacritics = true; continue; }
            if (lower == "nodiacritics:" || lower == "diacritics:false") { matchDiacritics = false; continue; }
            if (lower.StartsWith("diacritics:") && lower.Length > 11)
            {
                matchDiacritics = true;
                AddTerm(clauses, token[11..], false);
                continue;
            }

            // ── type: file / folder ───────────────────────────────────────
            if (lower == "file:" || lower == "files:") { fileOnly = true; continue; }
            if (lower.StartsWith("file:") && lower.Length > 5)
            {
                fileOnly = true;
                AddTerm(clauses, token[5..], false);
                continue;
            }
            if (lower == "folder:" || lower == "folders:" || lower == "nofileonly:") { folderOnly = true; continue; }
            if (lower.StartsWith("folder:") && lower.Length > 7)
            {
                folderOnly = true;
                AddTerm(clauses, token[7..], false);
                continue;
            }

            // ── extension filter ──────────────────────────────────────────
            if (lower.StartsWith("ext:") && lower.Length > 4)
            {
                extWhitelist = lower[4..].Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                continue;
            }

            // ── type macros ───────────────────────────────────────────────
            if (TryTypeMacro(lower, token, ref extWhitelist, clauses)) continue;

            // ── size filter ───────────────────────────────────────────────
            if (lower.StartsWith("size:") && lower.Length > 5)
            {
                sizeRange = ParseSize(lower[5..]);
                continue;
            }

            // ── date filters ──────────────────────────────────────────────
            if (lower.StartsWith("dm:") && lower.Length > 3)
            {
                dmRange = ParseDateSpec(lower[3..], DateTime.Now);
                continue;
            }
            if (lower.StartsWith("dc:") && lower.Length > 3)
            {
                dcRange = ParseDateSpec(lower[3..], DateTime.Now);
                continue;
            }
            if (lower.StartsWith("da:") && lower.Length > 3)
            {
                daRange = ParseDateSpec(lower[3..], DateTime.Now);
                continue;
            }
            if (lower.StartsWith("dr:") && lower.Length > 3)
            {
                drRange = ParseDateSpec(lower[3..], DateTime.Now);
                continue;
            }

            // ── run-count filter (rc: / runcount:) ───────────────────────
            if (lower.StartsWith("runcount:") && lower.Length > 9)
            {
                runCount = ParseIntExpression(lower[9..]);
                continue;
            }
            if (lower.StartsWith("rc:") && lower.Length > 3)
            {
                runCount = ParseIntExpression(lower[3..]);
                continue;
            }

            // ── attribute filter ──────────────────────────────────────────
            if (lower.StartsWith("attrib:") && lower.Length > 7)
            {
                attribFilter = AttributeFilter.Parse(lower[7..]);
                continue;
            }

            // ── path scope filters (preserve original casing of value) ────
            if (lower.StartsWith("child:") && token.Length > 6)
            {
                childOfPath = token[6..];
                continue;
            }
            // Everything's infolder:<path> is the recursive "anywhere under this
            // folder" filter — the same semantics as child:.
            if (lower.StartsWith("infolder:") && token.Length > 9)
            {
                childOfPath = token[9..];
                continue;
            }
            if (lower.StartsWith("parent:") && token.Length > 7)
            {
                parentIsPath = token[7..];
                continue;
            }

            // ── dupe: family (duplicate post-filter) ─────────────────────
            if (lower == "dupe:" || lower == "dupes:" || lower == "duplicates:")
            {
                dupeMode = DupeKind.NameSize;
                continue;
            }
            if (lower == "sizedupe:" || lower == "sizedupes:")
            {
                dupeMode = DupeKind.Size;
                continue;
            }
            if (lower == "namepartdupe:" || lower == "namepartdupes:")
            {
                dupeMode = DupeKind.NamePart;
                continue;
            }
            if (lower == "attribdupe:" || lower == "attribdupes:")
            {
                dupeMode = DupeKind.Attrib;
                continue;
            }

            // ── startwith: / endwith: / wfn: ─────────────────────────────
            if ((lower.StartsWith("startwith:") || lower.StartsWith("startswith:"))
                && token.Contains(':'))
            {
                var v = token[(token.IndexOf(':') + 1)..];
                if (v.Length > 0) startsWith.Add(v);
                continue;
            }
            if ((lower.StartsWith("endwith:") || lower.StartsWith("endswith:"))
                && token.Contains(':'))
            {
                var v = token[(token.IndexOf(':') + 1)..];
                if (v.Length > 0) endsWith.Add(v);
                continue;
            }
            if ((lower.StartsWith("wfn:") || lower.StartsWith("wholefilename:"))
                && token.Contains(':'))
            {
                var v = token[(token.IndexOf(':') + 1)..];
                if (v.Length > 0) wholeName.Add(v);
                continue;
            }

            // ── root: / empty: (no value) ────────────────────────────────
            if (lower == "root:") { rootOnly = true; continue; }
            if (lower == "empty:") { emptyOnly = true; continue; }

            // ── len: (filename length) ───────────────────────────────────
            if (lower.StartsWith("len:") && lower.Length > 4)
            {
                nameLen = ParseIntExpression(lower[4..]);
                continue;
            }

            // ── count: (result cap) ──────────────────────────────────────
            if (lower.StartsWith("count:") && lower.Length > 6)
            {
                if (int.TryParse(lower[6..], out var n) && n >= 0) maxResults = n;
                continue;
            }

            // ── childcount: family (folder child-count post-filter) ──────
            if (lower.StartsWith("childfilecount:") && lower.Length > 15)
            {
                childFileCount = ParseIntExpression(lower[15..]);
                continue;
            }
            if (lower.StartsWith("childfoldercount:") && lower.Length > 17)
            {
                childFolderCount = ParseIntExpression(lower[17..]);
                continue;
            }
            if (lower.StartsWith("childcount:") && lower.Length > 11)
            {
                childCount = ParseIntExpression(lower[11..]);
                continue;
            }

            // ── depth: / parents: (folder-depth post-filter) ─────────────
            if (lower.StartsWith("depth:") && lower.Length > 6)
            {
                depth = ParseIntExpression(lower[6..]);
                continue;
            }
            if (lower.StartsWith("parents:") && lower.Length > 8)
            {
                depth = ParseIntExpression(lower[8..]);
                continue;
            }

            // ── image dimensions (width: / height: / dimensions:) ────────
            if (lower.StartsWith("width:") && lower.Length > 6)
            {
                widthRange = ParseIntExpression(lower[6..]);
                continue;
            }
            if (lower.StartsWith("height:") && lower.Length > 7)
            {
                heightRange = ParseIntExpression(lower[7..]);
                continue;
            }
            if (lower.StartsWith("dimensions:") && lower.Length > 11)
            {
                ParseDimensions(lower[11..], ref widthRange, ref heightRange);
                continue;
            }
            if (lower.StartsWith("orientation:") && lower.Length > 12)
            {
                orientationRange = ParseIntExpression(lower[12..]);
                continue;
            }

            // ── audio tags (artist:/album:/title:/year:/genre:/track:/comment:) ──
            if (TryMediaFilter(lower, token, mediaFilters)) continue;

            // ── audio stream properties (duration:/samplerate:/channels:) ──
            if (lower.StartsWith("duration:") && lower.Length > 9)
            {
                durationRange = ParseDuration(lower[9..]);
                continue;
            }
            if (lower.StartsWith("samplerate:") && lower.Length > 11)
            {
                sampleRateRange = ParseIntExpression(lower[11..]);
                continue;
            }
            if (lower.StartsWith("channels:") && lower.Length > 9)
            {
                channelsRange = ParseIntExpression(lower[9..]);
                continue;
            }
            if (lower.StartsWith("bitrate:") && lower.Length > 8)
            {
                bitrateRange = ParseIntExpression(lower[8..]);
                continue;
            }

            // ── content: family (file-contents post-filter) ──────────────
            // Everything exposes encoding-specific aliases; WhereIsIt's reader
            // auto-detects encoding, so they all map to the same content search.
            if (TryContentAlias(lower, token, ref contentSearch)) continue;
            if (lower.StartsWith("content:") && token.Length > 8)
            {
                contentSearch = token[8..];
                continue;
            }

            // ── regular term (may have ! prefix or | alternatives) ────────
            var negated = token.StartsWith("!");
            var termStr = negated ? token[1..] : token;
            if (!string.IsNullOrEmpty(termStr))
                clauses.Add(new SearchClause(negated,
                    termStr.Split('|', StringSplitOptions.RemoveEmptyEntries)));
        }

        // Grouped boolean queries (< >) build an expression tree that supersedes
        // the flat clause list for term matching. Only engaged when a bracket is
        // present, so non-grouped queries keep the original fast path untouched.
        BoolExpr? termExpr = null;
        if (query.IndexOf('<') >= 0 || query.IndexOf('>') >= 0)
        {
            termExpr = BooleanQuery.TryParse(query);
            if (termExpr is not null) clauses.Clear();
        }

        return new ParsedQuery
        {
            CaseSensitive = caseSensitive,
            RegexMode = regexMode,
            WholeWord = wholeWord,
            MatchPath = matchPath,
            FileOnly = fileOnly,
            FolderOnly = folderOnly,
            ExtWhitelist = extWhitelist,
            Size = sizeRange,
            Modified = dmRange,
            Created  = dcRange,
            Accessed = daRange,
            Attribute = attribFilter,
            ChildOfPath  = childOfPath,
            ParentIsPath = parentIsPath,
            DupeMode     = dupeMode,
            ContentSearch = contentSearch,
            StartsWith    = startsWith.Count  > 0 ? startsWith.ToArray()  : null,
            EndsWith      = endsWith.Count    > 0 ? endsWith.ToArray()    : null,
            WholeFilename = wholeName.Count   > 0 ? wholeName.ToArray()   : null,
            RootOnly      = rootOnly,
            NameLength    = nameLen,
            EmptyOnly     = emptyOnly,
            MaxResults    = maxResults,
            Wildcards        = wildcards,
            MatchDiacritics  = matchDiacritics,
            ChildCount       = childCount,
            ChildFileCount   = childFileCount,
            ChildFolderCount = childFolderCount,
            Depth            = depth,
            Width            = widthRange,
            Height           = heightRange,
            Orientation      = orientationRange,
            MediaFilters     = mediaFilters.Count > 0 ? mediaFilters.ToArray() : [],
            Duration         = durationRange,
            SampleRate       = sampleRateRange,
            Channels         = channelsRange,
            Bitrate          = bitrateRange,
            DateRun          = drRange,
            RunCount         = runCount,
            TermExpr         = termExpr,
            Clauses = clauses,
        };
    }

    /// <summary>
    /// Maps Everything's encoding-specific content functions
    /// (<c>ansicontent:</c>, <c>utf8content:</c>, <c>utf16content:</c>,
    /// <c>utf16becontent:</c>) onto <see cref="ParsedQuery.ContentSearch"/>.
    /// The reader auto-detects encoding, so the search itself is identical.
    /// </summary>
    private static bool TryContentAlias(string lower, string token, ref string? contentSearch)
    {
        foreach (var prefix in ContentAliasPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length)
            {
                contentSearch = token[prefix.Length..];
                return true;
            }
        }
        return false;
    }

    private static readonly string[] ContentAliasPrefixes =
        ["ansicontent:", "utf8content:", "utf16becontent:", "utf16content:"];

    /// <summary>
    /// Parses an integer comparison expression (<c>N</c>, <c>&gt;N</c>, <c>&lt;=N</c>,
    /// <c>a..b</c>) for <c>len:</c>. Units are intentionally ignored — a filename
    /// length is a plain count, never kb/mb.
    /// </summary>
    private static SizeRange? ParseIntExpression(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return null;

        int dotdot = spec.IndexOf("..", StringComparison.Ordinal);
        if (dotdot > 0)
        {
            if (ulong.TryParse(spec[..dotdot], out var lo) &&
                ulong.TryParse(spec[(dotdot + 2)..], out var hi))
                return new SizeRange(SizeOp.Between, lo, hi);
            return null;
        }

        SizeOp op = SizeOp.Equal;
        string rest = spec;
        if (spec.StartsWith(">=")) { op = SizeOp.GreaterOrEqual; rest = spec[2..]; }
        else if (spec.StartsWith("<=")) { op = SizeOp.LessOrEqual; rest = spec[2..]; }
        else if (spec.StartsWith(">")) { op = SizeOp.GreaterThan; rest = spec[1..]; }
        else if (spec.StartsWith("<")) { op = SizeOp.LessThan; rest = spec[1..]; }
        else if (spec.StartsWith("=")) { op = SizeOp.Equal; rest = spec[1..]; }

        return ulong.TryParse(rest, out var v) ? new SizeRange(op, v) : null;
    }

    /// <summary>
    /// Extracts the plain literal terms a result-list highlighter should mark up
    /// in matched names: the positive (non-negated) clause alternatives, minus
    /// wildcard/regex patterns that wouldn't map to a simple substring span.
    /// Returns an empty list for regex queries (no literal span to highlight).
    /// </summary>
    public static IReadOnlyList<string> ExtractHighlightTerms(string query)
    {
        var parsed = Parse(query);
        if (parsed.RegexMode || parsed.Clauses.Count == 0) return [];

        var terms = new List<string>();
        foreach (var clause in parsed.Clauses)
        {
            if (clause.Negated) continue;
            foreach (var alt in clause.Alternatives)
            {
                if (string.IsNullOrEmpty(alt)) continue;
                if (alt.Contains('*') || alt.Contains('?')) continue; // wildcard, not a literal span
                terms.Add(alt);
            }
        }
        return terms;
    }

    /// <summary>
    /// Parses an Everything <c>dimensions:</c> value. <c>WxH</c> sets both to an
    /// exact match; a bare <c>N</c> or comparison applies to both width and
    /// height (so <c>dimensions:&gt;1000</c> means wide-and-tall).
    /// </summary>
    private static void ParseDimensions(string spec, ref SizeRange? width, ref SizeRange? height)
    {
        spec = spec.Trim();
        int x = spec.IndexOf('x');
        if (x < 0) x = spec.IndexOf('X');
        if (x > 0 && x < spec.Length - 1)
        {
            width = ParseIntExpression(spec[..x]);
            height = ParseIntExpression(spec[(x + 1)..]);
            return;
        }
        var both = ParseIntExpression(spec);
        width = both;
        height = both;
    }

    /// <summary>Parses a <c>duration:</c> value: an <c>H:M:S</c>/<c>M:S</c> clock
    /// form (exact match) or a plain seconds count / comparison via
    /// <see cref="ParseIntExpression"/>.</summary>
    private static SizeRange? ParseDuration(string spec)
    {
        spec = spec.Trim();
        if (spec.Contains(':'))
        {
            long secs = 0;
            foreach (var part in spec.Split(':'))
            {
                if (!long.TryParse(part, out var v) || v < 0) return null;
                secs = secs * 60 + v;
            }
            return new SizeRange(SizeOp.Equal, (ulong)secs);
        }
        return ParseIntExpression(spec);
    }

    private static readonly (string Prefix, MediaField Field)[] MediaPrefixes =
    [
        ("artist:", MediaField.Artist), ("album:", MediaField.Album), ("title:", MediaField.Title),
        ("genre:", MediaField.Genre), ("track:", MediaField.Track), ("comment:", MediaField.Comment),
        ("year:", MediaField.Year),
        ("author:", MediaField.Author), ("subject:", MediaField.Subject), ("keywords:", MediaField.Keywords),
    ];

    private static bool TryMediaFilter(string lower, string token, List<MediaFilter> into)
    {
        foreach (var (prefix, field) in MediaPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length)
            {
                into.Add(new MediaFilter(field, token[prefix.Length..]));
                return true;
            }
        }
        return false;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Folder depth = number of path separators in the full path, so a
    /// volume-root entry (<c>C:\file.txt</c>) is depth 1. Used by <c>depth:</c>/
    /// <c>parents:</c>.</summary>
    public static int FolderDepth(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return 0;
        int n = 0;
        foreach (var ch in fullPath)
            if (ch == '\\' || ch == '/') n++;
        return n;
    }

    /// <summary>
    /// Strips combining diacritical marks so accented characters fold to their
    /// base letter (é → e). Used by both engines for <c>nodiacritics:</c>
    /// matching. Returns the input unchanged when it has no non-ASCII chars,
    /// avoiding the Normalize allocation on the common all-ASCII hot path.
    /// </summary>
    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool hasNonAscii = false;
        foreach (var ch in text)
            if (ch > 127) { hasNonAscii = true; break; }
        if (!hasNonAscii) return text;

        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static bool TryTypeMacro(string lower, string token, ref string[]? extWhitelist, List<SearchClause> clauses)
    {
        if (lower == "audio:") { extWhitelist = AudioExts; return true; }
        if (lower.StartsWith("audio:") && lower.Length > 6) { extWhitelist = AudioExts; AddTerm(clauses, token[6..], false); return true; }
        if (lower == "video:") { extWhitelist = VideoExts; return true; }
        if (lower.StartsWith("video:") && lower.Length > 6) { extWhitelist = VideoExts; AddTerm(clauses, token[6..], false); return true; }
        if (lower == "doc:") { extWhitelist = DocExts; return true; }
        if (lower.StartsWith("doc:") && lower.Length > 4) { extWhitelist = DocExts; AddTerm(clauses, token[4..], false); return true; }
        if (lower == "pic:" || lower == "image:") { extWhitelist = PicExts; return true; }
        if (lower.StartsWith("pic:") && lower.Length > 4) { extWhitelist = PicExts; AddTerm(clauses, token[4..], false); return true; }
        if (lower == "exe:") { extWhitelist = ExeExts; return true; }
        if (lower.StartsWith("exe:") && lower.Length > 4) { extWhitelist = ExeExts; AddTerm(clauses, token[4..], false); return true; }
        if (lower == "zip:" || lower == "compressed:") { extWhitelist = ZipExts; return true; }
        if (lower.StartsWith("zip:") && lower.Length > 4) { extWhitelist = ZipExts; AddTerm(clauses, token[4..], false); return true; }
        if (lower == "code:" || lower == "source:") { extWhitelist = CodeExts; return true; }
        if (lower.StartsWith("code:") && lower.Length > 5) { extWhitelist = CodeExts; AddTerm(clauses, token[5..], false); return true; }
        return false;
    }

    private static void AddTerm(List<SearchClause> clauses, string term, bool negated)
    {
        if (string.IsNullOrEmpty(term)) return;
        var alts = term.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (alts.Length > 0)
            clauses.Add(new SearchClause(negated, alts));
    }

    /// <summary>
    /// Rewrites Everything-style boolean keywords (case-insensitive) into the
    /// existing punctuation form so the rest of the parser is unchanged:
    /// <c>AND</c> is dropped (already implicit), <c>NOT term</c> becomes
    /// <c>!term</c>, and <c>a OR b</c> becomes <c>a|b</c>.
    /// </summary>
    private static List<string> NormaliseBooleans(List<string> tokens)
    {
        var output = new List<string>(tokens.Count);
        bool negateNext = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var upper = t.ToUpperInvariant();

            if (upper == "AND") { continue; }
            if (upper == "NOT") { negateNext = true; continue; }

            if (upper == "OR" && output.Count > 0 && i + 1 < tokens.Count)
            {
                var prev = output[^1];
                var next = tokens[i + 1];
                // Only fold two plain terms into the a|b alternative form. A
                // modifier token (ext:, size:, …) or a negated term can't be
                // OR-folded into one token without corrupting it — e.g.
                // "ext:cs OR txt" must NOT become the bogus extension "cs|txt".
                // For those, drop the OR and let the neighbours stand as
                // implicit-AND terms (peek, don't consume, so no term is lost).
                if (!prev.StartsWith('!') && !next.StartsWith('!')
                    && !prev.Contains(':') && !next.Contains(':'))
                {
                    output[^1] = prev + "|" + next;
                    i++; // consume the folded next term
                }
                continue; // the OR keyword itself is never emitted as a term
            }

            if (negateNext)
            {
                negateNext = false;
                if (!t.StartsWith('!')) output.Add("!" + t);
                else output.Add(t);
            }
            else
            {
                output.Add(t);
            }
        }
        return output;
    }

    private static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        int i = 0;
        while (i < query.Length)
        {
            char c = query[i];
            if (c == '"')
            {
                // A quote only groups when it has a matching close quote. A lone,
                // unterminated quote is skipped so it cannot swallow the rest of
                // the query (and any following modifier tokens) into one literal
                // term — e.g. `report" ext:cs` must still parse the ext: filter.
                int close = query.IndexOf('"', i + 1);
                if (close < 0) { i++; continue; }
                current.Append(query, i + 1, close - (i + 1));  // spaces preserved
                i = close + 1;
                continue;
            }
            if (c == ' ')
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                i++;
                continue;
            }
            current.Append(c);
            i++;
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static SizeRange? ParseSize(string spec)
    {
        spec = spec.Trim();
        var lower = spec.ToLowerInvariant();

        return lower switch
        {
            "empty"    => new SizeRange(SizeOp.Equal, 0),
            "tiny"     => new SizeRange(SizeOp.Between, 1, 10 * 1024),
            "small"    => new SizeRange(SizeOp.Between, 10 * 1024 + 1, 100 * 1024),
            "medium"   => new SizeRange(SizeOp.Between, 100 * 1024 + 1, 1024 * 1024),
            "large"    => new SizeRange(SizeOp.Between, 1024 * 1024 + 1, 16 * 1024 * 1024),
            "huge"     => new SizeRange(SizeOp.Between, 16 * 1024 * 1024 + 1, 128 * 1024 * 1024),
            "gigantic" => new SizeRange(SizeOp.GreaterThan, 128 * 1024 * 1024),
            _ => ParseSizeExpression(lower)
        };
    }

    private static SizeRange? ParseSizeExpression(string spec)
    {
        // Range with ".." separator: 1kb..10mb
        int dotdot = spec.IndexOf("..", StringComparison.Ordinal);
        if (dotdot > 0)
        {
            if (TryParseSizeBytes(spec[..dotdot], out ulong lo) &&
                TryParseSizeBytes(spec[(dotdot + 2)..], out ulong hi))
                return new SizeRange(SizeOp.Between, lo, hi);
        }

        // Comparison operators
        SizeOp op = SizeOp.Equal;
        string rest = spec;
        if (spec.StartsWith(">=")) { op = SizeOp.GreaterOrEqual; rest = spec[2..]; }
        else if (spec.StartsWith("<=")) { op = SizeOp.LessOrEqual; rest = spec[2..]; }
        else if (spec.StartsWith(">")) { op = SizeOp.GreaterThan; rest = spec[1..]; }
        else if (spec.StartsWith("<")) { op = SizeOp.LessThan; rest = spec[1..]; }
        else if (spec.StartsWith("=")) { op = SizeOp.Equal; rest = spec[1..]; }

        return TryParseSizeBytes(rest, out ulong bytes)
            ? new SizeRange(op, bytes)
            : null;
    }

    public static DateRange? ParseDateSpec(string spec, DateTime now)
    {
        spec = spec.Trim().ToLowerInvariant();
        if (spec.Length == 0) return null;

        // Keyword: today/yesterday/thisweek/lastweek/thismonth/lastmonth/thisyear/lastyear
        var keyword = TryKeyword(spec, now);
        if (keyword is not null) return keyword;

        // Relative span: "3days", "last2weeks", "past6months", "next1year", … —
        // a rolling window relative to now (past unless next/coming).
        var relative = TryRelativeSpan(spec, now);
        if (relative is not null) return relative;

        // Range with ".." separator
        int dotdot = spec.IndexOf("..", StringComparison.Ordinal);
        if (dotdot > 0)
        {
            var lo = ParsePoint(spec[..dotdot], lower: true);
            var hi = ParsePoint(spec[(dotdot + 2)..], lower: false);
            if (lo is not null && hi is not null) return new DateRange(lo.Value, hi.Value);
            return null;
        }

        // Comparison operators
        if (spec.StartsWith(">="))
        {
            var d = ParsePoint(spec[2..], lower: true);
            return d is null ? null : new DateRange(d.Value, DateTime.MaxValue);
        }
        if (spec.StartsWith("<="))
        {
            var d = ParsePoint(spec[2..], lower: false);
            return d is null ? null : new DateRange(DateTime.MinValue, d.Value);
        }
        if (spec.StartsWith(">"))
        {
            var d = ParsePoint(spec[1..], lower: false);
            if (d is null) return null;
            return new DateRange(d.Value.AddTicks(1), DateTime.MaxValue);
        }
        if (spec.StartsWith("<"))
        {
            var d = ParsePoint(spec[1..], lower: true);
            if (d is null) return null;
            return new DateRange(DateTime.MinValue, d.Value.AddTicks(-1));
        }

        // Bare literal — match the whole period (year / month / day)
        var (low, high) = ParsePointAsRange(spec);
        return (low is null || high is null) ? null : new DateRange(low.Value, high.Value);
    }

    // "[last|past|prev|previous|next|coming] N unit[s]" collapsed to one token
    // (the tokenizer splits on spaces, so multi-word forms must be quoted).
    private static readonly System.Text.RegularExpressions.Regex RelativeSpanRx =
        new(@"^(last|past|prev|previous|next|coming)?\s*(\d{1,6})\s*(minute|min|hour|hr|day|week|wk|month|mon|year|yr)s?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static DateRange? TryRelativeSpan(string spec, DateTime now)
    {
        var m = RelativeSpanRx.Match(spec);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[2].Value, out var n) || n <= 0) return null;

        bool future = m.Groups[1].Value is "next" or "coming";
        var unit = m.Groups[3].Value;

        DateTime Shift(DateTime t, int sign) => unit switch
        {
            "minute" or "min"  => t.AddMinutes(sign * n),
            "hour" or "hr"     => t.AddHours(sign * n),
            "day"              => t.AddDays(sign * n),
            "week" or "wk"     => t.AddDays(sign * 7 * n),
            "month" or "mon"   => t.AddMonths(sign * n),
            "year" or "yr"     => t.AddYears(sign * n),
            _                  => t,
        };

        return future
            ? new DateRange(now, Shift(now, +1))
            : new DateRange(Shift(now, -1), now);
    }

    private static DateRange? TryKeyword(string spec, DateTime now)
    {
        var today = now.Date;
        var kw = spec switch
        {
            "today"     => new DateRange(today, today.AddDays(1).AddTicks(-1)),
            "yesterday" => new DateRange(today.AddDays(-1), today.AddTicks(-1)),
            "tomorrow"  => new DateRange(today.AddDays(1), today.AddDays(2).AddTicks(-1)),
            "thisweek"  => WeekRange(today, 0),
            "lastweek"  => WeekRange(today, -1),
            "thismonth" => MonthRange(today.Year, today.Month),
            "lastmonth" => MonthOffsetRange(today, -1),
            "thisyear"  => YearRange(today.Year),
            "lastyear"  => YearRange(today.Year - 1),
            // Rolling "past N to now" ranges (distinct from the calendar-aligned
            // last* keywords above), matching Everything's past* functions.
            "pastweek"  => new DateRange(today.AddDays(-7), now),
            "pastmonth" => new DateRange(today.AddMonths(-1), now),
            "pastyear"  => new DateRange(today.AddYears(-1), now),
            _ => (DateRange?)null
        };
        if (kw is not null) return kw;

        // Month names (january…december and the jan…dec abbreviations) resolve
        // to that month of the current year, matching Everything's <month>
        // date function.
        if (MonthNames.TryGetValue(spec, out var month))
            return MonthRange(today.Year, month);

        // Weekday names (monday…sunday and mon…sun) resolve to the most recent
        // occurrence of that weekday, including today — Everything's <weekday>
        // date function.
        if (WeekdayNames.TryGetValue(spec, out var dow))
        {
            int back = ((int)today.DayOfWeek - (int)dow + 7) % 7;
            var day = today.AddDays(-back);
            return new DateRange(day, day.AddDays(1).AddTicks(-1));
        }

        return null;
    }

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.Ordinal)
    {
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday,
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
    };

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.Ordinal)
    {
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
    };

    private static DateRange WeekRange(DateTime today, int weekOffset)
    {
        // Treat Monday as start of week.
        int dow = ((int)today.DayOfWeek + 6) % 7; // Mon=0 … Sun=6
        var monday = today.AddDays(-dow + (weekOffset * 7));
        return new DateRange(monday, monday.AddDays(7).AddTicks(-1));
    }

    private static DateRange MonthRange(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        return new DateRange(start, start.AddMonths(1).AddTicks(-1));
    }

    private static DateRange MonthOffsetRange(DateTime today, int offset)
    {
        var firstOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var start = firstOfThisMonth.AddMonths(offset);
        return new DateRange(start, start.AddMonths(1).AddTicks(-1));
    }

    private static DateRange YearRange(int year)
    {
        var start = new DateTime(year, 1, 1);
        return new DateRange(start, start.AddYears(1).AddTicks(-1));
    }

    /// <summary>
    /// Parses a date literal as a single point. When the literal is a
    /// year or year-month, returns the start (lower=true) or end (lower=false)
    /// of that period.
    /// </summary>
    private static DateTime? ParsePoint(string spec, bool lower)
    {
        var (lo, hi) = ParsePointAsRange(spec);
        return lower ? lo : hi;
    }

    private static (DateTime? Low, DateTime? High) ParsePointAsRange(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return (null, null);

        // Full date: YYYY-MM-DD
        if (DateTime.TryParseExact(spec, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
        {
            return (d, d.AddDays(1).AddTicks(-1));
        }

        // Year-month: YYYY-MM
        if (DateTime.TryParseExact(spec, "yyyy-MM",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var ym))
        {
            return (ym, ym.AddMonths(1).AddTicks(-1));
        }

        // Year only: YYYY
        if (spec.Length == 4 && int.TryParse(spec, out var year) && year >= 1 && year <= 9999)
        {
            var start = new DateTime(year, 1, 1);
            return (start, start.AddYears(1).AddTicks(-1));
        }

        return (null, null);
    }

    private static bool TryParseSizeBytes(string s, out ulong bytes)
    {
        s = s.Trim().ToLowerInvariant();
        ulong mult = 1;
        if (s.EndsWith("gb", StringComparison.Ordinal)) { mult = 1024UL * 1024 * 1024; s = s[..^2]; }
        else if (s.EndsWith("mb", StringComparison.Ordinal)) { mult = 1024UL * 1024; s = s[..^2]; }
        else if (s.EndsWith("kb", StringComparison.Ordinal)) { mult = 1024; s = s[..^2]; }
        else if (s.EndsWith("b", StringComparison.Ordinal)) { s = s[..^1]; }

        if (ulong.TryParse(s, out ulong v)) { bytes = v * mult; return true; }
        bytes = 0;
        return false;
    }
}
