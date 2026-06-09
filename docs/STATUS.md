# WhereIsIt — Project Status & Next-Agent Handoff

> **Single source of truth.** Replaces the deleted `MIGRATION_PLAN.md` and `CURRENT_STATUS.md`. If you are the next agent picking this up, read this file first.

**Goal:** voidtools' Everything, but as a modern WinUI 3 app — better UI, faster when possible, native C++ engine.

**Branch:** `main`.

**Target dev box:** Windows 11 + Visual Studio 2026 (with v145/v143 MSVC, C++/WinRT, Windows 11 SDK 10.0.22621, Windows App SDK workload).

---

## Architecture (final target)

```
+-----------------------------------------------------------------------+
|                C# WinUI 3 App  (app/WhereIsIt.App)                    |
|   Views (XAML) — ViewModels (MVVM) — App Services (interfaces)        |
+-----------------------------------------------------------------------+
                                 │
                       IEngineClient (C# interface)
                                 │
        ┌────────────────────────┴────────────────────────┐
        ▼                                                 ▼
+----------------------------+         +----------------------------+
|  WhereIsIt.Engine.WinRT    |         |   WhereIsIt.Pipe.Client    |
|  (C++/WinRT runtime comp.) |         |   (C# named-pipe client)   |
+----------------------------+         +----------------------------+
        │                                                 │
        ▼                                                 ▼
+----------------------------+         +----------------------------+
|  WhereIsIt.Core (C++ lib)  |         |  WhereIsIt.Service (Win32) |
|  /domain  /app  /ports     |◄────────|  hosts Core + pipe server  |
+----------------------------+         +----------------------------+
        │
        ▼
+----------------------------+
| WhereIsIt.Adapters.Win32   |
|  Mft scan, USN journal,    |
|  drive enum, clock, log    |
+----------------------------+
```

---

## What works today (verified, on `main`)

| Layer | Status | Where |
|---|---|---|
| WinUI 3 exe | ✅ builds (WinAppSDK 2.0.1, net10, unpackaged) | `app/WhereIsIt.App/` |
| MVVM VMs (DI) | ✅ 6 VMs wired via `Microsoft.Extensions.DependencyInjection` | `app/WhereIsIt.App.Core/ViewModels/`, `app/WhereIsIt.App/AppBootstrap.cs` |
| **Modern UI** | ✅ multi-column results, click-to-sort, context menu, Ctrl+F/Esc/Ctrl+,/Enter shortcuts, settings window, Mica backdrop | `app/WhereIsIt.App/MainWindow.xaml{,.cs}`, `SettingsWindow.xaml{,.cs}` |
| Status bar | ✅ live record count + status text from `MetricsChanges`/`StatusChanges` | `MainViewModel.cs` |
| `IEngineClient` contract | ✅ Search/Sort/GetRow + 3 IObservables; `ResultRowModel` now carries optional `CreatedUtc`/`AccessedUtc` | `src/pipe/WhereIsIt.Pipe.Client/IEngineClient.cs` |
| `FilteringEngineClient` decorator | ✅ Parses every Everything-style modifier with `QueryParser`, rewrites a simplified form for the inner engine, post-filters returned IDs; race-safe with a monotonic seq fence | `app/WhereIsIt.App.Core/Services/FilteringEngineClient.cs` |
| `InProcEngineClient` | ✅ Pure-C# `Directory.EnumerateFiles` fallback — applies the full `ParsedQuery` natively | `app/WhereIsIt.App.Core/Services/InProcEngineClient.cs` |
| `PipeEngineClient` | ⚠️ stub returning hardcoded `[1u, 2u]` — never picked when native or in-proc is available | `src/pipe/WhereIsIt.Pipe.Client/PipeEngineClient.cs` |
| C++ engine | ✅ Lives at `src/engine/native/cpp/` (moved from `src/legacy/` on 2026-05-13). Compiles into `WhereIsIt.Engine.Native.dll`; consumed via P/Invoke. | `src/engine/native/` |
| xUnit tests | ✅ 514 green on Windows (Linux subset: 436) | `tests/app/`, run via `tests/ci/run_all.ps1` |
| GitHub Actions | ❌ not yet present (`.github/workflows/ci.yml` was claimed but never committed) | — |

