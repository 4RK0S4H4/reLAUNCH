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
        try { $apps = @(Invoke-RestMethod "$base/api/apps") } catch { }
    }
    Write-Host "Status: $(Invoke-RestMethod "$base/api/status" | ConvertTo-Json -Compress)"
    Write-Host "Catalogue entries: $($apps.Count)"
    $apps | Select-Object -First 15 name, trackable, iconHash | Format-Table | Out-String | Write-Host
    if ($apps.Count -eq 0) { throw "catalogue is empty" }

    $target = $apps | Where-Object { $_.trackable -and $_.name -match "Notepad|Calculator|Paint" } | Select-Object -First 1
    if (-not $target) { $target = $apps | Where-Object { $_.trackable } | Select-Object -First 1 }
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
