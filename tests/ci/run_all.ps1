$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
dotnet test "$root\tests\app\WhereIsIt.App.Tests.csproj" --logger "console;verbosity=normal"
