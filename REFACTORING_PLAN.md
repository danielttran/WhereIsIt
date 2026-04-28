# WhereIsIt Refactor Plan for Unit Testing + AI-Debuggable Logging

## Goals
- Break the codebase into small, testable modules with clear dependencies.
- Add structured, high-signal logging that lets AI (or humans) diagnose failures from logs only.
- Keep behavior stable while refactoring (strangler pattern + compatibility adapters).

## Current Pain Points (from code layout)
- `IndexingEngine` mixes lifecycle, scanning, USN monitoring, search, sorting, path resolution, persistence, and status notifications.
- Win32/system APIs are called directly from business logic, making unit tests difficult.
- Query parsing, filtering, and sort behavior are spread across multiple concerns.
- Concurrency behavior (workers/events/atomics) is hard to verify deterministically.

---

## Target Architecture (small chunks)

### 1) Core Domain (no Win32)
**Purpose:** deterministic business rules.

Modules:
- `domain/FileRecordView` (normalized, testable view model)
- `domain/PathBuilder` (full path, parent path)
- `domain/FileSizeResolver` (regular/giant-map semantics)
- `domain/Query` (parser + matcher + sort policy)

Test type:
- pure unit tests (no threads, no OS calls).

### 2) Ports (interfaces)
**Purpose:** isolate side effects.

Interfaces:
- `IDriveEnumerator`
- `IFileSystemScanner`
- `IUsnJournalReader`
- `IClock`
- `IEventSignal`
- `IStatusNotifier`
- `IIndexStorage`
- `ILogger`

Test type:
- mock-driven unit tests around ports.

### 3) Adapters (Win32 implementations)
**Purpose:** contain all Win32-specific API calls.

Modules:
- `adapters/win32/DriveEnumeratorWin32`
- `adapters/win32/UsnJournalReaderWin32`
- `adapters/win32/EventSignalWin32`
- `adapters/win32/IndexStorageBinary`
- `adapters/win32/StatusNotifierHwnd`

Test type:
- focused integration tests (Windows CI).

### 4) Application Services (orchestrators)
**Purpose:** coordinate workflows with ports.

Modules:
- `app/IndexBuildService` (discover + scan + persist)
- `app/IndexUpdateService` (USN delta queue/apply)
- `app/SearchService` (query execution over record store)
- `app/SortService`

Test type:
- service-level tests using fake ports.

### 5) Composition Root
**Purpose:** wire concrete adapters to interfaces.

Modules:
- `bootstrap/EngineFactory`
- `bootstrap/ServiceFactory`

Test type:
- smoke tests only.

---

## Logging Design (AI-friendly)

### Logging format
Use JSON Lines (one object per line) for machine parsing:
```json
{"ts":"2026-04-28T12:00:00.123Z","lvl":"INFO","event":"scan.file.indexed","corr":"IDX-...","module":"IndexBuildService","drive":"C:","mft":12345,"parent":33,"size":4096,"dur_ms":0.14}
```

### Required fields
- `ts`, `lvl`, `event`, `module`, `corr` (correlation id)
- operation-specific context keys
- `dur_ms` where meaningful
- on errors: `err_code`, `err_class`, `err_msg`, `retryable`

### Event taxonomy (stable names)
- Lifecycle: `engine.start`, `engine.stop`
- Scanning: `scan.start`, `scan.file.indexed`, `scan.end`
- USN: `usn.poll.start`, `usn.record.upsert`, `usn.record.delete`, `usn.apply.end`
- Search: `query.parse`, `query.execute.start`, `query.execute.end`
- Sort: `sort.start`, `sort.end`
- Persistence: `index.save.start`, `index.save.end`, `index.load.start`, `index.load.end`
- IPC: `ipc.request.recv`, `ipc.response.sent`

### Log levels and sampling
- `DEBUG`: detailed per-record events (sampled or gated)
- `INFO`: operation boundaries + counts
- `WARN`: recoverable anomalies
- `ERROR`: operation failure

