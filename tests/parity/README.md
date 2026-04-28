# Phase 0 Parity Baseline

This folder provides a machine-friendly parity comparator so agents can iterate quickly.

## Requirements (Windows)
- PowerShell 7+ (`pwsh`) or Windows PowerShell 5.1
- C++ compiler available as `cl.exe` (preferred) or `g++.exe`

## Workflow
1. Run the app in **admin mode** and export results to JSONL (`admin/results.jsonl`).
2. Run the app in **non-admin mode** and export results to JSONL (`non_admin/results.jsonl`).
3. Compare with one command:

```powershell
pwsh ./tests/parity/run_parity.ps1 `
  -AdminResults ./artifacts/parity/admin/results.jsonl `
  -NonAdminResults ./artifacts/parity/non_admin/results.jsonl `
  -OutDir ./artifacts/parity
```

The command fails fast (exit code 1) on mismatch and emits:
- `diff.json` with exact mismatches
- `parity-summary.json` with pass/fail and first mismatch

`run_parity.ps1` compiles and runs a dedicated C++ comparator (`parity_comparator.cpp`)
with shared logic in `ParityComparatorLib.cpp`, so the parity gate stays within the C++ toolchain.

## JSONL schema
Each line must include:
- `case_id`
- `rank`
- `record_id`
- `path`
- `size`
- `modified`
- `attributes`

## C++ unit tests (comparator core)
Run:

```powershell
pwsh ./tests/unit/run_unit_tests.ps1
```
