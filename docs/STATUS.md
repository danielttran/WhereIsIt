# WhereIsIt — Project Status & Next-Agent Handoff

> **Single source of truth.** Replaces the deleted `MIGRATION_PLAN.md` and `CURRENT_STATUS.md`. If you are the next agent picking this up, read this file first.

**Goal:** voidtools' Everything, but as a modern WinUI 3 app — better UI, faster when possible, native C++ engine.

**Branch:** `claude/review-winui3-migration-kpecT` (do not push to `main`).

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
| `IEngineClient` contract | ⚠️ minimal (Search/Sort/GetRow + 3 IObservables) | `src/pipe/WhereIsIt.Pipe.Client/IEngineClient.cs` |
| `InProcEngineClient` | ⚠️ **toy `Directory.EnumerateFiles` scanner** — depth 8, capped at 2000 hits | `app/WhereIsIt.App.Core/Services/InProcEngineClient.cs` |
| `PipeEngineClient` | ⚠️ **stub** returning hardcoded `[1u, 2u]` | `src/pipe/WhereIsIt.Pipe.Client/PipeEngineClient.cs` |
| C++ engine | ❌ **not implemented**. `src/core/`, `src/adapters/win32/`, `src/engine/winrt/`, `service/WhereIsIt.Service/` are empty vcxproj stubs. Real source lives untouched in `src/legacy/`. | |
| xUnit tests | ✅ 16 green | `tests/app/`, run via `tests/ci/run_all.ps1` |
| GitHub Actions | ✅ CI workflow (`windows-2022`, dotnet 10, runs tests + builds exe) | `.github/workflows/ci.yml` |

---

## What's left, in priority order

### P-1 — Verify the build on the new Windows + VS 2026 box

Before changing anything else:

```powershell
dotnet build app\WhereIsIt.App\WhereIsIt.App.csproj
powershell -File tests\ci\run_all.ps1
.\app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\WhereIsIt.App.exe
```

The UI changes in this branch (multi-column layout, shortcuts, Mica, SettingsWindow) were authored on a Linux sandbox without a C# compiler. **Expect minor compile fixups** — for example, namespace tweaks for `MicaBackdrop` (it's in `Microsoft.UI.Xaml.Media`), or `KeyboardAccelerator`/`VirtualKey` resolution. Fix forward, do not revert.

### P0 — Real native C++ engine (the headline feature)

Source material is `src/legacy/` (Engine.cpp, RecordPool, StringPool, QueryEngine, UsnJournalReaderWin32, DriveEnumeratorWin32, ServiceIPC, etc.). Port — don't rewrite — into the new layered structure that already exists as empty scaffolds:

- `src/core/domain/` — pure types, **no `<Windows.h>`**: `FileRecord`, `RecordPool`, `StringPool`, `Query`, `QueryParser`, `QueryMatcher`. Some headers/cpps are scaffolded (~354 LOC) — extend them.
- `src/core/ports/` — interfaces already drafted: `IClockPort.h`, `IDriveEnumeratorPort.h`, `IEventSignalPort.h`, `IFileSystemScannerPort.h`, `IIndexStoragePort.h`, `ILoggerPort.h`, `IUsnJournalReaderPort.h`. Confirm shapes; do not break them.
- `src/core/app/` — orchestration services: `IndexBuildService`, `SearchService`, `IncrementalUpdateService` (new).
- `src/adapters/win32/` — `Win32DriveEnumerator`, `Win32UsnJournalReader` (`FSCTL_QUERY_USN_JOURNAL`/`FSCTL_READ_USN_JOURNAL`), `Win32MftScanner` (`FSCTL_ENUM_USN_DATA` over `\\.\C:`), `Win32IndexStorage`, `SystemClock`, `Win32EventSignal`. Port from `src/legacy/UsnJournalReaderWin32.{h,cpp}` and `DriveEnumeratorWin32.{h,cpp}`.
- Populate the empty `<ClCompile>` / `<ClInclude>` in `src/core/WhereIsIt.Core.vcxproj`, `src/adapters/win32/WhereIsIt.Adapters.Win32.vcxproj`.

Targets to validate after P0:
- 1M-record fixture: full search < 250 ms.
- First 50 results visible < 30 ms after the 120 ms debounce already wired in `MainViewModel.cs:42`.
- Zero per-row allocation on scroll.
- No 2000-cap, no depth-8 cap.

### P0 (continued) — C++/WinRT bridge

- `src/engine/winrt/WhereIsIt.Engine.WinRT.vcxproj` is an empty stub. Populate it as a WinRT runtime component.
- New: `src/engine/winrt/EngineClient.idl` declaring `SearchAsync`, `SortAsync`, `GetRowAsync`, plus event sources for `StatusChanges`/`MetricsChanges`/`ObserveResults`. WinRT events project to `IObservable<T>` on the C# side via a thin adapter.
- `EngineClient.cpp` instantiates the C++ `SearchService`. Hot path keeps `std::wstring_view` over `StringPool` — no copies cross the ABI; C# pulls rows lazily via `GetRowAsync`.
- `app/WhereIsIt.App.Core/WhereIsIt.App.Core.csproj`: add CsWinRT (`Microsoft.Windows.CsWinRT`) and project-reference the WinRT component.
- Replace the body of `InProcEngineClient.cs` (`app/WhereIsIt.App.Core/Services/InProcEngineClient.cs`) with a thin wrapper around the WinRT projection. Keep the `IEngineClient` shape identical so VMs and tests stay green.

### P1 — Tighten `IEngineClient` (after the bridge works)