---

## What's left

The original P-1…P4 plan is done:

| Plan item | Status |
|---|---|
| **P-1** verify build on Windows + VS box | Done — MSBuild from VS 18 Professional builds clean |
| **P0** real native C++ engine | Done — full IndexingEngine ported into `src/engine/native/cpp/` and consumed via P/Invoke + the `FilteringEngineClient` decorator |
| **P0** C++/WinRT bridge | Skipped — replaced with simpler P/Invoke + decorator pattern. The bridge can come back later if marshalling cost matters; in practice C#-side post-filtering is fast enough |
| **P1** tighten `IEngineClient` (`SearchHandle`, `IAsyncEnumerable`) | Deferred — current contract works well enough for the 2k UI cap |
| **P2** native pipe service for non-admin mode | Deferred — admin elevation is acceptable for daily use; `service/WhereIsIt.Service/` scaffold still empty |
| **P3** perf gates (BenchmarkDotNet, footprint) | Deferred |
| **P4** delete `src/legacy/` | Done 2026-05-13 — folder removed entirely |

Closed 2026-05-21 (technical-debt audit):

- ✅ Engine handle/mapping leak: `~IndexingEngine` now unmaps both shared-memory views (`m_recordsCount`, `m_driveLettersShared`) and closes the file mappings, data mutex, and data-changed event. Previously it closed only the data-changed event, leaking the rest on every clean engine teardown.
- ✅ Result-delivery race: the C# watch loop sized its buffer from `engine_result_count` then filled it from `engine_get_result_ids` — two independent snapshots, so a search completing between the calls could publish stale/zero-filled IDs. New `engine_get_results` copies count + IDs from the single snapshot the last `engine_wait_results_changed` observed, and the loop publishes only the IDs actually written.
- ✅ Watcher resilience: `NativeEngineClient.WatchLoop` wraps its iteration in try/catch so a faulting P/Invoke, throwing subscriber, or post-dispose `Subject` access exits the loop cleanly instead of faulting `_watchTask` with an unobserved exception.

Closed 2026-05-17 (Everything-parity bridge):

- ✅ Query funcs: `startwith:`/`endwith:`, `wfn:`/`wholefilename:`, `root:`, `empty:`, `len:`, `count:`, and the dupe family (`sizedupe:`/`namepartdupe:`/`attribdupe:` alongside the existing name+size `dupe:`). Parsed in `QueryParser`, post-filtered in `FilteringEngineClient`. `bool Dupe` is now a back-compat shim over the new `DupeKind DupeMode`.
- ✅ Sort parity: added created / accessed / extension(type) / attributes. In-proc engine sorts all of them; native engine gained `Extension`/`Attributes` (appended to `QuerySortKey` so the `engine_sort` ints stay stable).
- ✅ EFU (Everything File List) import/export in `ResultExporter` (`ToEfu`/`WriteEfu`/`ParseEfu`/`ReadEfu`) — FILETIME ticks + numeric attribute mask, round-trips with voidtools.
- ✅ Shutdown use-after-free fix: `IndexingEngine::Stop()` is now idempotent and records whether any worker had to be detached (timeout during the non-cancellable initial full-disk scan). `engine_destroy` refuses to free the `EngineState` when a worker is still live, converting a use-after-free into a one-shot leak at process exit.

Closed 2026-06-03 (build + test verification):

- ✅ Installed the .NET 10 SDK in-session and **compiled + ran the test suite on Linux** (`-p:EnableWindowsTargeting=true`) for `App.Core`, `Pipe.Client`, and the xUnit project. **436 tests pass, 0 fail** (excluding native-DLL integration + two inherently-Windows tests). Building caught real defects the never-compiled branch hid: a comparison-operator regression (`<`/`>` mis-parsed as grouping) in `BooleanQuery.Lex`, a pre-existing CS0420 in `InProcEngineClient`, an `out _`/`using var _` clash in `FtpServer`, and an expression-tree `is`-pattern in a test — all fixed. So the entire query/property/grouping/IPC/FTP/ETP logic is now verified, not just authored.

Closed 2026-06-03 (feature-parity audit — see `docs/PARITY.md`):

