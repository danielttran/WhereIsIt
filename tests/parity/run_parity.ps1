param(
    [Parameter(Mandatory = $true)] [string] $AdminResults,
    [Parameter(Mandatory = $true)] [string] $NonAdminResults,
    [Parameter(Mandatory = $false)] [string] $OutDir = "./artifacts/parity"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$adminPath = (Resolve-Path -Path $AdminResults).Path
$nonAdminPath = (Resolve-Path -Path $NonAdminResults).Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$compareSource = Join-Path $PSScriptRoot "parity_comparator.cpp"
$compareLibSource = Join-Path $PSScriptRoot "ParityComparatorLib.cpp"
$comparatorExe = Join-Path $OutDir "parity_comparator.exe"

if (-not (Test-Path -LiteralPath $compareSource)) {
    throw "Missing comparator source: $compareSource"
}
if (-not (Test-Path -LiteralPath $compareLibSource)) {
    throw "Missing comparator library source: $compareLibSource"
}

$diffPath = Join-Path $OutDir "diff.json"
$summaryPath = Join-Path $OutDir "parity-summary.json"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$comparatorExe $compareSource $compareLibSource
    if ($LASTEXITCODE -ne 0) { throw "Failed to build comparator with cl.exe." }
}
elseif (Get-Command g++.exe -ErrorAction SilentlyContinue) {
    & g++.exe -std=c++20 -O2 -o $comparatorExe $compareSource $compareLibSource
    if ($LASTEXITCODE -ne 0) { throw "Failed to build comparator with g++.exe." }
}
else {
    throw "A C++ compiler is required (expected cl.exe or g++.exe)."
}

Push-Location $repoRoot
& $comparatorExe `
  --admin $adminPath `
  --non-admin $nonAdminPath `
  --diff-out $diffPath `
  --summary-out $summaryPath
$exitCode = $LASTEXITCODE
Pop-Location

if ($exitCode -ne 0) {
    Write-Host "Parity FAILED. See $summaryPath and $diffPath" -ForegroundColor Red
    exit $exitCode
}

Write-Host "Parity PASSED. Summary: $summaryPath" -ForegroundColor Green
exit 0
