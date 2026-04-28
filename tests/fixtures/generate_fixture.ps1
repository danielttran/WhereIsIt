param(
    [Parameter(Mandatory = $false)] [string] $Root = "./artifacts/fixture"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Path $Root -Force | Out-Null

$dirs = @(
    (Join-Path $Root "docs"),
    (Join-Path $Root "media\photos"),
    (Join-Path $Root "media\videos"),
    (Join-Path $Root "logs"),
    (Join-Path $Root "projects\alpha")
)

foreach ($d in $dirs) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

Set-Content -Path (Join-Path $Root "docs\invoice-2026.txt") -Value "invoice record"
Set-Content -Path (Join-Path $Root "docs\report final.txt") -Value "report"
Set-Content -Path (Join-Path $Root "logs\app.log") -Value "log-line"
Set-Content -Path (Join-Path $Root "projects\alpha\readme.md") -Value "project"

# deterministic binary-ish payloads
$bytes = New-Object byte[] 4096
(new-object System.Random 42).NextBytes($bytes)
[System.IO.File]::WriteAllBytes((Join-Path $Root "media\photos\image01.jpg"), $bytes)
[System.IO.File]::WriteAllBytes((Join-Path $Root "media\videos\clip01.mp4"), $bytes)

Write-Host "Fixture created at $Root"
