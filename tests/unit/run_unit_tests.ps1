param(
    [Parameter(Mandatory = $false)] [string] $OutDir = "./artifacts/unit"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$parityTestSrc = Join-Path $PSScriptRoot "parity_comparator_tests.cpp"
$sortTestSrc = Join-Path $PSScriptRoot "sort_service_tests.cpp"
$libSrc = Join-Path $repoRoot "tests/parity/ParityComparatorLib.cpp"
$sortServiceSrc = Join-Path $repoRoot "SortService.cpp"
$parityExePath = Join-Path $OutDir "parity_comparator_tests.exe"
$sortExePath = Join-Path $OutDir "sort_service_tests.exe"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    Push-Location $repoRoot
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$parityExePath $parityTestSrc $libSrc
    $compileCode1 = $LASTEXITCODE
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$sortExePath $sortTestSrc $sortServiceSrc
    $compileCode2 = $LASTEXITCODE
    Pop-Location
    if ($compileCode1 -ne 0 -or $compileCode2 -ne 0) { throw "Failed to build unit tests with cl.exe." }
}
elseif (Get-Command g++.exe -ErrorAction SilentlyContinue) {
    & g++.exe -std=c++20 -O2 -o $parityExePath $parityTestSrc $libSrc
    if ($LASTEXITCODE -ne 0) { throw "Failed to build parity unit tests with g++.exe." }
    & g++.exe -std=c++20 -O2 -o $sortExePath $sortTestSrc $sortServiceSrc
    if ($LASTEXITCODE -ne 0) { throw "Failed to build sort unit tests with g++.exe." }
}
else {
    throw "A C++ compiler is required (expected cl.exe or g++.exe)."
}

& $parityExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $sortExePath
exit $LASTEXITCODE
