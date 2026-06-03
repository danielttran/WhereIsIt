# WhereIsIt ⇄ Everything — Feature Parity Audit

> Audit date: **2026-06-03**. Reference: voidtools **Everything** 1.4 (stable)
> with notes on 1.5 alpha features. This document is the parity scorecard;
> `STATUS.md` remains the build/architecture source of truth.

**Legend**

- ✅ **Done** — implemented and at parity.
- 🟡 **Partial** — works for the common case; edge/notes called out.
- ➕ **Added this audit** — closed during the 2026-06-03 parity pass.
- ⛔ **Skipped** — deliberate non-goal (reason given); needs Windows-only
  infra, a proprietary protocol, or shell/registry integration that the
  project chose not to ship.
- ⭐ **WhereIsIt does it better / differently** — a deliberate divergence.

---

## 1. Search syntax — modifiers

| Everything | WhereIsIt | Notes |
|---|---|---|
| `case:` / `nocase:` | ✅ | |
| `wholeword:` / `ww:` | ✅ | |
| `path:` / `nopath:` (match path) | ✅ | |
| `regex:` | ✅ | per-match timeout guards catastrophic backtracking ⭐ |
| `wildcards:` / `nowildcards:` | ➕ | literal `*`/`?` when off; default on |
| `diacritics:` / `nodiacritics:` | ➕ | folds accents in substring + whole-word paths; native engine gets the `diacritics:false` hint so its candidate set is the needed superset |
| `ascii:` / encoding toggles | ⛔ | WhereIsIt auto-detects encoding instead |

**Default difference:** Everything's *Match Diacritics* defaults **off**
(accent-insensitive). WhereIsIt defaults **on** (accent-sensitive) and exposes
`nodiacritics:` to opt in. Flagged here as a deliberate, documented difference;
flip `MatchDiacritics`'s default in `ParsedQuery` if exact parity is desired.

## 2. Search syntax — functions / filters

| Everything | WhereIsIt | Notes |
|---|---|---|
| `ext:` | ✅ | `;`-separated list |
| `size:` (`>`,`<`,`..`, kb/mb/gb) | ✅ | |
| size keywords (empty/tiny/small/medium/large/huge/gigantic) | ✅ | `unknown` n/a (always known) |
| `dm:` date modified | ✅ | |
| `dc:` date created | ✅ | |
| `da:` date accessed | ✅ | |
| `dr:` date run | ➕ | backed by `RunCountService` last-run timestamps (persisted in settings) via the decorator's path-keyed lookup |
| `rc:` / `runcount:` | ➕ | backed by `RunCountService` open counts via the decorator's path-keyed lookup |
| date keywords (today/yesterday/tomorrow, this-/last-/past- periods, month + weekday names, relative spans) | ✅ | ➕ `tomorrow`, month + weekday names, rolling `past*`, and relative spans (`3days`/`last2weeks`/`past6months`/`next1year`). Only the space-separated phrasing `dm:last 3 days` needs quoting — the collapsed `last3days` form works unquoted |
| date ranges `a..b`, comparisons | ✅ | |
| `attrib:` / `attributes:` (rhsad) | ✅ | |
| `child:<path>` | ✅ | |
| `parent:<path>` | ✅ | |
| `root:` | ✅ | volume + UNC share roots |
| `empty:` | ✅ | zero-byte files + childless folders |
| `len:` filename length | ✅ | |
| `count:<n>` cap | ✅ | |
| `dupe:` / `sizedupe:` / `namepartdupe:` / `attribdupe:` | ✅ | |
| `startwith:` / `endwith:` | ✅ | |
| `wfn:` / `wholefilename:` | ✅ | |
| `content:` | ✅ | streamed, cross-chunk overlap, size-capped |
| `ansicontent:`/`utf8content:`/`utf16content:`/`utf16becontent:` | ➕ | map onto `content:` (reader auto-detects encoding) |
| `childcount:` / `childfilecount:` / `childfoldercount:` | ➕ | folder child-count filters |
| `file:` / `folder:` | ✅ | |
| type macros `audio:`/`video:`/`doc:`/`pic:`/`exe:`/`zip:` | ✅ | plus `code:`/`source:` ⭐ |
| `type:<regtype>` (registry file type) | ⛔ | needs the Windows registry file-type DB |
| `depth:` / `parents:<n>` (folder depth) | ➕ | depth = number of path separators in the full path (volume-root entry = depth 1); `parents:` is an alias |
| `frn:` file reference number | ⛔ | niche NTFS internal id |
| `infolder:` | ✅ | ➕ explicit alias for `child:` (recursive "anywhere under this folder") |
| image dimensions `width:` / `height:` / `dimensions:` | ➕ | dependency-free header reader (`ImageDimensions`) for PNG/JPEG/GIF/BMP/WEBP; post-filtered per row |
| audio tags `artist:`,`album:`,`title:`,`year:`,`genre:`,`track:`,`comment:` | ➕ | dependency-free `AudioTags` reader — MP3 (ID3v2.3/2.4 + ID3v1), FLAC + OGG (Vorbis comments), and M4A/MP4 (iTunes atoms); matched as case-insensitive substrings |
| audio stream `duration:` / `samplerate:` / `channels:` / `bitrate:` | ➕ | FLAC STREAMINFO (rate/channels/duration) + M4A `mvhd` (duration); `bitrate:` is the average (size × 8 ÷ duration, kbps); `duration:` accepts seconds or `H:M:S` |
| document properties `author:`/`subject:`/`keywords:`/`title:`/`comment:` | ➕ | OOXML core props (.docx/.xlsx/.pptx via ZIP + Dublin-Core) and best-effort PDF `/Info`; unified with audio tags through `MediaProperties` |
| image `orientation:` (EXIF) | ➕ | reads the JPEG EXIF Orientation tag (1–8) |