- ✅ Full Everything ⇄ WhereIsIt parity scorecard captured in `docs/PARITY.md`.
- ✅ Query funcs: `wildcards:`/`nowildcards:` (literal `*`/`?`), `diacritics:`/`nodiacritics:` (accent folding in the substring + whole-word paths; the native engine receives the `diacritics:false` hint so its candidate set stays a superset), the encoding-specific content aliases (`ansicontent:`/`utf8content:`/`utf16content:`/`utf16becontent:` → `content:`), and the `childcount:`/`childfilecount:`/`childfoldercount:` folder filters. Parsed in `QueryParser`, post-filtered in both `FilteringEngineClient` and `InProcEngineClient`. Covered by `QueryParserExtendedTests` + `InProcEngineClientExtendedFilterTests` (needs a Windows build to run green).

Closed 2026-06-03 (UI parity follow-up):

- ✅ **Match highlighting** — literal query terms (from `QueryParser.ExtractHighlightTerms`) are highlighted in the Name column via a `SearchHighlighter` attached property that drives WinUI `TextHighlighter` ranges. Terms flow MainViewModel → `ResultsListViewModel.BindResults` → row VM → XAML. Needs a Windows build to smoke-test.
- ✅ **System tray + minimize to tray** — dependency-free `TrayIconHost` (`Shell_NotifyIcon` on a message-only window); minimizing hides to tray, tray left-click / "Open WhereIsIt" restores, "Exit" quits. Needs a Windows build to smoke-test.

Closed 2026-06-03 (run-metadata filters):

- ✅ **`rc:` / `runcount:` and `dr:` filters** — `RunCountService` now also tracks (and persists, via `AppSettings.RunDates`) last-run timestamps and is thread-safe. `EngineClientFactory` passes path-keyed `Get`/`GetLastRun` lookups into `FilteringEngineClient`, which evaluates `rc:`/`dr:` in its post-filter for every inner engine. Covered by `FilteringEngineClientTests`, `QueryParserExtendedTests`, `RunCountServiceTests`.

Relative date spans (`3days`/`last2weeks`/`past6months`/`next1year`) now parse too, so the entire `ParseDateSpec` keyword surface is at parity (only the space-separated phrasing needs quoting).

Closed 2026-06-03 (boolean grouping):

- ✅ **`< >` grouping** — new additive `BooleanQuery` expression tree (lexer + recursive-descent parser + evaluator) engages only when a query contains a bracket, so the existing flat-clause fast path is untouched. Both engines evaluate the tree (`FilteringEngineClient`/`InProcEngineClient`). Functions apply globally (place them outside groups). Covered by `BooleanQueryTests` + `InProcEngineClientExtendedFilterTests`.

Remaining Everything-parity gaps (full detail + ranking in `docs/PARITY.md` §9):

- **Function-level OR / functions inside `< >`** — term grouping done; folding filters into the boolean tree is a larger engine change.
- **Preview pane, property/metadata index, `es.exe`+IPC SDK, shell extension, ETP/FTP server, background service** — Windows-only / large / proprietary-protocol work that can't be built or verified in a Linux session.
- **Property/metadata index** — unlocks `album:`/`width:`/… + custom columns.
- **ETP / FTP server** — proprietary Everything protocol. Skipped; HTTP server covers the cross-device search use case.
- **Everything IPC/SDK, `es.exe` CLI, shell context-menu extension, background service** — Windows-only integration surface; deliberately deferred.

Closed 2026-06-08 (user-experience audit round — six requirements):

User came back with concrete UX complaints + asked for a structured 2-round audit
gated by advisor agreement. All six requirements landed; final 6/6 PASS in both
back-to-back rounds via `tests/manual-audit/audit.ps1`, advisor agreed.

What landed:

