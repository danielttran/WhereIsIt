# WhereIsIt Audit for “Everything Replacement” Goal

## Executive summary
WhereIsIt already has a strong low-level foundation (NTFS MFT ingest + USN incremental updates + in-memory filename index), but it is currently an **engine prototype with a basic Win32 UI**, not yet a production-grade replacement for Everything.

The highest-risk gaps are:
1. **Correctness and concurrency safety** under live updates.
2. **Search feature parity** (query language, filters, sorting/ranking behavior).
3. **Operational hardening** (index integrity, corruption recovery, observability, testing).
4. **Platform/volume coverage and update reliability** beyond happy-path NTFS.

## What exists today (strengths)
- Multi-threaded indexing/search architecture with background workers.
- Fast case-insensitive substring matching over UTF-8 filename pool.
- NTFS direct MFT scan path plus generic filesystem fallback scan.
- Incremental updates via USN journal monitoring.
- Persisted index cache (`index.dat`) with drive serial validation.
- Owner-data list view UI for large result sets.

## Gap analysis

### 1) Data-race and consistency gaps (critical)
- `m_records`, `m_pool`, and `m_mftLookupTables` are read in search/UI paths while written by monitor/indexing paths with no shared lock strategy. This can produce stale reads, torn visibility, crashes, or undefined behavior under heavy churn.
- `m_status` is written/read cross-thread without synchronization.
- `m_isSearchRequested` is atomic, but its usage mixes with non-atomic shared state (`m_pendingSearchQuery`) and unsynchronized container reads.

**Impact:** reliability blocker for production use at scale.

### 2) Real-time update correctness gaps (critical)
- On create/rename-new, path metadata lookup uses `drive + filename` instead of full parent path, which can return wrong metadata or fail for nested files.
- Delete handling marks tombstones but does not compact/rebuild long-lived structures; index quality and memory use degrade over time.
- Journal truncation only sets status text and does not trigger auto-rebuild/recovery.

**Impact:** index drift, stale/incorrect results, silent trust erosion.

### 3) Search capability parity gaps (high)
Current search is filename substring, optional ancestor-token match if query contains `\\`.
Missing expected “Everything-class” capabilities:
- Rich query parser (AND/OR/NOT, phrase, parentheses).
- Built-in filters (`ext:`, `size:`, `date modified:`, `path:` etc.).
- Regex mode, case-sensitive mode, whole-word mode.
- Sort/rank controls (name/path/size/date, stable sort semantics).
- Saved searches, bookmarks, quick filters.

**Impact:** users cannot switch from Everything without losing core workflows.

### 4) Filesystem and scope coverage gaps (high)
- Discovery only includes fixed/removable/CD drives; excludes many practical sources (network-mounted scenarios, reparse handling strategy, policy-based excludes).
- Generic scan recursion has no explicit cycle protection for reparse-point edge cases.
- No explicit include/exclude model for roots, paths, patterns, attributes.

**Impact:** incomplete indexing footprint and unpredictable scan behavior.

### 5) Persistence and corruption-hardening gaps (high)
- Binary format has no checksums, no atomic swap/write-rename pattern, no partial-write recovery path.
- Minimal schema evolution strategy (single version check only).
- No guardrails around malformed or oversized fields from corrupted index files.

**Impact:** startup failures, potential data loss, brittle upgrades.

### 6) UX and product polish gaps (medium-high)
- Single-window, minimal controls; no settings surface for indexing policy, filters, or performance limits.
- No in-app telemetry for indexing progress details, error surfaces, or diagnostics.
- No explicit accessibility/i18n review path.

**Impact:** difficult adoption in real-world desktop usage.

### 7) Testability and release readiness gaps (critical)
- No automated tests (unit/integration/performance/regression).
- No deterministic benchmarking harness for indexing latency, query latency, and memory footprint.
- No fault-injection tests for USN truncation, permission failures, disk errors, or corrupt cache.

**Impact:** cannot claim production-grade reliability.

## Plan to bridge gaps

## Phase 0 — Guardrails before feature work (1–2 weeks)
1. Introduce thread-safety model:
   - Reader/writer synchronization around index state.
   - Immutable search snapshots for lock-minimized querying.
2. Add structured error/status channel.
3. Add auto-rebuild trigger on journal truncation and unrecoverable sync errors.

**Exit criteria:** no data races under stress; deterministic rebuild on sync-loss.

## Phase 1 — Index integrity and recovery (1–2 weeks)
1. Replace raw index save with atomic temp-write + fsync + rename.
2. Add header checksum + section checksums.
3. Add recovery policy:
   - If cache invalid/corrupt ⇒ rebuild automatically.
4. Version migration framework for schema evolution.

**Exit criteria:** resilient restart behavior, corrupted cache never crashes app.

## Phase 2 — Query engine parity core (2–4 weeks)
1. Build query parser/AST (boolean logic + phrase + parentheses).
2. Implement filter primitives (`path`, `ext`, `size`, `date`, `attrib`).
3. Add user-selectable modes: case-sensitive, regex, whole-word.
4. Implement deterministic multi-column sorting.

**Exit criteria:** equivalent daily search workflows for advanced users.

## Phase 3 — Coverage and policy (2–3 weeks)
1. Index scope configuration (include/exclude roots, patterns).
2. Reparse/link policy and cycle-safe traversal in generic mode.
3. Optional network/location strategy with clear user controls.

**Exit criteria:** predictable coverage with user-governed scope.

## Phase 4 — UX/productization (2–3 weeks)
1. Settings UI (indexing, filters, startup behavior, performance limits).
2. Better progress/error surfaces and troubleshooting panel.
3. Keyboard-heavy workflows, saved searches, quick filters.

**Exit criteria:** viable daily-driver experience for power users.

## Phase 5 — Quality gates and launch readiness (ongoing, starts early)
1. Automated tests:
   - Unit tests for parser/matcher/index serialization.
   - Integration tests with synthetic filesystem fixtures.
   - Long-run stress tests with concurrent updates.
2. Performance baselines and CI thresholds.
3. Crash/health telemetry and release checklist.

**Exit criteria:** production SLOs and repeatable release confidence.

## Suggested acceptance metrics (replace-only when all are met)
- Startup to searchable (warm cache): <= 1.5s on reference hardware.
- Search latency P95: <= 30ms for common queries on multi-million records.
- Correctness: zero mismatches in sampled path/metadata validation set.
- Stability: zero crashes in 72h churn stress run.
- Recovery: 100% successful automatic rebuild after forced cache corruption.

## Immediate next sprint backlog (recommended)
1. Thread-safe snapshot architecture RFC + implementation.
2. Journal-truncation auto-rebuild.
3. Atomic+checksummed index format v6.
4. Query parser MVP with AND/OR/NOT + `ext:` + `path:`.
5. Add first CI suite (serialization + search correctness + stress smoke).
