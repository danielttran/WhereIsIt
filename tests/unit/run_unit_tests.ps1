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
$pathSizeSrc = Join-Path $repoRoot "PathSizeDomain.cpp"
$pathSizeTestSrc = Join-Path $PSScriptRoot "path_size_domain_tests.cpp"
$driveEnumTestSrc = Join-Path $PSScriptRoot "drive_enumerator_tests.cpp"
$usnReaderTestSrc = Join-Path $PSScriptRoot "usn_reader_tests.cpp"
$parityExePath = Join-Path $OutDir "parity_comparator_tests.exe"
$sortExePath = Join-Path $OutDir "sort_service_tests.exe"
$pathSizeExePath = Join-Path $OutDir "path_size_domain_tests.exe"
$driveEnumExePath = Join-Path $OutDir "drive_enumerator_tests.exe"
$usnReaderExePath = Join-Path $OutDir "usn_reader_tests.exe"

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    Push-Location $repoRoot
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$parityExePath $parityTestSrc $libSrc
    $compileCode1 = $LASTEXITCODE
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$sortExePath $sortTestSrc $sortServiceSrc
    $compileCode2 = $LASTEXITCODE
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$pathSizeExePath $pathSizeTestSrc $pathSizeSrc
    $compileCode3 = $LASTEXITCODE
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$driveEnumExePath $driveEnumTestSrc (Join-Path $repoRoot "DriveEnumeratorWin32.cpp")
    $compileCode4 = $LASTEXITCODE
    & cl.exe /nologo /std:c++20 /EHsc /O2 /Fe:$usnReaderExePath $usnReaderTestSrc (Join-Path $repoRoot "UsnJournalReaderWin32.cpp")
    $compileCode5 = $LASTEXITCODE
    Pop-Location
    if ($compileCode1 -ne 0 -or $compileCode2 -ne 0 -or $compileCode3 -ne 0 -or $compileCode4 -ne 0 -or $compileCode5 -ne 0) { throw "Failed to build unit tests with cl.exe." }
}
elseif (Get-Command g++.exe -ErrorAction SilentlyContinue) {
    & g++.exe -std=c++20 -O2 -o $parityExePath $parityTestSrc $libSrc
    if ($LASTEXITCODE -ne 0) { throw "Failed to build parity unit tests with g++.exe." }
    & g++.exe -std=c++20 -O2 -o $sortExePath $sortTestSrc $sortServiceSrc
    if ($LASTEXITCODE -ne 0) { throw "Failed to build sort unit tests with g++.exe." }
    & g++.exe -std=c++20 -O2 -o $pathSizeExePath $pathSizeTestSrc $pathSizeSrc
    if ($LASTEXITCODE -ne 0) { throw "Failed to build path/size unit tests with g++.exe." }
    & g++.exe -std=c++20 -O2 -o $driveEnumExePath $driveEnumTestSrc (Join-Path $repoRoot "DriveEnumeratorWin32.cpp")
    if ($LASTEXITCODE -ne 0) { throw "Failed to build drive enumerator tests with g++.exe." }
    & g++.exe -std=c++20 -O2 -o $usnReaderExePath $usnReaderTestSrc (Join-Path $repoRoot "UsnJournalReaderWin32.cpp")
    if ($LASTEXITCODE -ne 0) { throw "Failed to build USN reader tests with g++.exe." }
}
else {
    throw "A C++ compiler is required (expected cl.exe or g++.exe)."
}

& $parityExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $sortExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $pathSizeExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $driveEnumExePath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $usnReaderExePath
exit $LASTEXITCODE
