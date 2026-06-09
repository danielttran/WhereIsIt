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
| add/remove columns | ✅ | Created/Accessed/Runs + Dimensions/Artist/Album/Author toggles (View menu), thumbnail gutter |
| resize columns | ➕ | drag the header grippers (Size/Modified/Type/Attr); widths persist. Free drag-*reorder* still needs a `DataGrid` (which would regress drag-to-Explorer) |
| custom property columns | ➕ | Dimensions/Artist/Album/Author columns (View menu), lazily read per visible row via the image/audio/document property readers |
| `sort:` in query | ✅ | native `sort:asc`/`sort:desc` |

## 5. UI / shell

| Everything | WhereIsIt | Notes |
|---|---|---|
| instant-as-you-type results | ✅ | 75 ms throttle, seq-fenced decorator ⭐ |
| menu bar (File/Edit/Search/Bookmarks/View/Tools/Help) | ✅ | |
| result context menu (open / open path / copy name / copy full path / rename / recycle / properties) | ✅ | |
| Explorer shell context menu integration | ➕ | "Search with WhereIsIt" on folders/drives/background via the classic registry shell verb (`ShellMenuRegistration`, no COM); launches `-p <path>`. Toggle in Tools menu. Win11 shows it under "Show more options" |
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
| non-admin via background service | ➕ | `tools/WhereIsIt.EngineService` — a real Windows service (generic host + `UseWindowsService`) hosting the engine behind a named pipe (`EnginePipeServer`); a non-elevated app connects via the real `NamedPipeEngineClient`. Also runs as a console |
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
| FTP server | ➕ | read-only RFC 959 FTP (`FtpServer`: USER/PASV/LIST/NLST/RETR/SIZE/CWD), localhost-only + opt-in, directory-traversal sandboxed. Enable via `settings.json` (`EnableFtpServer`) |
| ETP server | ➕ | `FtpServer` (with an engine) speaks Everything's ETP extension — `EVERYTHING SEARCH`/`QUERY`/`RESULT_OFFSET`/`MAX_RESULTS`/sort+column directives over FTP, streaming matching full paths. Localhost-only + opt-in (`EnableFtpServer`); the exact result-column framing should be confirmed against a real Everything ETP client |
| Everything service | ➕ | `tools/WhereIsIt.EngineService` — engine-over-named-pipe host (see §6) |
| `es.exe` CLI | 🟡 | ➕ `tools/WhereIsIt.Es` builds `es.exe` — same engine + query syntax, output/sort/export/modifier flags. Searches in-proc (one-shot) rather than over Everything's live-index IPC |
| IPC / SDK (WM_COPYDATA) | 🟡 | ➕ `EverythingIpcServer` implements the public Everything IPC SDK `WM_COPYDATA` query/list layout so Everything SDK clients / `es.exe` can query WhereIsIt (opt-in via `EnableEverythingIpc`). Built from the documented SDK — needs a Windows build + an SDK-client to verify the exact window-class/struct match |
| "Search WhereIsIt" shell verb | ➕ | registry context-menu verb (see §5) |

## 9. Remaining items