Today's contract was sized for the toy scanner (`src/pipe/WhereIsIt.Pipe.Client/IEngineClient.cs`). For Everything-grade:

- `SearchHandle` so concurrent searches don't clobber each other.
- `IAsyncEnumerable<IReadOnlyList<uint>> ObserveResults(SearchHandle, CancellationToken)` instead of a property — lets the UI render incrementally without subject pumps.
- `StartAsync`/`StopAsync` so the indexer can warm up before first keystroke.
- `GetFullPathAsync(uint id)` to defer path materialization off the engine.

Mirror in the WinRT IDL and the C# interface in lockstep. Update VMs (`MainViewModel`, `ResultsListViewModel`) and tests (`tests/app/ViewModels/MainViewModelTests.cs`) in the same PR.

### P2 — Native C++ pipe service (non-admin mode)

- Populate `service/WhereIsIt.Service/WhereIsIt.Service.vcxproj`: Win32 service host (`StartServiceCtrlDispatcher` + `RegisterServiceCtrlHandlerEx`) linking `WhereIsIt.Core.lib`.
- Named-pipe server over `\\.\pipe\WhereIsIt`, overlapped I/O. Frame layout already specified in `src/pipe/WhereIsIt.Pipe.Client/PipeProtocolV2.cs` — implement the matching server in C++.
- Service runs as `LocalSystem` to read MFT/USN on every fixed volume; non-admin client connects, no UAC prompt.
- Real `PipeEngineClient.cs`: `NamedPipeClientStream` + length-prefixed framing.
- Installer mode in the service exe (`WhereIsIt.Service.exe --install`) → `CreateService`. No MSI yet.
- `tests/parity/` — diff results between in-proc (admin) and pipe (non-admin).

### P3 — Performance gates

- New `WhereIsIt.Bench` BenchmarkDotNet project. Fail CI on >10% regression in parse, search, sort.
- Footprint gate in CI: assert `WhereIsIt.App.exe` ≤ 8 MB after `dotnet publish -c Release`.

### P4 — Legacy removal (last step)

After P0–P3 burn in:

- Delete `WhereIsIt.vcxproj`, `WhereIsIt.vcxproj.filters`, `WhereIsIt.vcxproj.user`.
- Delete `src/legacy/` entirely.
- Delete root `WhereIsIt.ico`, `framework.h`, `targetver.h` if unreferenced.

---

## PR-by-PR sequence (recommended)

1. **PR-A** — port `src/legacy/` → `src/core/domain/` (pure) + extend `src/core/ports/`. GoogleTest suite in `tests/core/`. Domain compiles standalone, no `<Windows.h>`.
2. **PR-B** — populate `src/adapters/win32/`. Smoke tests in `tests/adapters/` against real volumes (Windows-only).
3. **PR-C** — `WhereIsIt.Engine.WinRT` (IDL + impl + CsWinRT projection).
4. **PR-D** — rewrite `InProcEngineClient.cs` as thin WinRT wrapper. Existing 16 xUnit stay green; add bench microbench.
5. **PR-E** — tighten `IEngineClient` (P1).
6. **PR-F** — native C++ pipe service + real `PipeEngineClient` (P2).
7. **PR-G** — perf + footprint gates (P3).
8. **PR-H** — legacy removal (P4).

Each PR lands with green tests; no PR depends on a future PR's contract.

---

## Decisions captured

- **Indexer:** native C++, ported from `src/legacy/` into `src/core/domain` → `src/core/app` → `src/adapters/win32`, bridged to C# via `WhereIsIt.Engine.WinRT` runtime component.
- **Service:** native C++ Win32 service hosting `WhereIsIt.Core.lib` behind a named-pipe server.
- **UI:** full Everything-grade — already shipped in this branch (multi-column, shortcuts, context menu, Mica, Settings).

---

## Project layout (current)

```
app/
  WhereIsIt.App/                 WinUI 3 exe (net10.0-windows10.0.19041.0)
    App.xaml{,.cs}
    MainWindow.xaml{,.cs}        ← overhauled this branch
    SettingsWindow.xaml{,.cs}    ← new this branch
    AppBootstrap.cs              DI composition root
    Services/DispatcherQueueAppDispatcher.cs

  WhereIsIt.App.Core/            Class library (net10.0-windows, no WinUI)
    ViewModels/                  6 VMs
    Services/
      InProcEngineClient.cs      ⚠️ toy scanner — replace with WinRT wrapper
      EngineClientFactory.cs     probes pipe → falls back to InProc
      AppDispatcher.cs

src/
  core/                          C++ domain — vcxproj empty, source partly scaffolded
    domain/{Path,Query,Records,Sort}/
    logging/
    ports/
  adapters/win32/                C++ adapter stubs — vcxproj empty
  engine/winrt/                  C++/WinRT stub — vcxproj empty
  legacy/                        Original Win32 source — port material, do not delete yet
  pipe/
    WhereIsIt.Pipe.Client/       IEngineClient + stub PipeEngineClient + protocol types

service/
  WhereIsIt.Service/             C++ service vcxproj (stub, not implemented)

tests/
  app/                           xUnit (16 tests, green)
  core/, adapters/, parity/, smoke/, unit/, cases/, fixtures/   ← scaffolds for C++ tests
  ci/run_all.ps1

.github/workflows/ci.yml         ← new this branch

WhereIsIt.slnx                   App, App.Core, Pipe.Client, Tests
WhereIsIt.vcxproj                Legacy C++ exe — keep until P4
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
