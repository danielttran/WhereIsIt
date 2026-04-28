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

## In Progress
- **Phase 3 (path/size domain extraction)**
  - Planning extraction boundaries and parity checks for path and file-size resolution.

## Next
- Move parser/tokenization internals behind `QueryDomain` implementation while preserving `BuildQueryPlan` compatibility.
- Add Windows CI step that runs parity comparator on captured admin/non-admin outputs.
