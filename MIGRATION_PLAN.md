# WhereIsIt → WinUI 3 (C# / MVVM) Migration & Testability Plan

> **Single source of truth.** This replaces all prior plan/status docs. Execute phases in order. Each phase is a self-contained PR.

## 0. Goals & Non-Goals

### Goals
1. Replace the Win32/`WhereIsIt.cpp` UI with a **C# WinUI 3** packaged app using **MVVM** (CommunityToolkit.Mvvm).
2. Refactor the C++ engine so every layer (domain, services, adapters, ViewModels) is **independently unit-testable**.
3. Preserve admin-mode (in-proc engine) and non-admin-mode (named-pipe service) parity.
4. Reach a state where an autonomous coding agent can: build, run unit tests, run parity tests, and ship a feature without manual GUI clicking.

### Non-Goals
- No new product features during the migration. Behavior parity only.
- Do **not** rewrite the C++ engine's core algorithms (MFT scan, USN journal, RecordPool, StringPool). Wrap and seam them — don't replace them.
- Do **not** target unpackaged WinUI 3 (we use Windows App SDK packaged with sparse signing).

---

## 1. Final Target Architecture

```
+-----------------------------------------------------------------------+
|                C# WinUI 3 App  (WhereIsIt.App, packaged)              |
|   Views (XAML) ── ViewModels (MVVM) ── App Services (interfaces)      |
|                                  │                                    |
|                                  ▼                                    |
|        IEngineClient  (C# interface, mockable in unit tests)          |
+-----------------------------------------------------------------------+
                                   │
        ┌──────────────────────────┴──────────────────────────┐
        ▼                                                     ▼
+----------------------------+             +----------------------------+
|  WhereIsIt.Engine.WinRT    |             |   WhereIsIt.Pipe.Client    |
|  (C++/WinRT runtime comp.) |             |   (C# named-pipe client)   |
|  Wraps in-proc engine      |             |   Talks to admin service   |
+----------------------------+             +----------------------------+
        │                                                     │
        ▼                                                     ▼
+----------------------------+             +----------------------------+
|  WhereIsIt.Core (C++ lib)  |             |  WhereIsIt.Service (Win32) |
|   /domain  /app  /ports    |◄────────────│   hosts WhereIsIt.Core +   |
|   pure, no Win32           |             |   pipe server              |
+----------------------------+             +----------------------------+
        │
        ▼
+----------------------------+
| WhereIsIt.Adapters.Win32   |
|  IDriveEnumerator,         |
|  IUsnJournalReader,        |
|  IFileSystemScanner,       |
|  IIndexStorage, IClock,    |
|  IEventSignal              |
+----------------------------+
```