### PII/safety
- Do not log full user query by default; log token count and hashed query.
- Truncate path strings and include hash + basename for reproducibility.

---

## Test Strategy by Layer

### Domain unit tests (first priority)
- Query parser correctness (operators, precedence, invalid cases)
- Matcher behavior (case sensitivity, whole-word, regex mode)
- Sort determinism (ties + stable ordering)
- Path construction + parent extraction edge cases
- File size resolution (normal vs giant-file map)

### Service unit tests (second priority)
- `IndexBuildService` with fake scanner + fake storage
- `IndexUpdateService` applies delta ordering/idempotency
- `SearchService` cancellation and re-entrant query behavior

### Concurrency tests
- deterministic scheduler/fake event signal to simulate races
- verify no lost notifications between search worker and UI notifier

### Adapter integration tests (Windows CI)
- minimal USN polling smoke
- binary index save/load roundtrip
- pipe request/response schema compatibility

---

## Incremental Migration Plan (safe, production-grade)

### Phase 0: Baseline & guardrails
1. Freeze public behavior with golden tests around current query and sorting outputs.
2. Add runtime feature flag: `UseRefactoredPipeline` (default off).
3. Introduce `ILogger` interface and no-op/logger adapter.

### Phase 1: Extract query stack
1. Move query parsing/matching into `domain/Query`.
2. Add exhaustive unit tests for parser + matcher.
3. Keep current engine path but call new query module through adapter shim.

### Phase 2: Extract sorting service
1. Move sort logic to `app/SortService` + test tie-break determinism.
2. Add logs: `sort.start`, `sort.end` with counts/duration.

### Phase 3: Extract path/size domain logic
1. Move `GetFullPath*`, `GetParentPath*`, and size resolution into domain services.
2. Unit test deep tree/path edge cases and giant-map behavior.

### Phase 4: Introduce ports for scan and USN
1. Wrap Win32 scanning/USN operations behind interfaces.
2. Move orchestration into `IndexBuildService` and `IndexUpdateService`.
3. Add structured logs around each state transition.

### Phase 5: Persistence and IPC boundaries
1. Move save/load into `IIndexStorage` + adapter.
2. Add request/response schema logger in named pipe server/client.

### Phase 6: Flip feature flag gradually
1. Run canary with `UseRefactoredPipeline=1` for internal users.
2. Compare metrics/log parity vs legacy pipeline.
3. Remove legacy path after parity + burn-in window.

---

## Definition of Done per Module
- 90%+ line coverage for domain modules (excluding adapters).
- Every public service method emits start/end/error logs with correlation id.
- Deterministic tests for cancellation/race-sensitive behavior.
- No regression in result counts/order against golden datasets.
- Feature-flag rollback path validated.

---

## Suggested Initial File/Folder Layout
```
/src
  /domain
    QueryParser.h/.cpp
    QueryMatcher.h/.cpp
    PathBuilder.h/.cpp
    FileSizeResolver.h/.cpp
  /app
    IndexBuildService.h/.cpp
    IndexUpdateService.h/.cpp
    SearchService.h/.cpp
    SortService.h/.cpp
  /ports
    ILogger.h
    IIndexStorage.h
    IUsnJournalReader.h
    IFileSystemScanner.h
  /adapters/win32
    LoggerEtwOrFile.h/.cpp
    IndexStorageBinary.h/.cpp
    UsnJournalReaderWin32.h/.cpp
    FileSystemScannerWin32.h/.cpp
/tests
  /domain
  /app
  /integration
```

---

## First 2-week Execution Plan
- Week 1:
  - Extract query parser/matcher + add unit tests + add structured logs in legacy path.
- Week 2:
  - Extract sorting and path/size domain helpers + add unit tests + parity checks.

This sequence yields immediate unit-test value with minimal risk to scanning/USN behavior.
