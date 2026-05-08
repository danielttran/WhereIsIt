# Migration Progress Log

## 2026-05-07
- Audited prior Phase 1 scaffold and retained legacy source relocation.
- Started Phase 2 extraction by introducing pure domain copies under `src/core/domain`:
  - `Path/PathSizeResolver.*`
  - `Sort/SortPolicy.*`
  - `Records/FileRecordView.h`
- Added this progress log to track phase-by-phase implementation state.
- Continued with next phase by adding explicit core ports and Win32 adapter wrappers for drive enumeration and USN journal reading.
- Implemented next phase foundation: structured JSONL logger with optional correlation IDs in `src/core/logging/StructuredLogger.*`.
- Completed major remaining Phase 2 modules: QueryParser/QueryMatcher/QueryPlan and PathBuilder added under `src/core/domain`.
- Completed Phase 3 adapter surface by adding scanner/storage/signal/clock/logger ports and Win32 adapter implementations.
- Completed Phase 4 implementation pass: JSONL event logger with required schema fields, IPC metadata support, query hash/token logging, line-size guard, and retention purge.
- Bug audit fix: corrected JsonlEventLogger timestamp generation, file-time arithmetic, and JSON escaping robustness.
- Phase 4 hardening: upgraded query hashing to SHA-256 (12-hex truncation) and made oversize log handling emit valid fallback JSON instead of truncating partial lines.
- Phase 5 scaffold: added WinRT IDL, EngineClient async component skeleton, native WinRT smoke test, and C# in-proc smoke test placeholder.
- Phase 6 scaffold: added DI bootstrap, MVVM ViewModels (SearchBox/ResultsList/ResultRow/StatusBar/Main/Settings), engine-client contract, dispatcher abstraction, and initial ViewModel unit test.
- Phase 7 scaffold: added protocol-v2 pipe client contract, pipe client implementation skeleton, engine-client factory elevation probe, and parity runner that generates in-proc/pipe fixture outputs and validates parity-summary.json.
- Migration hardening steps 1-5: added phase acceptance checklist, tightened placeholder contract TODOs, added deterministic query fixtures, and made parity script fail-fast with machine-readable summary output.
- Audit remediation: PipeEngineClient now implements app IEngineClient + disconnect error; EngineClientFactory now pipe-probes then falls back; WinRT IDL aligned with SearchIdsAsync; expanded Phase6 ViewModel tests; parity runner split in-proc vs service fixtures with machine-readable summary.
