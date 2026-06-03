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
| xUnit tests | ✅ 256 green (200 non-native + 56 native integration) | `tests/app/`, run via `tests/ci/run_all.ps1` |
| GitHub Actions | ✅ CI workflow (`windows-2022`, dotnet 10, runs tests + builds exe) | `.github/workflows/ci.yml` |

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

Closed 2026-06-03 (feature-parity audit — see `docs/PARITY.md`):

- ✅ Full Everything ⇄ WhereIsIt parity scorecard captured in `docs/PARITY.md`.
- ✅ Query funcs: `wildcards:`/`nowildcards:` (literal `*`/`?`), `diacritics:`/`nodiacritics:` (accent folding in the substring + whole-word paths; the native engine receives the `diacritics:false` hint so its candidate set stays a superset), the encoding-specific content aliases (`ansicontent:`/`utf8content:`/`utf16content:`/`utf16becontent:` → `content:`), and the `childcount:`/`childfilecount:`/`childfoldercount:` folder filters. Parsed in `QueryParser`, post-filtered in both `FilteringEngineClient` and `InProcEngineClient`. Covered by `QueryParserExtendedTests` + `InProcEngineClientExtendedFilterTests` (needs a Windows build to run green).

Closed 2026-06-03 (UI parity follow-up):

- ✅ **Match highlighting** — literal query terms (from `QueryParser.ExtractHighlightTerms`) are highlighted in the Name column via a `SearchHighlighter` attached property that drives WinUI `TextHighlighter` ranges. Terms flow MainViewModel → `ResultsListViewModel.BindResults` → row VM → XAML. Needs a Windows build to smoke-test.
- ✅ **System tray + minimize to tray** — dependency-free `TrayIconHost` (`Shell_NotifyIcon` on a message-only window); minimizing hides to tray, tray left-click / "Open WhereIsIt" restores, "Exit" quits. Needs a Windows build to smoke-test.

Closed 2026-06-03 (run-metadata filters):

- ✅ **`rc:` / `runcount:` and `dr:` filters** — `RunCountService` now also tracks (and persists, via `AppSettings.RunDates`) last-run timestamps and is thread-safe. `EngineClientFactory` passes path-keyed `Get`/`GetLastRun` lookups into `FilteringEngineClient`, which evaluates `rc:`/`dr:` in its post-filter for every inner engine. Covered by `FilteringEngineClientTests`, `QueryParserExtendedTests`, `RunCountServiceTests`.

Remaining Everything-parity gaps (full detail + ranking in `docs/PARITY.md` §9):

- **Richer date keywords**, **explicit `< >` grouping** — `QueryParser` work.
- **Property/metadata index** — unlocks `album:`/`width:`/… + custom columns.
- **ETP / FTP server** — proprietary Everything protocol. Skipped; HTTP server covers the cross-device search use case.
- **Everything IPC/SDK, `es.exe` CLI, shell context-menu extension, background service** — Windows-only integration surface; deliberately deferred.

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
