# WhereIsIt — Current State (2026-05-07)

> Read this before reading `MIGRATION_PLAN.md`. The plan describes the aspirational C++/WinRT architecture. Actual implementation diverged — this doc is the ground truth.

---

## What was shipped (on `main`, 16/16 tests green)

| Area | Status | Notes |
|---|---|---|
| WinUI 3 exe builds | ✅ | `app/WhereIsIt.App/`, WinAppSDK 2.0.1, net10.0, `WindowsPackageType=None` |
| MVVM ViewModels | ✅ | All 6 VMs in `app/WhereIsIt.App.Core/ViewModels/` |
| Data binding (XAML) | ✅ | `MainWindow.xaml` with search TextBox, results ListView, status bar |
| Core/exe split | ✅ | Core = `net10.0-windows` (no WinUI), exe = `net10.0-windows10.0.19041.0` |
| DI bootstrap | ✅ | `AppBootstrap.cs` wires all services; real `DispatcherQueueAppDispatcher` |
| InProcEngineClient | ⚠️ | Pure C# `Directory.EnumerateFiles` — **not the C++ USN journal engine** |
| PipeEngineClient | ⚠️ | Stub: returns hardcoded rows `[1u, 2u]`; pipe server not implemented |
| xUnit tests | ✅ | 16 tests, all green, CI via `tests/ci/run_all.ps1` |
| Solution file | ✅ | `WhereIsIt.slnx` lists all 4 C# projects |
| Dead file cleanup | ✅ | Root-level `.obj`, `.tmp`, `.aps`, `.log`, stale headers deleted |
| C++ legacy | 🔒 | Kept intact in `src/legacy/` and `WhereIsIt.vcxproj` — do not delete yet |

---

## Critical divergence from `MIGRATION_PLAN.md`

The plan called for a C++/WinRT runtime component (`WhereIsIt.Engine.WinRT`) as the bridge between C# and the C++ index engine. **This was not implemented.** The plan's Phases 2–5 were skipped.

**What this means in practice:**
- The app searches files by calling `Directory.EnumerateFiles` (slow, no index, caps at 2000 results)
- The real C++ engine (USN journal, in-memory RecordPool, ~1M records in <250ms) is **not connected**
- The C++ vcxproj stubs (`WhereIsIt.Core.vcxproj`, `WhereIsIt.EngineWinRT.vcxproj`, etc.) are empty — zero C++ source wired

**Other deviations:**
| Plan said | Actual |
|---|---|
| `net8.0-windows10.0.19041.0` | `net10.0-windows10.0.19041.0` |
| WinAppSDK 1.6 | WinAppSDK 2.0.1 (1.6 fails with .NET SDK 10.0.203 — PRI task DLL missing) |
| MSIX packaged | `WindowsPackageType=None` (unpackaged) — no MSIX, no signing |
| `IEngineClient` with `SearchHandle`, `IAsyncEnumerable` | Simpler interface: `SearchAsync(string)`, `IObservable<IReadOnlyList<uint>>` |
| `[ObservableProperty]` partial properties (AOT) | Field-based with `<NoWarn>MVVMTK0045</NoWarn>` (no AOT guarantee) |
| `pwsh` available | Only PowerShell 5.1 (`powershell.exe`); use `powershell -File` not `pwsh` |
| dotnet workloads installed | None installed; WinAppSDK 2.0.1 works without workloads, 1.6 did not |

---

## What's left (prioritized)

### P0 — App barely works; real engine needed

The current app is a UI skeleton over a toy scanner. For it to be the "fully working app" described in the goal, `InProcEngineClient` needs real indexing. Two paths:

**Option A — Pure C# indexer (no C++/WinRT bridge)**
- Use `DeviceIoControl` + P/Invoke to query NTFS MFT/USN journal from C#
- Or use `Everything SDK` or a managed MFT library
- Keeps the architecture purely C# — simpler, no C++ toolchain needed
- Gives sub-second indexed search without touching the C++ codebase

**Option B — Wire the C++/WinRT component (per original plan)**
- Resume Phases 2–5 of the migration plan
- Requires Visual Studio with C++/WinRT workload AND `v145` toolset (v145 = VS 2022)
- The C++ stubs (`src/core/`, `src/adapters/win32/`, `src/engine/winrt/`) need to be populated
- The C++ source files in `src/legacy/` are the source material — port into the new structure
- Then wire `InProcEngineClient` to call the `EngineClient` WinRT class instead of `Directory.EnumerateFiles`

**Recommendation:** Option A is faster. Option B is required by the migration plan.

### P1 — IEngineClient contract mismatch