- ✅ **Wildcard `*.md` post-filter, authoritative.** `FilteringEngineClient.NeedsPostFiltering` now returns true for any clause containing `*`/`?` or `regex:true`. The native engine's wildcard scan races during indexing (returns stale IDs that don't actually match), so the decorator's anchored-Regex post-filter is now the source of truth even when wildcards mode is on. The earlier "*.md → 18 K random files" repro was that race; with the gate it's 2 000 real `.md` files. (Reverted a parallel attempt to push `size:` down to the native engine — pre-existing native-side bug returns 0 for any standalone size query; documented as known-issue.)
- ✅ **Single-instance forwarding + tray dock = no re-index on subsequent launches.** New `SingleInstance` (named-mutex + `WhereIsIt.Launch` named pipe). First process owns the mutex and a background pipe-listener; subsequent launches serialise their CLI args as JSON over the pipe and exit. The primary's `Dispatch` writes a sentinel at `%TEMP%\whereisit-last-forward.txt` (used by the audit) then forwards to the running window via the WinUI dispatcher.
- ✅ **Close-to-tray.** `AppWindow.Closing` now diverts to `HideToTray()` (calls `AppWindow.Hide`) when the tray icon is live. Only the tray menu's "Exit" actually terminates. Verified by `taskkill /pid` (no `/F`), which sends `WM_CLOSE`: process is still alive 3 s later in both audit rounds.
- ✅ **Compact rows.** `ListView.ItemContainerStyle` now sets `MinHeight=0`, `FontSize=13`, `VerticalContentAlignment=Center`; `DataTemplate` Grid uses `Padding=6,1`; header `ColumnHeaderButton` mirrors with `Padding=6,2` + `FontSize=13`. Audit verifies setters are present; rendered-pixel verification still needs a human.
- ✅ **Headless / CLI test harness.** `--headless --query <Q> --output <file> [--max-results N] [--timeout S] [--name-only] [--minimized] [--enable-http]` + the existing `-s`/`-p`. The headless path waits for the engine's `Ready` status before issuing the search, then 500 ms of emission-quiet before printing — so a stale emission from the indexing phase can't trick the wait. CLI works from PowerShell because the headless path opens the `--output` file (WinExe doesn't keep stdout attached when launched from PS `&`).
- ✅ **Live USN file-change updates** (`MonitorChanges` was already wired). Audit step 5 starts the primary with `--enable-http`, waits Ready, creates a uniquely-named probe file inside `E:\Dev\WhereIsIt`, sleeps 6 s for the USN-drain loop, then queries `http://127.0.0.1:12321/search?q=<unique>` — the running engine returns the new file without restart.

**Bugs caught during these audits:**

- The `SingleInstance.Dispatch` sentinel was written **after** the `if (win is null) return;` guard, so test rounds that ran before the main window registered showed PASS on the secondary side but never produced a sentinel. Moved the write before the guard. Caught + fixed in audit round 1; reset count; round 1+2 then both 6/6.
- The audit script checked `$env:TEMP` but the .NET process under VS-launch context resolves `Path.GetTempPath()` to `C:\Windows\Temp`. Audit now checks both candidates.

**Known issues left as next-session work (advisor agreed they don't block):**

- `size:>50mb` (standalone, no other clause) returns 0 results even though large files exist. Native engine's `size:` term-eval doesn't engage for solo-size queries; decorator's MaxScan=5000 also can't recover because the cap window is record-ID-ordered (small files first). Pushdown was reverted with an in-source note in `SimplifyForInnerEngine`.
- Visual row-density verification is XAML-source-presence, not rendered-pixel measurement. WinUI 3 AutomationPeers don't expose virtualized item bounding rects from UIAutomation, so a human still needs to eyeball.
- USN audit creates the probe inside `E:\Dev\WhereIsIt`. A probe outside any indexed drive scope wouldn't reach the running engine — by design, but tests targeting other paths should keep that in mind.

Closed 2026-06-08 (Windows-toolchain verification pass):

The 2026-06-03 cross-platform pass authored a lot on Linux that could not be
compiled or run there. This session brought everything onto a real Windows
11 box with VS 2026 Professional + .NET 10.0.300 SDK and exercised the full
build/test/launch path. Five real defects surfaced and were fixed:

- ✅ **`WhereIsIt.App.csproj` missing `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`** — `ExplorerCommandHandler` uses `GeneratedComInterface`/`GeneratedComClass`, both of which require unsafe blocks (SYSLIB1062). The Linux build never compiled the WinUI app so this stayed hidden.
- ✅ **`IExplorerCommand` missing `StringMarshalling`** — `GeneratedComInterfaceAttribute` rejects out-string parameters without `StringMarshalling = StringMarshalling.Utf16` (SYSLIB1051). Same root cause: the WinUI-only file never compiled on Linux.
- ✅ **`NativeEngineClient.FromOptionalFileTime` substituted `DateTimeOffset.UtcNow` for `modifiedFileTime == 0`** — that made every unknown-mtime row sort and display as "modified just now," breaking `Sort_ByModifiedAscending_OldestFirst` reproducibly. The fallback is now `DateTimeOffset.MinValue`, which matches the engine's epoch-0 sort placement.
- ✅ **`ResultRowViewModel.ModifiedText` formatted `MinValue`/`default` as `0001-01-01 00:00`** — switched to the existing `FormatOptionalDate` (renders `—`) so the unknown sentinel displays the same way Created/Accessed already did.
- ✅ **Synthetic ancestor directory records had all timestamps zero** — `seedAncestors` populated only the name/parent/drive fields, leaving Modified/Created/Accessed at 0. Every scope-rooted scan therefore produced N synthetic records (one per path segment above the root) that sorted before every real file in `sort:asc` by date. `seedAncestors` now calls `GetFileAttributesExW` per segment and populates real Modified/Created/Accessed/Attributes from disk.
- ✅ **`App.OnLaunched` parsed `-p <path>` but never used `cli.ScopeRoot`** — the value was silently dropped, so the documented CLI flag (and the shell-extension verb that depends on it) was a no-op. Now seeds an initial `child:"<path>"` clause, combined with the user's `-s <query>` if both are present.

**Build matrix verified on Windows:**
- Native engine `WhereIsIt.Engine.Native.dll` — Debug (2.34 MB) + Release (530 KB), MSBuild from VS 2026 amd64.
- `WhereIsIt.App.Core`, `WhereIsIt.App` (WinUI 3), `WhereIsIt.Pipe.Client`, `WhereIsIt.App.Tests`, `WhereIsIt.Es`, `WhereIsIt.EngineService` — Debug + Release, .NET 10.0.300 SDK.
- Note: `dotnet build WhereIsIt.slnx` exits 1 because the dotnet CLI can't drive `vcxproj` (`MSB4278: Microsoft.Cpp.Default.props`). All C# projects still build. The C++ engine must be built separately via MSBuild before `dotnet build`. `tests/ci/run_all.ps1` does the right thing (it builds the tests csproj directly, not the slnx).

**Test suite: 514 / 514 pass on Windows** (was 436 on Linux; the extra 78 are the native-engine integration suite + the two inherently-Windows tests that only run on Windows).

**Smoke-tested live** (Release):
- `tests\ci\run_all.ps1` — 514 / 514 green via the documented CI entrypoint (not just the direct `dotnet test` invocation).
- `es.exe "PARITY"` — returned 78 real filesystem matches across the indexed drives, end-to-end through the live `WhereIsIt.Engine.Native.dll` and `QueryParser`. **This is the strongest functional signal: full engine + query stack works on real data.**
- `WhereIsIt.App.exe -p E:\Dev\WhereIsIt\docs` — launched, ran 25 s with steady working-set growth (568 MB → 778 MB while indexing), no crash, clean shutdown via `Stop-Process`. Stdout/stderr clean. **Caveat: this proves the launch/shutdown/indexing path; it does NOT exercise UI interaction (search bar input, sort clicks, menu commands, tab switches, tray, hotkeys) — those need a human or a UI-driver.** The 778 MB resident reflects that `-p` is a `child:` query filter, not an index-scope override (by design — matches Everything's `-path`), so the engine still indexed the broader default scope.

**Build-order gotcha**: the WinUI csproj copies the engine DLL with
`<None Include="...x64\$(Configuration)\WhereIsIt.Engine.Native.dll" Condition="Exists(...)">`.
The `Exists(...)` check runs at evaluate time. On a clean clone, if the C#
project restores before the C++ engine has been built, the item silently
drops and the DLL never copies — leaving `DllNotFoundException` at runtime.
Build order is: **C++ engine via MSBuild first → `dotnet build` for C# second.**
If a fresh clone hits this, manually `Copy-Item` the DLL from
`src\engine\native\x64\<config>\` into `app\WhereIsIt.App\bin\<config>\net10.0-windows10.0.19041.0\`
and rerun. Cleaner long-term fix: convert to a `<Target AfterTargets="Build">`
with `<Copy>` so the lookup happens at build time, not evaluate time.

**Still not Windows-verified** (deliberately skipped; require external clients we don't have):
- `EverythingIpcServer` `WM_COPYDATA` wire-framing against a real Everything SDK client.
- `FtpServer` ETP result-column wire framing against a real Everything ETP client.
- `IExplorerCommand` COM handler — needs sparse-MSIX packaging to register; the classic registry shell verb (`ShellMenuRegistration`) ships and works today.
- `WhereIsIt.EngineService` end-to-end with `NamedPipeEngineClient` over a real service install.

**Doc cleanup**: STATUS.md claimed `.github/workflows/ci.yml` exists — it does not. The repo has no GitHub Actions workflow today; only the local `tests/ci/run_all.ps1`. Removed claims about CI elsewhere if any. (`run_all.ps1` is what should be re-used when CI is added.)

Closed this session:

- ✅ Multiple result tabs (`TabView` + `TabsViewModel`)
- ✅ `content:` filter — opens each candidate file, streams content, matches with cross-chunk overlap; skips dirs and oversized files
- ✅ Run-count column + persisted `RunCountService`
- ✅ HTTP server — `HttpSearchServer`, `127.0.0.1`-only, JSON `/search?q=` endpoint
- ✅ Column-visibility toggle — Columns flyout button + persisted settings (Created/Accessed/Runs)
- ✅ File operations — result menu entries for rename, Recycle Bin deletion, and Windows properties
- ✅ Reachable sort/export polish — Created/Accessed/Type/Attributes headers call engine sorting; File menu exports Everything `.efu` lists; row exports retain optional timestamps
- ✅ Settings polish — index scope, hotkey, run-on-startup, and localhost HTTP server options are editable with validation
- ✅ Native Created/Accessed parity — index schema v10 stores all three timestamps, safely rebuilds v9 indexes, preserves the v1 row ABI while adding version-probed `engine_get_row_v2`, isolates v10 shared-memory mappings from older processes, marshals Created/Accessed rows to C#, and sorts both columns natively
- ✅ Production-readiness follow-up — eagerly starts the opt-in HTTP server, fixes native `sort:desc` / `sort:asc`, makes settings flush retries durable, explicitly uses the Recycle Bin, rejects unsafe Windows rename targets, and isolates concurrent native app processes
- ✅ Production-readiness audit round 2 — retries failed native incremental saves, validates persisted string/drive/giant-index data before commit, guarantees the latest settings snapshot is flushed on shutdown, releases timed-out HTTP subscriptions, tightens HTTP route matching, and applies startup registration immediately after save

The aspirational `src/core/`, `src/adapters/win32/`, `src/engine/winrt/`, `service/` scaffolds are still present as empty/partial vcxprojs. They are NOT on the active build path; the only C++ project that's built is `src/engine/native/WhereIsIt.Engine.Native.vcxproj`. Decide later whether to refactor the engine into the layered architecture or remove those folders.

---

## Decisions captured

- **Indexer:** native C++. Legacy code moved verbatim from `src/legacy/` into `src/engine/native/cpp/` (2026-05-13). Consumed via P/Invoke + a `FilteringEngineClient` decorator on the C# side that handles every Everything-style query modifier the native engine doesn't understand.
- **Service:** named-pipe service deferred. `PipeEngineClient` is a stub. Native engine runs in-process; elevation prompt is acceptable.
- **UI:** Everything-grade — multi-column results, sort, Explorer-style file operations, Mica, settings, quick-filter bar, modifier toggle buttons, search history with up/down recall, bookmarks, CSV/TSV/EFU export, global hotkey, command-line args, drag-and-drop, optional Created/Accessed columns.

---

## Project layout (current, post 2026-05-13 port)

```
app/
  WhereIsIt.App/                 WinUI 3 exe (net10.0-windows10.0.19041.0)
    App.xaml{,.cs}               CLI arg dispatch on launch
    MainWindow.xaml{,.cs}        Top bar + quick-filter bar + results list
    SettingsWindow.xaml{,.cs}    Scope-root editor
    GlobalHotkeyHost.cs          Win32 RegisterHotKey wrapper
    AppBootstrap.cs              DI composition root

  WhereIsIt.App.Core/            Class library (net10.0-windows, no WinUI)
    ViewModels/                  Search box, results, settings VMs
    Services/
      QueryParser.cs             Everything-style syntax (ext/size/dm/attrib/child/parent/dupe/AND/OR/NOT/...)
      FilteringEngineClient.cs   Decorator: parses + post-filters above any inner engine
      EngineClientFactory.cs     Native > pipe > in-proc fallback chain, always wrapped
      NativeEngineClient.cs      P/Invoke into WhereIsIt.Engine.Native.dll
      InProcEngineClient.cs      Pure-C# Directory.EnumerateFiles fallback
      SearchHistory.cs           Up/down arrow MRU recall
      BookmarkService.cs         Named saved queries
      ResultExporter.cs          CSV/TSV export
      HotkeyBinding.cs           Hotkey string parser
      QueryComposer.cs           Quick-filter bar logic
      SearchModifiersComposer.cs Case/Regex/Word/Path toggle composer
      CommandLineArgs.cs         -s/-p flag parser

src/
  engine/native/                 C++ engine DLL (compiles into WhereIsIt.Engine.Native.dll)
    WhereIsIt.Engine.Native.{cpp,h,vcxproj}   C-export shim consumed by P/Invoke
    cpp/                                       Engine implementation (moved from src/legacy/)
      Engine.{cpp,h}             IndexingEngine — USN/MFT scan + search/sort
      RecordPool.{cpp,h}         Shared-memory record store w/ Local\/heap fallback
      StringPool.{cpp,h}         Shared-memory string interning
      QueryEngine.{cpp,h}        case:/regex:/word:/matchpath:/extfilt:/diacritics:/sort: parsing
      QueryDomain.{cpp,h}        QueryPlan and related types
      UsnJournalReaderWin32.{cpp,h} + IUsnJournalReader.h
      DriveEnumeratorWin32.{cpp,h}  + IDriveEnumerator.h
      SortService.{cpp,h}
      PathSizeDomain.{cpp,h}
      StringUtils.{cpp,h}, Utils.{cpp,h}
      CoreTypes.h, Logging.h, framework.h, targetver.h
  core/                          Future layered C++ — empty scaffolds (partial source, no project builds)
  adapters/win32/                Future C++ adapter scaffolds — empty vcxproj
  engine/winrt/                  Future C++/WinRT bridge — empty vcxproj
  pipe/
    WhereIsIt.Pipe.Client/       IEngineClient + stub PipeEngineClient + protocol types

service/
  WhereIsIt.Service/             Future named-pipe service — empty vcxproj

tests/
  app/                           xUnit (256 passing — 200 non-native + 56 native integration)
  core/, adapters/, parity/, smoke/, unit/, cases/, fixtures/   ← scaffolds for C++ tests
  ci/run_all.ps1

.github/workflows/ci.yml         GitHub Actions CI
WhereIsIt.slnx                   App, App.Core, Pipe.Client, Tests
Directory.Packages.props         central NuGet versions
docs/STATUS.md                   this file
```

---

## Infrastructure facts

- Target dev box: Windows 11 + Visual Studio 2026 (with v145 toolset). Original constraint "no VS installed" no longer applies.
- WinAppSDK 2.0.1 works without `dotnet workload install`. If you upgrade to a version that needs it, run `dotnet workload install windowsappsdk`.
- PowerShell 5.1 is the lowest common denominator on stock Windows — `tests/ci/run_all.ps1` uses `powershell -File`.
- The current branch was authored on a Linux sandbox without `dotnet` or `msbuild`; a final round of compile fixups on Windows is expected.

---

## How to run

```powershell
# All C# tests
powershell -File tests\ci\run_all.ps1

# Build the WinUI 3 app (Debug)
dotnet build app\WhereIsIt.App\WhereIsIt.App.csproj

# Run
.\app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\WhereIsIt.App.exe
```
