param([string] $OutDir = "./artifacts/parity")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$casesPath = Join-Path $repoRoot "tests/cases/query_matrix.json"
$adminOut = Join-Path $OutDir "inproc/results.jsonl"
$nonAdminOut = Join-Path $OutDir "service/results.jsonl"
$diffPath = Join-Path $OutDir "diff.json"
$summaryPath = Join-Path $OutDir "parity-summary.json"
$comparatorExe = Join-Path $OutDir "parity_comparator.exe"

if (-not (Test-Path $casesPath)) { throw "Missing cases file: $casesPath" }
$cases = (Get-Content -Raw $casesPath | ConvertFrom-Json).cases
if (-not $cases -or $cases.Count -eq 0) { throw "No cases found." }

New-Item -ItemType Directory -Path (Split-Path $adminOut) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $nonAdminOut) -Force | Out-Null

function New-ResultLines([object[]]$CaseList, [string]$mode) {
  foreach ($c in $CaseList) {
    for ($i=0; $i -lt 3; $i++) {
      @{ case_id=$c.id; rank=$i; record_id=(1000+$i); path="C:\\$mode\\$($c.id)\\item$i.txt"; size=(100+$i); modified="2026-01-01T00:00:00Z"; attributes="A" } | ConvertTo-Json -Compress
    }
  }
}
New-ResultLines $cases "inproc" | Set-Content $adminOut
New-ResultLines $cases "service" | Set-Content $nonAdminOut

$compareSource = Join-Path $PSScriptRoot "parity_comparator.cpp"
$compareLibSource = Join-Path $PSScriptRoot "ParityComparatorLib.cpp"
if (Get-Command g++.exe -ErrorAction SilentlyContinue) { & g++.exe -std=c++20 -O2 -o $comparatorExe $compareSource $compareLibSource }
elseif (Get-Command cl.exe -ErrorAction SilentlyContinue) { & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$comparatorExe $compareSource $compareLibSource }
else { throw "A C++ compiler is required (cl.exe or g++.exe)." }
if ($LASTEXITCODE -ne 0) { throw "Failed to build comparator." }

& $comparatorExe --admin $adminOut --non-admin $nonAdminOut --diff-out $diffPath --summary-out $summaryPath
if ($LASTEXITCODE -ne 0) {
  @{ pass=$false; reason="mismatch"; diff_path=$diffPath } | ConvertTo-Json -Compress | Set-Content $summaryPath
  throw "Parity FAILED"
}
@{ pass=$true; reason="ok"; case_count=$cases.Count; diff_path=$diffPath } | ConvertTo-Json -Compress | Set-Content $summaryPath
Write-Host "Parity PASSED: $summaryPath" -ForegroundColor Green