The plan's `IEngineClient` (in `MIGRATION_PLAN.md §3.2`) is richer than what's implemented:
- Plan has `SearchHandle` (supports concurrent searches), current has none
- Plan has `IAsyncEnumerable<IReadOnlyList<uint>> ObserveResults(SearchHandle, CancellationToken)`, current has `IObservable<IReadOnlyList<uint>> ObserveResults` (property, not method)
- Plan has `StartAsync`/`StopAsync`, `SetScopeAsync`/`GetScopeAsync`, `GetFullPathAsync`, `GetParentPathAsync`
- Current implementation is minimal — sufficient for the UI prototype but not for full feature parity

Upgrade path: evolve the interface incrementally. The contract lives in `src/pipe/WhereIsIt.Pipe.Client/IEngineClient.cs`.

### P2 — PipeEngineClient is a stub

`PipeEngineClient` returns hardcoded rows. The named-pipe service (`WhereIsIt.Service`) exists as a C++ vcxproj stub but is not implemented. Until it's real, non-admin mode doesn't work.

### P3 — UI is minimal

`MainWindow.xaml` has a basic TextBox + ListView. Missing:
- Keyboard shortcut (Ctrl+F focus, Esc clear) 
- Column headers with sort click
- Right-click context menu (Open, Open folder, Copy path)
- Settings page (wired to `SettingsViewModel` but no XAML page)
- Status bar showing actual record count (property exists but wired count is from `ObserveResults`)
- Virtualized list (currently `ListView`, should be `ItemsRepeater` for performance)

### P4 — Phase 8 (CI) not done

No GitHub Actions workflow. `tests/ci/run_all.ps1` runs locally but there's no `.github/workflows/` configuration.

### P5 — Phase 9 (legacy removal) not done

After burn-in, delete:
- `WhereIsIt.vcxproj`, `WhereIsIt.vcxproj.filters`, `WhereIsIt.vcxproj.user`
- `src/legacy/` (all files)
- `WhereIsIt.ico`, `WhereIsIt.rc`, `framework.h`, `targetver.h`, `Resource.h` (in `src/legacy/`)

---

## Infrastructure facts for the next agent

```
OS:           Windows 11 Pro 10.0.26200
.NET SDKs:    9.0.303, 10.0.203
MSBuild:      18.3.3 (from .NET SDK 10 — expects VS v18.0)
Visual Studio: NOT INSTALLED (no devenv, no v145 toolset)
PowerShell:   5.1 only (pwsh/PowerShell 7 not in PATH — use `powershell -File`)
dotnet workloads: none installed
WinAppSDK:    2.0.1 (NuGet, no workload needed at this version)
```

**Critical constraint:** C++/WinRT compilation requires Visual Studio with C++/WinRT workload AND the v145 (or v143) MSVC toolset. These are NOT installed. Option A (pure C#) avoids this entirely.

---

## Project layout (actual, as of this date)

```
app/
  WhereIsIt.App/            WinUI 3 exe (net10.0-windows10.0.19041.0)
    App.xaml / App.xaml.cs  Application subclass
    MainWindow.xaml/.cs     Main UI window
    AppBootstrap.cs         DI composition root
    Services/
      DispatcherQueueAppDispatcher.cs
    WhereIsIt.App.csproj    refs Core project

  WhereIsIt.App.Core/       Class library (net10.0-windows, no WinUI)
    ViewModels/             All 6 ViewModels
    Services/
      InProcEngineClient.cs   ⚠️ Directory.EnumerateFiles, not USN journal
      EngineClientFactory.cs  Probes pipe → falls back to InProc
      AppDispatcher.cs        IAppDispatcher + InlineDispatcher
    WhereIsIt.App.Core.csproj refs Pipe.Client

src/
  core/                     C++ domain (vcxproj stub — no ClCompile items)
    domain/, ports/, logging/   Source files present but NOT wired to vcxproj
  adapters/win32/           C++ adapter stubs (vcxproj empty)
  engine/winrt/             C++/WinRT stub (vcxproj empty)
  legacy/                   Original C++ source files (kept for reference)
  pipe/
    WhereIsIt.Pipe.Client/  IEngineClient + PipeEngineClient stub + contract types

service/
  WhereIsIt.Service/        C++ service vcxproj (stub, not implemented)

tests/
  app/                      xUnit against App.Core only
    InProcEngineClientTests.cs
    PipeEngineClientTests.cs
    ViewModels/*.cs
    WhereIsIt.App.Tests.csproj  refs App.Core (NOT the exe)
  ci/
    run_all.ps1             `dotnet test tests/app/...`

WhereIsIt.slnx              Lists: App, App.Core, Pipe.Client, Tests
WhereIsIt.vcxproj           Legacy C++ exe (buildable, keep until Phase 9)
Directory.Packages.props    Central NuGet versions
test.ps1                    Wrapper: calls tests/ci/run_all.ps1
```

---

## How to run

```powershell
# Run all C# tests
powershell -File tests\ci\run_all.ps1

# Build the WinUI 3 app
dotnet build app\WhereIsIt.App\WhereIsIt.App.csproj

# Run the app (output is in app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\)
.\app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\WhereIsIt.App.exe
```
