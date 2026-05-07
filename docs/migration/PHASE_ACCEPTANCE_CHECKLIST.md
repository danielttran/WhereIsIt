# Phase Acceptance Checklist (Scaffold vs Acceptance)

## Phase 5 — WinRT runtime component
- [ ] Build WinRT component on Windows CI.
- [ ] Validate `WhereIsIt.Engine.WinRT.winmd` reference from C# app.
- [ ] Run `tests/core/winrt/EngineClientWinRTTests.cpp` in CI.

## Phase 6 — WinUI3 + MVVM
- [ ] Register full app services (`IDialogService`, `IClipboardService`, `IShellService`, `ISettingsService`).
- [ ] Add complete ViewModel tests for debounce/cancellation/sort/status subscriptions.
- [ ] Meet coverage gate ≥85% for ViewModels.

## Phase 7 — Pipe client + parity
- [ ] Wire real PipeEngineClient to service transport and protocol v2.
- [ ] Add disconnect canary test with typed exception surfacing.
- [ ] Execute parity matrix in Windows CI and assert `parity-summary.json.pass == true`.

## Phase 8 — CI + deterministic loop
- [ ] Add `.github/workflows/ci.yml` windows-latest matrix and artifact uploads.
- [ ] Ensure `pwsh ./test.ps1` runs full test stack on warm cache.

## Phase 9 — Cutover
- [ ] Burn-in complete.
- [ ] Remove legacy Win32 UI and protocol v1.
- [ ] Ship only WinUI app + service artifacts.