## 3. Boolean / grouping

| Everything | WhereIsIt | Notes |
|---|---|---|
| implicit AND (space) | ✅ | |
| `OR` / `|` | ✅ | keyword and `|` alternative form |
| `NOT` / `!` | ✅ | |
| `AND` keyword | ✅ | |
| `<` `>` explicit grouping / precedence | ➕ | boolean expression tree (`BooleanQuery`) evaluated in both engines; activates only when a bracket is present. Supports terms **and** functions as leaves, so function-level OR works via groups (`<ext:cs>\|<ext:txt>`, `<ext:cs alpha>`). Cross-row functions (`dupe:`/`count:`) stay global. Bracketless function OR (`ext:cs \| ext:txt`) still needs brackets |
| quoted phrases `"..."` | ✅ | unterminated-quote-safe ⭐ |
| operators `==` `<` `>` `<=` `>=` on functions | ✅ | size/date/len/childcount |

## 4. Sorting & columns

| Everything | WhereIsIt | Notes |
|---|---|---|
| sort by name/path/size/modified | ✅ | click-to-sort, asc/desc |
| sort by created/accessed | ✅ | native schema v10 stores all three timestamps |
| sort by extension(type)/attributes | ✅ | |
| run-count column | ✅ | persisted `RunCountService` ⭐ |
| add/remove/reorder/resize columns | 🟡 | Created/Accessed/Runs toggle + thumbnail gutter; arbitrary column reorder/resize not yet |
| custom property columns | ⛔ | tied to property index (§9) |
| `sort:` in query | ✅ | native `sort:asc`/`sort:desc` |

## 5. UI / shell

| Everything | WhereIsIt | Notes |
|---|---|---|
| instant-as-you-type results | ✅ | 75 ms throttle, seq-fenced decorator ⭐ |
| menu bar (File/Edit/Search/Bookmarks/View/Tools/Help) | ✅ | |
| result context menu (open / open path / copy name / copy full path / rename / recycle / properties) | ✅ | |
| Explorer shell context menu integration | ⛔ | needs a Windows shell extension |
| drag & drop to Explorer/editors | ✅ | |
| tabs | ✅ | TabView + restore-previous-tabs prompt ⭐ |
| bookmarks | ✅ | |
| search history (↑/↓ recall) | ✅ | |
| quick-filter bar (Everything/Audio/Video/Doc/Pic/Exe/Zip/Folder) | ✅ | plus Code ⭐ |
| thumbnails view | ✅ | Off/Small/Medium/Large/XL |
| preview pane | ➕ | toggleable right-hand pane (View → Preview pane) showing image thumbnails, a text head, and file info for the selected row; needs a Windows build to smoke-test |
| match highlighting in results | ➕ | literal query terms highlighted in the Name column via WinUI `TextHighlighter` ranges |
| system tray / minimize to tray | ➕ | dependency-free `Shell_NotifyIcon` tray host; minimize hides to tray, left-click/menu restores |
| global hotkey | ✅ | configurable, default Ctrl+Alt+W |
| run on startup | ✅ | |
| command-line args (`-s`, `-p`) | ✅ | |
| status bar (count + status) | ✅ | |
| Mica / modern backdrop | ⭐ | WinUI 3 Mica — newer than Everything's Win32 UI |

## 6. Index / engine

