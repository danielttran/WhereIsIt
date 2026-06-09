# Manual audit harness for the six user requirements. Each block prints
# PASS / FAIL with a short reason. Run with `powershell -File audit.ps1`.
# Each block is independent — failures don't stop later blocks.
$ErrorActionPreference = 'Continue'

$exe   = 'E:\Dev\WhereIsIt\app\WhereIsIt.App\bin\Debug\net10.0-windows10.0.19041.0\WhereIsIt.App.exe'
$tmp   = $env:TEMP
$pass  = 0
$fail  = 0
function Report($name, $ok, $note) {
    if ($ok) { Write-Host ("[PASS] {0}  {1}" -f $name, $note); $script:pass++ }
    else     { Write-Host ("[FAIL] {0}  {1}" -f $name, $note); $script:fail++ }
}

# Kill any stray instance from a prior aborted run so the single-instance
# tests get a clean slate.
Get-Process WhereIsIt.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ─── 1. Headless *.md returns real .md files, no crash, no FileNotFoundException ──
$out = Join-Path $tmp 'audit-md.txt'
Remove-Item $out -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList @('--headless','--query','*.md','--max-results','25','--timeout','60','--output',$out) -PassThru
$p.WaitForExit(90000) | Out-Null
$ok = $p.HasExited -and $p.ExitCode -eq 0 -and (Test-Path $out)
if ($ok) {
    $lines = Get-Content $out
    $mdLines = $lines | Where-Object { $_ -match '\.md$' }
    $hasError = $lines | Where-Object { $_ -match 'FileNotFoundException|exception' }
    $ok = ($mdLines.Count -ge 1) -and (-not $hasError)
    Report 'wildcard *.md' $ok ("exit=$($p.ExitCode), mdLines=$($mdLines.Count), errLines=$($hasError.Count)")
} else {
    Report 'wildcard *.md' $false 'process did not exit cleanly or no output file'
}

# ─── 2. Headless ext:cs returns real .cs files ─────────────────────────────────
$out = Join-Path $tmp 'audit-cs.txt'
Remove-Item $out -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList @('--headless','--query','ext:cs','--max-results','25','--timeout','60','--output',$out) -PassThru
$p.WaitForExit(90000) | Out-Null
$ok = $p.HasExited -and $p.ExitCode -eq 0 -and (Test-Path $out)
if ($ok) {
    $lines = Get-Content $out
    $csLines = $lines | Where-Object { $_ -match '\.cs$' }
    $ok = $csLines.Count -ge 1
    Report 'ext:cs' $ok ("csLines=$($csLines.Count)")
} else {
    Report 'ext:cs' $false 'process did not exit cleanly'
}

# ─── 3. Single-instance: launch primary with HTTP server enabled, then forward ──
#       and assert the PRIMARY actually received the forward (sentinel file).
# Path.GetTempPath() in the WinUI process can resolve to C:\Windows\Temp under
# some session contexts even though the user's $env:TEMP is %LocalAppData%\Temp,
# so check both.
$sentinelCandidates = @(
    (Join-Path $tmp 'whereisit-last-forward.txt'),
    'C:\Windows\Temp\whereisit-last-forward.txt'
)
$sentinelCandidates | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }
# Single attempt — no retry. A racy STATUS_STOWED_EXCEPTION is a real bug,
# so the audit must surface it instead of masking it with relaunches.
$p1 = Start-Process -FilePath $exe -ArgumentList @('--enable-http') -PassThru
Start-Sleep -Seconds 15
if (-not (Get-Process -Id $p1.Id -ErrorAction SilentlyContinue)) {
    Report 'single-instance forward' $false 'primary died during indexing — racy XAML stowed exception'
} else {
    $marker = 'audit-fwd-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $p2 = Start-Process -FilePath $exe -ArgumentList @('--query', $marker) -PassThru
    $exited = $p2.WaitForExit(8000)
    if (-not $exited) { Stop-Process -Id $p2.Id -Force }
    Start-Sleep -Milliseconds 600
    $sentinelOk = $false
    foreach ($s in $sentinelCandidates) {
        if ((Test-Path $s) -and ((Get-Content $s) -join "`n") -match "query=$marker") {
            $sentinelOk = $true; break
        }
    }
    Report 'single-instance forward' ($sentinelOk -and $exited) ("sentinel matches '$marker': $sentinelOk")
}

# ─── 4. Close-to-tray: taskkill (no /F) sends WM_CLOSE; process must stay alive ─
if (Get-Process -Id $p1.Id -ErrorAction SilentlyContinue) {
    & taskkill /pid $p1.Id 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    $stillAlive = (Get-Process -Id $p1.Id -ErrorAction SilentlyContinue) -ne $null
    Report 'close-to-tray' $stillAlive ("process alive 3s after WM_CLOSE: $stillAlive")
}

# ─── 5. USN live update: create a unique file with the engine ALREADY RUNNING. ──
#       Use the HTTP server (which we enabled in step 3) to query the LIVE engine.
if (Get-Process -Id $p1.Id -ErrorAction SilentlyContinue) {
    $unique = 'audit-live-' + [Guid]::NewGuid().ToString('N').Substring(0,12) + '.txt'
    $probe  = Join-Path 'E:\Dev\WhereIsIt' $unique
    'live-usn probe' | Out-File -Encoding utf8 $probe
    # Engine has a 16 ms WatchLoop wait + MonitorChanges thread that polls the
    # USN journal — give it a generous window so this isn't flaky.
    Start-Sleep -Seconds 6
    try {
        $resp = Invoke-WebRequest -Uri ("http://127.0.0.1:12321/search?q=" + [uri]::EscapeDataString($unique)) -UseBasicParsing -TimeoutSec 20
        $found = $resp.StatusCode -eq 200 -and $resp.Content -match $unique
    } catch {
        $found = $false
    }
    Report 'usn live-update (running engine)' $found "probe=$unique found=$found"
    Remove-Item $probe -ErrorAction SilentlyContinue
} else {
    Report 'usn live-update (running engine)' $false 'primary instance died before USN check'
}

# ─── 6. Compact rows: assert the XAML template carries the tight setters. ──────
#       This is a SOURCE check, not a rendered-pixel measurement — for a true
#       visual diff the user has to eyeball it. UIAutomation row-height
#       inspection is fiddly with WinUI 3 (its AutomationPeers don't expose
#       BoundingRectangle for virtualized templates) so we read the XAML on
#       disk and verify the compactness setters are present.
$xaml = Get-Content 'E:\Dev\WhereIsIt\app\WhereIsIt.App\MainWindow.xaml' -Raw
$minHeightOk = $xaml -match 'MinHeight" Value="0"'
$fontSizeOk  = $xaml -match 'FontSize" Value="13"'
$padOk       = $xaml -match 'Padding="6,1"'
Report 'compact-row XAML setters' ($minHeightOk -and $fontSizeOk -and $padOk) `
    ("MinHeight=0:$minHeightOk FontSize=13:$fontSizeOk Padding=6,1:$padOk")

# ─── Cleanup ───────────────────────────────────────────────────────────────────
Get-Process WhereIsIt.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("=== AUDIT TOTAL: PASS={0} FAIL={1} ===" -f $pass, $fail)
exit $fail
