# Starts the published service (console mode, debug API on) and exercises it over HTTP.
# Usage: scripts/smoke-test.ps1 -Root artifacts/publish
param([string]$Root = "artifacts/publish")

$ErrorActionPreference = "Stop"
$data = Join-Path $PWD "smoke-data"
New-Item -ItemType Directory -Force $data | Out-Null
$env:RELAUNCH_DATA = $data
$env:RELAUNCH_DEBUG_API = "1"

$svc = Start-Process "$Root/service/ReLaunch.Service.exe" -PassThru `
    -RedirectStandardOutput "$data/service.out.log" -RedirectStandardError "$data/service.err.log"
$base = "http://127.0.0.1:5480"
$failed = $false
try {
    $apps = @()
    for ($i = 0; $i -lt 60 -and $apps.Count -eq 0; $i++) {
        Start-Sleep -Seconds 2
        try { $apps = @((Invoke-WebRequest "$base/api/apps" -UseBasicParsing).Content | ConvertFrom-Json) } catch { }
    }
    Write-Host "Status: $(Invoke-RestMethod "$base/api/status" | ConvertTo-Json -Compress)"
    Write-Host "Catalogue entries: $($apps.Count)"
    $withIcons = 0
    for ($i = 0; $i -lt 30 -and $withIcons -eq 0; $i++) {
        $apps = @((Invoke-WebRequest "$base/api/apps" -UseBasicParsing).Content | ConvertFrom-Json)
        $withIcons = @($apps | Where-Object { $_.iconHash }).Count
        if ($withIcons -eq 0) { Start-Sleep -Seconds 2 }
    }
    Write-Host "Entries with icons: $withIcons"
    if ($withIcons -eq 0) { throw "no icons were extracted" }
    $apps | Select-Object -First 15 name, trackable, iconHash | Format-Table | Out-String | Write-Host
    if ($apps.Count -eq 0) { throw "catalogue is empty" }

    $target = $apps | Where-Object { $_.trackable -and $_.name -match "Notepad|Calculator|Paint" } | Select-Object -First 1
    if (-not $target) { $target = $apps | Where-Object { $_.trackable } | Select-Object -First 1 }
    if (-not $target) { throw "no trackable app to test with" }
    if ($target) {
        Write-Host "Trying $($target.name) ($($target.id))"
        $r = Invoke-RestMethod -Method Post "$base/api/apps/$($target.id)/start" -SkipHttpErrorCheck
        Write-Host "start -> $($r | ConvertTo-Json -Compress)"
        $r = Invoke-RestMethod -Method Post "$base/api/apps/$($target.id)/restart" -SkipHttpErrorCheck
        Write-Host "restart -> $($r | ConvertTo-Json -Compress)"
        $r = Invoke-RestMethod -Method Post "$base/api/apps/$($target.id)/stop" -SkipHttpErrorCheck
        Write-Host "stop -> $($r | ConvertTo-Json -Compress)"
    }
    Write-Host "Activity: $(Invoke-RestMethod "$base/api/activity" | ConvertTo-Json -Compress)"

    # The desktop app opens every page against the running service (catches XAML and startup crashes).
    $appExe = "$Root/app/ReLaunch.App.exe"
    $selftestLog = "$Root/app/selftest.log"
    Remove-Item $selftestLog -ErrorAction SilentlyContinue
    $appProc = Start-Process $appExe -ArgumentList "--selftest" -PassThru
    if (-not $appProc.WaitForExit(120000)) { Stop-Process -Id $appProc.Id -Force; throw "desktop app self-test timed out" }
    if (Test-Path $selftestLog) { Get-Content $selftestLog | ForEach-Object { Write-Host "app: $_" } } else { Write-Host "app: no self-test log was written" }
    if ($appProc.ExitCode -ne 0) { throw "desktop app self-test failed (exit code $($appProc.ExitCode))" }
}
catch {
    Write-Host "SMOKE TEST FAILED: $_"
    $failed = $true
}
finally {
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    Get-Process ReLaunch.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($f in "service.out.log", "service.err.log") {
        Write-Host "---- $f"
        if (Test-Path "$data/$f") { Get-Content "$data/$f" -Tail 60 }
    }
}
if ($failed) { exit 1 }
