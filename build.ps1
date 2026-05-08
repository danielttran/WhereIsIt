$ErrorActionPreference = 'Stop'
Write-Host 'Building solution...'
msbuild .\WhereIsIt.sln /m /p:Configuration=Debug
