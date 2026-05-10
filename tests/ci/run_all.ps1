$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Write-Host "Building test project..."
dotnet build "$root\tests\app\WhereIsIt.App.Tests.csproj" --no-restore -v quiet

Write-Host "Running tests..."
dotnet test "$root\tests\app\WhereIsIt.App.Tests.csproj" --no-restore --no-build --logger "console;verbosity=normal"
