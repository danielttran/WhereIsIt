$ErrorActionPreference = 'Stop'
msbuild .\WhereIsIt.sln /m /p:Configuration=Debug
ctest --output-on-failure
Write-Host 'dotnet not available in this environment; dotnet test should run in CI.'