### Test projects (all run via `dotnet test` / `ctest`)
- `WhereIsIt.Core.Tests` (C++, GoogleTest) — domain + services with fake adapters.
- `WhereIsIt.Adapters.Tests` (C++, GoogleTest, Windows-only) — Win32 adapter smoke.
- `WhereIsIt.App.Tests` (C# xUnit + NSubstitute + FluentAssertions) — ViewModels + app services with mocked `IEngineClient`.
- `WhereIsIt.Parity.Tests` (PowerShell + C# host) — admin vs non-admin output diffing.

---

## 2. Repository Layout (target)

```
/src
  /core                          # C++ static lib, no Win32
    /domain
      Query/QueryParser.{h,cpp}
      Query/QueryMatcher.{h,cpp}
      Query/QueryPlan.{h,cpp}
      Path/PathBuilder.{h,cpp}
      Path/PathSizeResolver.{h,cpp}
      Records/FileRecordView.{h,cpp}
      Sort/SortPolicy.{h,cpp}
    /app
      IndexBuildService.{h,cpp}
      IndexUpdateService.{h,cpp}
      SearchService.{h,cpp}
      SortService.{h,cpp}
      Logger/StructuredLogger.{h,cpp}
    /ports
      IDriveEnumerator.h
      IFileSystemScanner.h
      IUsnJournalReader.h
      IIndexStorage.h
      IClock.h
      IEventSignal.h
      IStatusNotifier.h
      ILogger.h
    Core.vcxproj                 # static lib
  /adapters/win32
    DriveEnumeratorWin32.{h,cpp}
    UsnJournalReaderWin32.{h,cpp}
    FileSystemScannerWin32.{h,cpp}
    IndexStorageBinary.{h,cpp}
    EventSignalWin32.{h,cpp}
    ClockWin32.{h,cpp}
    JsonlFileLogger.{h,cpp}
    AdaptersWin32.vcxproj        # static lib
  /engine-winrt                  # C++/WinRT runtime component (.winmd)
    WhereIsIt.Engine.idl
    EngineClient.{h,cpp}         # implements IEngineClient
    RecordView.{h,cpp}
    EngineWinRT.vcxproj
  /pipe-client                   # C# pipe client (alternative to in-proc)
    WhereIsIt.Pipe.Client.csproj
    PipeEngineClient.cs          # implements IEngineClient
    PipeProtocol.cs              # versioned schema
  /service                       # Win32 host of Core (existing, refactored)
    Service.cpp
    PipeServer.{h,cpp}
    WhereIsIt.Service.vcxproj
  /app                           # WinUI 3 app
    WhereIsIt.App.csproj         # net8.0-windows10.0.19041.0, WinAppSDK
    Package.appxmanifest
    App.xaml{,.cs}
    /Abstractions
      IEngineClient.cs
      IDialogService.cs
      IClipboardService.cs
      IShellService.cs
      ISettingsService.cs
    /Services
      EngineClientFactory.cs     # picks in-proc WinRT vs pipe client
      DialogService.cs
      ClipboardService.cs
      ShellService.cs
      SettingsService.cs
    /ViewModels
      MainViewModel.cs
      SearchBoxViewModel.cs
      ResultsListViewModel.cs
      ResultRowViewModel.cs
      StatusBarViewModel.cs
      SettingsViewModel.cs
    /Views
      MainWindow.xaml{,.cs}
      ResultsListView.xaml{,.cs}
      SettingsPage.xaml{,.cs}
    /Converters
/tests
  /core         (GoogleTest; CMake or vcxproj)
  /adapters     (GoogleTest, Windows-only)
  /app          (xUnit C#)
  /parity       (run_parity.ps1, parity_comparator.cpp)
  /fixtures     (deterministic disk dataset generator)
  /cases        (query_matrix.json shared between admin/non-admin runs)
WhereIsIt.sln                    # contains every project above
```

The existing top-level loose `.cpp/.h` files are physically moved into `/src/...` during Phase 1.

---

## 3. Public Contracts (write these first; they unblock parallel work)

### 3.1 C++ ports (`/src/core/ports/*.h`)

```cpp
// IDriveEnumerator.h
struct DriveInfo { std::wstring Letter; uint32_t Serial; DriveFileSystem FsType; bool IsNetwork; };
struct IDriveEnumerator { virtual ~IDriveEnumerator() = default;
  virtual std::vector<DriveInfo> Enumerate() = 0; };

// IFileSystemScanner.h
struct ScannedRecord { uint8_t DriveIndex; uint32_t Mft; uint16_t Seq; uint32_t ParentMft;
  uint16_t ParentSeq; std::wstring Name; uint64_t Size; uint64_t Mtime; uint16_t Attrs; };
struct IFileSystemScanner { virtual ~IFileSystemScanner() = default;
  virtual void Scan(const DriveInfo&, std::function<void(const ScannedRecord&)> sink) = 0; };

// IUsnJournalReader.h  (already exists; keep)
// IIndexStorage.h
struct IIndexStorage { virtual ~IIndexStorage() = default;
  virtual bool Save(const std::wstring& path, const IndexSnapshot&) = 0;
  virtual bool Load(const std::wstring& path, IndexSnapshot&) = 0; };

// IClock.h, IEventSignal.h, IStatusNotifier.h, ILogger.h — small, obvious shapes.
```

### 3.2 C# `IEngineClient` (`/src/app/Abstractions/IEngineClient.cs`)

```csharp
public interface IEngineClient : IAsyncDisposable {
  Task StartAsync(CancellationToken ct);
  Task StopAsync(CancellationToken ct);

  Task<SearchHandle> SearchAsync(string query, CancellationToken ct);
  Task SortAsync(SearchHandle h, SortKey key, bool descending, CancellationToken ct);

  // Streams the row id list as it grows; emits one final list on completion.
  IAsyncEnumerable<IReadOnlyList<uint>> ObserveResults(SearchHandle h, CancellationToken ct);

  Task<RowDisplayData> GetRowAsync(uint recordIndex, CancellationToken ct);
  Task<string> GetFullPathAsync(uint recordIndex, CancellationToken ct);
  Task<string> GetParentPathAsync(uint recordIndex, CancellationToken ct);

  IObservable<string> StatusChanges { get; }     // text status (e.g. "Scanning C:")
  IObservable<EngineMetrics> MetricsChanges { get; }

  Task SetScopeAsync(IndexScopeConfig cfg, CancellationToken ct);
  Task<IndexScopeConfig> GetScopeAsync(CancellationToken ct);
}

public sealed record SearchHandle(Guid Id);
public sealed record RowDisplayData(string Name, string ParentPath, ulong Size,
                                    DateTimeOffset Modified, ushort Attributes);
public sealed record IndexScopeConfig(bool IndexNetworkDrives, bool FollowReparsePoints,
                                      ImmutableArray<string> IncludeRoots,
                                      ImmutableArray<string> ExcludePathPatterns);
public sealed record EngineMetrics(ulong RecordCount, ImmutableArray<string> Drives);
```

Two concrete `IEngineClient` implementations:
- `InProcEngineClient` (wraps `WhereIsIt.Engine.WinRT`).
- `PipeEngineClient` (in `WhereIsIt.Pipe.Client`).
`EngineClientFactory` chooses based on `IsRunningElevated()` and pipe reachability — same logic as today's `WhereIsIt.cpp:WinMain`.

### 3.3 IPC schema (versioned, JSON-tagged binary frames)

Define in `/src/service/PipeProtocol.h` and mirror in C# `PipeProtocol.cs`. Every request/response carries `{ "v": 2, "op": "...", "id": "...", "payload": {...} }`. Version 1 = legacy, version 2 = post-migration. Server supports both during burn-in.

---

## 4. Phased Execution

Each phase: **scope · files · tests · acceptance · commit message**. Every phase ends with a green build and green tests, pushed to `claude/winui3-testable-refactor-lmHdh`.

---

### Phase 1 — Solution skeleton & relocation (no behavior change)

**Scope**
- Create `WhereIsIt.sln` with empty/skeleton projects: `Core` (static lib), `AdaptersWin32` (static lib), `EngineWinRT` (WinRT comp), `WhereIsIt.App` (WinUI 3 packaged), `WhereIsIt.Service` (Win32 exe — wraps existing service code), `WhereIsIt.Pipe.Client` (C# class lib), and the four test projects.
- Physically move existing `.h/.cpp` into `/src/legacy/` (a temporary holding dir referenced by `Core` + `AdaptersWin32` + `EngineWinRT` until extracted). Update `WhereIsIt.vcxproj` to remain buildable as the legacy Win32 exe — keep it in the solution as `WhereIsIt.Legacy` until Phase 9 deletes it.
- Add `Directory.Build.props` setting C++17, `/W4 /WX`, `<Nullable>enable</Nullable>` for C#, `LangVersion=latest`, `TreatWarningsAsErrors=true`.
- Add `.editorconfig`, `Directory.Packages.props` for central NuGet versions:
  - `Microsoft.WindowsAppSDK 1.6.*`
  - `CommunityToolkit.Mvvm 8.*`
  - `Microsoft.Extensions.DependencyInjection 8.*`
  - `xunit`, `xunit.runner.visualstudio`, `NSubstitute`, `FluentAssertions`
  - `System.Reactive` (for `IObservable<T>`)
- Add `build.ps1` and `test.ps1` at repo root. CI script: `tests/ci/run_all.ps1` runs `dotnet build`, `ctest`, `dotnet test`.

**Tests**
- `tests/smoke/SolutionBuilds.ps1`: `msbuild /m /p:Configuration=Debug` succeeds for every project.

**Acceptance**
- Legacy app still builds and runs identically.
- New empty projects build.
- `pwsh ./build.ps1` returns 0.

**Commit**: `phase1: solution skeleton, project scaffolding, central package versions`

---

### Phase 2 — Extract C++ Core (domain, no Win32)

**Scope**
Extract pure modules out of `Engine.cpp` and `QueryEngine.cpp` into `/src/core/domain`:

| New file | Source of code | Public API |
|---|---|---|
| `domain/Query/QueryParser` | `QueryEngine.cpp` parser path | `QueryAst Parse(std::string_view)` |
| `domain/Query/QueryMatcher` | `QueryEngine.cpp` matcher | `bool Match(const QueryAst&, const FileRecordView&)` |
| `domain/Query/QueryPlan` | parts of `QueryDomain.cpp` | `QueryPlan Compile(const QueryAst&); bool HasInlineSortDirective(const QueryAst&)` |
| `domain/Path/PathBuilder` | `Engine.cpp::GetFullPath*` | pure recursive builder over a `RecordLookup` callback |
| `domain/Path/PathSizeResolver` | `PathSizeDomain.{h,cpp}` | move file unchanged |
| `domain/Sort/SortPolicy` | `SortService.{h,cpp}` | move file unchanged, drop Win32 includes |
| `domain/Records/FileRecordView` | new | normalized DTO + adapter from `FileRecord` |

Rule: zero `#include <windows.h>` under `/src/core`. Use `uint64_t` filetimes, `std::wstring` (UTF-16) only at API boundary, `std::string` (UTF-8) internally.

**Tests** (`tests/core/`)
- Query parser:
  - empty / whitespace-only
  - single token
  - quoted phrase with escaped quote
  - boolean operators, precedence: `a AND (b OR c) NOT d`
  - regex flag, case-insensitive flag
  - inline sort directive: `sort:size desc`
  - invalid input returns error AST, never throws
- Matcher: case sensitivity, whole-word, regex, accent folding (if any), Unicode surrogate pairs.
- PathBuilder: deep nesting (100 levels), root edge cases, cycle defense.
- SortPolicy: stable on ties, all 8 sort key/direction combos, NaN/zero values.
- PathSizeResolver: regular size vs giant-map fallback.
- Coverage gate: ≥90% line coverage on `/src/core/domain/**` (enforced via OpenCppCoverage report).

**Acceptance**
- `ctest -R core` green; coverage report meets gate.
- Legacy engine compiles by linking `Core.lib` and replacing inlined logic with calls.
- Parity baseline (still legacy Win32 app) unchanged.

**Commit**: `phase2: extract pure C++ core domain; 90% covered`

---

### Phase 3 — Extract ports & adapters

**Scope**
- Define all ports under `/src/core/ports/`.
- Move existing Win32 implementations into `/src/adapters/win32/`:
  - `DriveEnumeratorWin32.{h,cpp}` (already exists)
  - `UsnJournalReaderWin32.{h,cpp}` (already exists)
  - **NEW** `FileSystemScannerWin32` extracted from `Engine.cpp::ScanMftForDrive` and `ScanGenericDrive`.
  - **NEW** `IndexStorageBinary` extracted from `Engine.cpp::SaveIndex/LoadIndex`.
  - **NEW** `EventSignalWin32`, `ClockWin32`.
  - **NEW** `JsonlFileLogger` implementing `ILogger` (one JSON object per line; required keys `ts, lvl, event, module, corr`).
- `IndexingEngine` constructor now takes `EnginePorts { IDriveEnumerator*, IFileSystemScanner*, IUsnJournalReader*, IIndexStorage*, IClock*, IEventSignal*, ILogger* }`. Composition root in `WhereIsIt.Service` and `EngineWinRT` provides Win32 adapters.

**Tests**
- `tests/core/app/IndexBuildServiceTests.cpp` — uses fake scanner emitting deterministic records; asserts:
  - records ingested in stable order
  - storage `Save` called once with expected snapshot
  - `engine.start`, `scan.start`, `scan.end`, `index.save.start`, `index.save.end` log events emitted in order with matching `corr`.
- `tests/core/app/IndexUpdateServiceTests.cpp` — fake USN reader emits Upsert + Delete + dup; asserts idempotency, ordering, and that `usn.record.upsert/delete` events land.
- `tests/core/app/SearchServiceTests.cpp` — cancellation mid-query, re-entrant query supersedes prior, no lost final notification (deterministic event-signal fake).
- `tests/adapters/IndexStorageBinaryRoundtripTests.cpp` (Windows-only) — Save then Load returns equal `IndexSnapshot`.

**Acceptance**
- Engine builds with all adapters injected; legacy app still works (composition wires Win32 adapters).
- Service-level tests run on every push.

**Commit**: `phase3: ports + win32 adapters; service-level unit tests`

---

### Phase 4 — Structured logging end-to-end

**Scope**
- Every public service method emits `*.start` / `*.end` / `*.error` events with `corr` (UUIDv4 generated at request entry).
- IPC frames log `ipc.request.recv`, `ipc.response.sent` with `op`, `bytes`, `dur_ms`, `client_pid`.
- Default log path: `%LOCALAPPDATA%\WhereIsIt\logs\YYYY-MM-DD.jsonl`, rotated daily, 30-day retention.
- PII rule: queries hashed with SHA-256 truncated to 12 hex chars; only token count + hash logged at INFO. Full query at DEBUG only.

**Tests**
- `LoggerSchemaTests.cpp`: every emitted line is valid JSON and contains required keys.
- `CorrelationIdPropagationTests.cpp`: a single search request produces a chain of events all sharing `corr`.

**Acceptance**
- Run a search end-to-end → grep `corr` produces a complete trace.
- No log line exceeds 4 KB; no log line contains a raw filesystem path beyond basename + 12-char path hash.

**Commit**: `phase4: structured JSONL logging with correlation ids`

---

### Phase 5 — C++/WinRT runtime component (`WhereIsIt.Engine.WinRT`)

**Scope**
- IDL (`WhereIsIt.Engine.idl`) defines `EngineClient` runtime class implementing the methods from §3.2 (`StartAsync`, `SearchAsync`, etc.) using `IAsyncAction` / `IAsyncOperation<T>`.
- Implementation wraps `IndexingEngine` and exposes:
  - `event TypedEventHandler<EngineClient, StatusChangedArgs> StatusChanged`
  - `event TypedEventHandler<EngineClient, ResultsChangedArgs> ResultsChanged`
- Marshal `std::wstring` → `winrt::hstring`, `uint32_t` row ids → `winrt::Windows::Foundation::Collections::IVectorView<uint32_t>`.
- All blocking work happens on `winrt::resume_background()`; UI thread is never blocked.

**Tests**
- `tests/core/winrt/EngineClientWinRTTests.cpp` (using cppwinrt host) — start, search "*", get one row, stop. Assert no thread joins on UI thread.
- C# smoke test (`WhereIsIt.App.Tests/InProcEngineClientSmoke.cs`) creates the runtime class, runs a search against a fixture, asserts ≥1 row.

**Acceptance**
- C# project references `WhereIsIt.Engine.WinRT.winmd`; IntelliSense resolves all members.

**Commit**: `phase5: C++/WinRT runtime component for engine`

---

### Phase 6 — WinUI 3 app shell & MVVM layer

**Scope**
- Bootstrap in `App.xaml.cs`: build `IServiceCollection`, register:
  - `IEngineClient` via `EngineClientFactory` (in-proc when elevated, pipe otherwise)
  - All app services (`IDialogService`, `IClipboardService`, `IShellService`, `ISettingsService`)
  - All ViewModels as transient
- Views (XAML) bind only via `{x:Bind}` two-way to ViewModel properties; **no code-behind logic** beyond constructor and `InitializeComponent`. Views must be a thin shell.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm. ViewModels never reference `Microsoft.UI.*` types.
- Implement these ViewModels (one-to-one mapping with current Win32 controls):
  - `MainViewModel` — owns `SearchBoxViewModel`, `ResultsListViewModel`, `StatusBarViewModel`. Wires search debounce (`TimeSpan.FromMilliseconds(120)`) via `System.Reactive`.
  - `SearchBoxViewModel` — `Query` (string), `SubmitCommand`.
  - `ResultsListViewModel` — `ObservableCollection<ResultRowViewModel> Rows`, `SelectedRow`, `SortKey`, `SortDescending`. Subscribes to `IEngineClient.ObserveResults` and projects ids → row VMs.
  - `ResultRowViewModel` — `Name`, `ParentPath`, `SizeText`, `ModifiedText`, `AttributesText`. Lazy-fetches via `IEngineClient.GetRowAsync`.
  - `StatusBarViewModel` — `StatusText`, `RecordCount`. Subscribes to `StatusChanges` + `MetricsChanges`.
  - `SettingsViewModel` — bound to `IndexScopeConfig`.

**Tests** (`WhereIsIt.App.Tests`)
- Each ViewModel: construct with `Substitute.For<IEngineClient>()` and other mocks. Test:
  - `SearchBoxViewModel`: typing populates `Query`; `SubmitCommand` calls `IEngineClient.SearchAsync` once after debounce window elapses (use `TestScheduler` from System.Reactive.Testing).
  - `ResultsListViewModel`: feed a fake `IAsyncEnumerable<IReadOnlyList<uint>>`; assert `Rows` reflects the ids; assert `SortAsync` called when `SortKey` changes.
  - `ResultRowViewModel`: lazy fetch happens once and is cached; bytes formatting matches Win32 legacy (golden table fixture).
  - `StatusBarViewModel`: status observable updates property on dispatcher.
  - `MainViewModel`: search-box change triggers exactly one `SearchAsync` per debounce window; cancellation propagated when query changes mid-flight.
- Coverage gate: ≥85% on `/src/app/ViewModels/**`.

**Acceptance**
- App launches, shows live results against the in-proc engine.
- All ViewModel tests green without any UI thread / dispatcher dependency (use `SynchronizationContext`-free design or inject `IDispatcherQueue`).

**Commit**: `phase6: WinUI 3 app shell + MVVM ViewModels with unit tests`

---

### Phase 7 — Pipe client + service-mode parity

**Scope**
- Refactor existing `ServiceIPC.{h,cpp}` into `WhereIsIt.Service` (server) + `WhereIsIt.Pipe.Client` (C# client implementing `IEngineClient`).
- Adopt protocol v2 (§3.3). Server supports v1 for one release for back-compat with the legacy exe.
- `EngineClientFactory`: probe pipe → fallback to in-proc WinRT. Identical decision logic as today's `WhereIsIt.cpp:WinMain`.

**Tests**
- `tests/parity/run_parity.ps1` — runs the same `tests/cases/query_matrix.json` against:
  - In-proc engine via `InProcEngineClient`
  - Service via `PipeEngineClient`
- `parity_comparator` (existing) compares `results.jsonl` from both runs. Emits `parity-summary.json` (`pass/fail`, mismatch category, first mismatch key). CI fails on any mismatch.
- Add canary test: kill the service mid-query → C# client surfaces a typed `EngineDisconnectedException`, ViewModel surfaces a "service disconnected" status, no crash.

**Acceptance**
- `parity-summary.json.pass == true` for the full matrix on Windows CI.
- Legacy Win32 exe parity still passes.

**Commit**: `phase7: pipe client + admin/non-admin parity green`

---

### Phase 8 — CI, packaging, deterministic dev loop

**Scope**
- GitHub Actions workflow `.github/workflows/ci.yml`:
  1. `windows-latest` runner.
  2. `nuget restore`, `msbuild WhereIsIt.sln /m`.
  3. `ctest --output-on-failure`.
  4. `dotnet test --collect:"XPlat Code Coverage"`.
  5. `pwsh tests/parity/run_parity.ps1` against fixture dataset built by `tests/fixtures/generate.ps1`.
  6. Upload `artifacts/parity/*` and coverage reports.
- `pwsh ./dev.ps1 watch` runs incremental build + unit tests on file change.
- Crash-dump capture: WinUI 3 app installs an `AppDomain.UnhandledException` handler that writes a minidump + final 200 log lines to `%LOCALAPPDATA%\WhereIsIt\crashes\`.

**Acceptance**
- Green CI on a no-op PR.
- One-command repro: `pwsh ./test.ps1` runs every test layer locally in <2 min on a warm cache.

**Commit**: `phase8: CI, packaging, deterministic dev loop`

---

### Phase 9 — Cutover & legacy removal

**Scope**
- Two-week burn-in with both apps shipping side-by-side (legacy `WhereIsIt.exe` + new `WhereIsIt.App` MSIX).
- Once parity & telemetry are clean:
  - Delete `WhereIsIt.Legacy` project, the old top-level `.cpp/.h` files, `WhereIsIt.rc`, `WhereIsIt.vcxproj{,.filters}`, `framework.h`, `targetver.h`, `Resource.h`.
  - Move `WhereIsIt.ico` into `/src/app/Assets/`.
  - Drop IPC protocol v1 support.

**Acceptance**
- Single shippable artifact: `WhereIsIt.App` MSIX + `WhereIsIt.Service.exe`.
- Repo no longer contains any Win32 UI code.

**Commit**: `phase9: remove legacy Win32 app and protocol v1`

---

## 5. Definition of Done (whole migration)

- [ ] Every C++ domain module ≥90% line coverage; every C# ViewModel ≥85%.
- [ ] Every public service method emits `start`/`end`/`error` events with shared `corr`.
- [ ] `tests/parity/run_parity.ps1` returns `pass=true` for the full query matrix.
- [ ] No Win32 API call exists outside `/src/adapters/win32/` and `/src/service/`.
- [ ] No `using Microsoft.UI.*` outside `/src/app/Views/` and `App.xaml.cs`.
- [ ] No code-behind logic in any `.xaml.cs` beyond constructor + `InitializeComponent`.
- [ ] CI green on a clean clone with `pwsh ./build.ps1 && pwsh ./test.ps1`.
- [ ] Legacy `WhereIsIt.exe` removed.

---

## 6. Working Agreements for the Executing Agent

1. **One phase per PR.** Don't combine phases.
2. **Never delete legacy files before Phase 9.** Use the strangler pattern: new code coexists, legacy keeps building.
3. **Tests land in the same commit as the code they cover.** No follow-up test commits.
4. **No new product features.** If you find a bug, file it in `BUGS.md`; do not fix during migration unless it blocks parity.
5. **Schema changes are versioned.** Bump `PipeProtocol.Version` and keep n-1 support until Phase 9.
6. **All async APIs accept `CancellationToken`.** Every long operation must be cancellable.
7. **No `Thread.Sleep` / `std::this_thread::sleep_for` in tests.** Use injected `IClock` and deterministic schedulers.
8. **Logs are the contract for AI debugging.** Don't change event names without updating `docs/log-events.md` (created in Phase 4).

---

## 7. Quick-Reference: First Commands for the Next Agent

```powershell
# Phase 1 starter
git switch claude/winui3-testable-refactor-lmHdh
mkdir src, src/core, src/core/domain, src/core/app, src/core/ports, `
      src/adapters/win32, src/engine-winrt, src/pipe-client, src/service, `
      src/app, src/legacy
# Move existing files into /src/legacy without breaking the legacy vcxproj:
git mv WhereIsIt.cpp Engine.cpp QueryEngine.cpp ServiceIPC.cpp ... src/legacy/
# Update WhereIsIt.vcxproj include paths.
dotnet new sln -n WhereIsIt
dotnet new winui3 -o src/app -n WhereIsIt.App     # requires WinAppSDK template pack
dotnet new xunit -o tests/app -n WhereIsIt.App.Tests
# Add C++ projects to the .sln manually (dotnet sln add doesn't accept .vcxproj on all SDKs;
# fall back to `MSBuild` / Visual Studio CLI: `devenv WhereIsIt.sln /Command "File.AddExistingProject"` or edit the solution file).
```