**Every Everything feature category now has an implementation in WhereIsIt**, and
the entire query/logic layer is **build- and test-verified on .NET 10** (see the
verification note below). What remains is *verification* of the Windows-only code
(WinUI app, native engine, and the two interop protocols' exact wire framing) on
a Windows toolchain, plus one deliberate UI trade-off:

| Item | Status |
|---|---|
| **Bracketless function OR** (`ext:cs \| ext:txt`) | ✅ **Implemented + verified** on .NET 10 (engages the boolean tree on a standalone `\|`; bare `a\|b` stays the flat form). |
| **Everything-wire IPC** (`WM_COPYDATA`) | Implemented (`EverythingIpcServer`, public SDK layout). Confirm window-class + struct packing against `Everything_IPC.h` with a real SDK client on Windows. |
| **ETP server** | Implemented + the command round-trip is **tested** (`FtpServer` + `EVERYTHING …`). Confirm the result-column wire framing against a real Everything ETP client on Windows. |
| **Win11 primary context menu** | Best-effort `IExplorerCommand` COM handler (`ExplorerCommandHandler.cs`) — **unverified**; needs a Windows build + a sparse MSIX package to register (see the file header). The classic registry verb ships and works today. |
| **Column drag-reorder** | Resize ships (header grippers) + add/remove toggles. Free drag-*reorder* would need a `DataGrid` that **regresses** WhereIsIt's drag-to-Explorer — a deliberate trade-off (a "does it better" case). |

The WinUI app, native engine, the `WM_COPYDATA` IPC window, and the COM handler
follow documented APIs/SDK but **need a Windows .NET 10 / WinUI / MSVC + MSIX
build to compile and verify**. The cross-platform query/IPC/FTP/ETP logic is
already verified (below).

## 10. Where WhereIsIt is intentionally *better*

- Modern WinUI 3 shell (Mica, tabs with session restore, thumbnails view).
- `code:` / `source:` quick filter and macro (not in Everything).
- Catastrophic-backtracking-safe regex (per-match timeout) in all engines.
- CSV/TSV formula-injection hardening on export.
- HTTP server is localhost-bound by default (safer out of the box).
- Unterminated-quote-safe tokenizer (a lone `"` can't swallow the query).

---

*Verification note:* the **logic layer is build- and test-verified on both
Linux (cross-target) and Windows (full toolchain)**. The .NET 10 SDK was
installed in-session on Linux; `WhereIsIt.App.Core`, `WhereIsIt.Pipe.Client`,
and the xUnit project were compiled with `-p:EnableWindowsTargeting=true` and
ran with **436 / 436 pass**. The subsequent **2026-06-08 Windows pass** (VS
2026 Professional, .NET 10.0.300 SDK) then built the C++ engine, the WinUI
app, the tests, and both tool projects (Debug + Release), and ran the full
xUnit suite — **514 / 514 pass on Windows** (the extra 78 are the native-DLL
integration suite and the two inherently-Windows tests). The Windows pass
also smoke-tested the live `WhereIsIt.App.exe` and the `es.exe` CLI on a real
file system.

Building actually surfaced and fixed real defects the never-compiled branch hid:
a pre-existing CS0420 in `InProcEngineClient`, an `out _`/`using var _` collision
in `FtpServer`, an expression-tree `is`-pattern in a test, and — most
importantly — a **regression where `<`/`>` comparison operators (`size:>1mb`,
`len:>8`, `dm:<2020`) were mis-tokenised as grouping brackets**, now fixed in
`BooleanQuery.Lex` and covered by tests. The verified surface includes all query
parsing/filters, `< >` grouping + bracketless OR, the image/audio/document
property readers, and the FTP/ETP/named-pipe round-trips.

The **2026-06-08 Windows pass** caught more defects the Linux build couldn't
even attempt because they live in the WinUI-only / native-engine layer:

- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` was missing from the WinUI
  csproj — `GeneratedComInterface`/`GeneratedComClass` require unsafe blocks
  (SYSLIB1062).
- `IExplorerCommand` interface lacked `StringMarshalling = StringMarshalling.Utf16`
  on its `GeneratedComInterface` attribute (SYSLIB1051), so the source generator
  refused to marshal its out-string parameters.
- `NativeEngineClient.FromOptionalFileTime` substituted `DateTimeOffset.UtcNow`
  as the fallback for an unknown modified time — that mis-displayed every such
  row as "modified just now" and broke `Sort_ByModifiedAscending_OldestFirst`
  reproducibly. Switched to `DateTimeOffset.MinValue` (matches the engine's
  epoch-0 sort placement) and routed the column formatter through
  `FormatOptionalDate` so the UI renders `—`.
- `IndexingEngine::seedAncestors` left synthetic above-the-scope-root directory
  records with all-zero Modified/Created/Accessed. Now populated via
  `GetFileAttributesExW` per segment.
- `App.OnLaunched` parsed `-p <path>` into `cli.ScopeRoot` but never used it.
  The shell-context-menu verb relied on this flag. Now seeds an initial
  `child:"<path>"` clause (combined with `-s <query>` if both are supplied).

Also compile-verified on Linux: the **`es.exe` CLI** and the **engine service**
(`tools/WhereIsIt.Es`, `tools/WhereIsIt.EngineService` — net10.0-windows console
apps; building them caught and fixed two more real errors).

Still requiring a Windows build to compile/smoke-test: the **WinUI app**
(`WhereIsIt.App` — highlighting, tray, preview pane, custom columns, the
`WM_COPYDATA` IPC window, the `IExplorerCommand` COM handler) and the **native
C++ engine**. This is a hard boundary, not a soft one: the WinUI XAML compiler
(`XamlCompiler.exe`) is a Windows-only PE binary and cannot run on Linux
(verified: "Exec format error"). Also pending Windows: **wire verification** of
the two interop protocols against real Everything clients (`WM_COPYDATA` IPC,
ETP result framing).