| Everything | WhereIsIt | Notes |
|---|---|---|
| NTFS USN journal + MFT indexing | ✅ | native C++ engine |
| live incremental updates | ✅ | |
| non-admin via background service | ⛔ | runs in-process w/ elevation; pipe service is a stub (STATUS §What's left) |
| folder indexing (non-NTFS / network) | 🟡 | `Directory.EnumerateFiles` fallback works but isn't a persistent folder index |
| ReFS / FAT indexing | ⛔ | NTFS only |
| file-list (.efu) as an index source | 🟡 | import/export ✅; not mountable as a live index |
| file-property / fast-sort metadata index | ⛔ | enables §2 property functions + custom columns |

## 7. Import / export

| Everything | WhereIsIt | Notes |
|---|---|---|
| export EFU (Everything File List) | ✅ | round-trips with voidtools (FILETIME + attr mask) |
| import EFU | ✅ | |
| export CSV / TSV / TXT | ✅ | + CSV formula-injection guard ⭐ |
| export to clipboard | 🟡 | copy name / copy full path; no bulk "copy as list" |

## 8. Servers / integration

| Everything | WhereIsIt | Notes |
|---|---|---|
| HTTP server (web UI) | ✅ | ➕ serves an HTML search page at `/` plus the JSON `/search?q=` endpoint; localhost-only by design (no LAN binding — security choice ⭐) |
| ETP / FTP server | ⛔ | proprietary protocol; HTTP covers cross-device search |
| Everything service | ⛔ | see §6 non-admin |
| `es.exe` CLI | 🟡 | ➕ `tools/WhereIsIt.Es` builds `es.exe` — same engine + query syntax, output/sort/export/modifier flags. Searches in-proc (one-shot) rather than over Everything's live-index IPC |
| IPC / SDK (DLL + messages) | ⛔ | needs Everything's undocumented WM_COPYDATA/IPC binary protocol for third-party interop |
| URL protocol / "Search Everything" shell verb | ⛔ | shell integration |

## 9. Remaining gaps

The entire **search/query/property surface is now at parity** (§1–3 + the
property functions in §2 cover every Everything search function implementable as
dependency-free logic: filters, modifiers, the full date-keyword surface,
`< >` grouping with function leaves, run metadata, image dimensions +
orientation, audio tags + stream properties across MP3/FLAC/OGG/M4A, and
document properties for OOXML + PDF).

What's left is **not query behaviour** — it's UI/native/integration
infrastructure plus one marginal convenience:

| Remaining item | Hard blocker |
|---|---|
| **Custom property columns** | Dynamic WinUI `ListView` columns bound to the metadata readers; needs a build to do safely |
| **Everything-compatible IPC SDK** | Byte-compatible reimplementation of Everything's **undocumented WM_COPYDATA/IPC protocol** so third-party tools interop (a native `es.exe` CLI ships in `tools/WhereIsIt.Es`) |
| **ETP / FTP server** | **Undocumented proprietary protocol** |
| **Shell context-menu extension** | A separate registered **Windows COM** component |
| **Background (non-admin) service** | A **Windows service** hosting the native engine |
| Bracketless function OR (`ext:cs \| ext:txt`) | Works *with* brackets today; marginal tokenizer change otherwise |

These cannot be implemented **and verified** without a Windows .NET 10 / WinUI /
MSVC build environment, and several (ETP/FTP, IPC SDK) target undocumented
proprietary Everything protocols. They are tracked here rather than landed as
unbuildable, untested, or guessed code.

## 10. Where WhereIsIt is intentionally *better*

- Modern WinUI 3 shell (Mica, tabs with session restore, thumbnails view).
- `code:` / `source:` quick filter and macro (not in Everything).
- Catastrophic-backtracking-safe regex (per-match timeout) in all engines.
- CSV/TSV formula-injection hardening on export.
- HTTP server is localhost-bound by default (safer out of the box).
- Unterminated-quote-safe tokenizer (a lone `"` can't swallow the query).

---

*Verification note:* this audit pass was authored on a Linux session without
the .NET 10 / WinUI / MSVC toolchain. The query-layer additions (`wildcards:`,
`nodiacritics:`, content aliases, `childcount:` family, month-name dates,
`ExtractHighlightTerms`) ship with xUnit coverage in
`tests/app/QueryParserExtendedTests.cs`,
`tests/app/InProcEngineClientExtendedFilterTests.cs`, and
`tests/app/ViewModels/ResultsListViewModelTests.cs`. The two WinUI pieces —
match highlighting (`SearchHighlighter.cs` attached property + MainWindow.xaml)
and the system-tray host (`TrayIconHost.cs` + MainWindow minimize-to-tray) —
are P/Invoke / XAML and **must be built and smoke-tested on Windows**; they
could not be compiled in this Linux session.
