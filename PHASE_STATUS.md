# Refactor Phase Status

## Completed
- **Phase 0 (guardrails)**
  - Experimental runtime flag (`Experimental.UseRefactoredPipeline`) defaulting to off.
  - Logging seam (`ILogger`, `NullLogger`).
  - Parity baseline artifacts (`tests/cases`, `tests/fixtures`, `tests/parity`).
- **Phase 1 (query seam extraction)**
  - Added `QueryDomain` seam (`CompilePlan`, `HasInlineSortDirective`).
  - `IndexingEngine::SearchThread` routes query-plan compilation through `QueryDomain`.
- **Phase 2 (sorting/parity hardening)**
  - Extracted reusable sort module (`SortService`) and wired service-side sorting through it.
  - Refactored parity comparator into reusable C++ library (`ParityComparatorLib`).
  - Added dedicated C++ unit tests for comparator core and sort behavior.
- **Phase 3 (path/size domain extraction)**
  - Added `PathSizeDomain` seam for giant-file size fallback and mapped-size resolution.
  - Centralized path join behavior through `JoinParentAndName` and covered with C++ unit tests.

- **Phase 4 (ports for scan/USN boundaries)**
  - Introduced `IDriveEnumerator` + `DriveEnumeratorWin32` and `IUsnJournalReader` + `UsnJournalReaderWin32` seams.
  - Adopted `IDriveEnumerator` in drive discovery path with default Win32 adapter.

## In Progress
- **Phase 5 (persistence and IPC boundaries)**
  - Planning `IIndexStorage` boundary and IPC response canonicalization/logging.

## Next
- Introduce `IIndexStorage` abstraction and move save/load code behind adapter.
- Add Windows CI step that runs parity comparator on captured admin/non-admin outputs.
