# End to end: local relay Worker (wrangler dev) <-> published service + agent <-> a fake phone (HTTP calls).
# Usage: scripts/relay-e2e.ps1 -Root artifacts/publish
param([string]$Root = "artifacts/publish")

$ErrorActionPreference = "Stop"
$env:CI = "1"
$env:WRANGLER_SEND_METRICS = "false"
$data = Join-Path $PWD "e2e-data"
New-Item -ItemType Directory -Force $data | Out-Null
$relay = "http://127.0.0.1:8787"
$debug = "http://127.0.0.1:5480"
$pcKey = "e2e-test-key-" + [guid]::NewGuid().ToString("N")

function Json($url, $method = "GET", $headers = @{}, $body = $null) {
    $req = @{ Uri = $url; Method = $method; Headers = $headers; UseBasicParsing = $true; SkipHttpErrorCheck = $true }
    if ($null -ne $body) { $req.Body = ($body | ConvertTo-Json -Compress); $req.ContentType = "application/json" }
    $r = Invoke-WebRequest @req
    $parsed = $null
    if ($r.Content) { try { $parsed = $r.Content | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $parsed; Raw = $r.Content }
}

function WaitFor($what, [scriptblock]$cond, $seconds = 60) {
    for ($i = 0; $i -lt $seconds; $i++) {
        try { if (& $cond) { Write-Host "ok: $what"; return } } catch { }
        Start-Sleep -Seconds 1
    }
    throw "timed out waiting for: $what"
}

Push-Location relay
npm ci
$wrangler = Start-Process "npx.cmd" -ArgumentList "wrangler", "dev", "--ip", "127.0.0.1", "--port", "8787", "--var", "PC_KEY:$pcKey" `
    -PassThru -RedirectStandardOutput "$data/wrangler.out.log" -RedirectStandardError "$data/wrangler.err.log"
Pop-Location

$env:RELAUNCH_DATA = $data
$env:RELAUNCH_DEBUG_API = "1"
$env:RELAUNCH_RELAY_URL = $relay
$env:RELAUNCH_PC_KEY = $pcKey
$svc = $null
$failed = $false
try {
    WaitFor "relay is up" { (Json "$relay/api/health").Status -eq 200 } 120

    $svc = Start-Process "$Root/ReLaunch.Service.exe" -PassThru `
        -RedirectStandardOutput "$data/service.out.log" -RedirectStandardError "$data/service.err.log"
    WaitFor "service API is up" { (Json "$debug/api/status").Status -eq 200 } 60
    WaitFor "service connected to the relay" { (Json "$debug/api/relay").Body.state -eq "connected" } 60

    # Pairing: the PC creates a code, the "phone" exchanges it.
    $pair = Json "$debug/api/pairing" "POST"
    if (-not $pair.Body.code) { throw "no pairing code: $($pair.Raw)" }
    Write-Host "pairing url: $($pair.Body.url)"
    $bad = Json "$relay/api/pair" "POST" @{} @{ code = "WRONGCODE1"; name = "x" }
    if ($bad.Status -ne 403) { throw "wrong code should be 403, got $($bad.Status)" }
    $paired = Json "$relay/api/pair" "POST" @{} @{ code = $pair.Body.code; name = "CI phone" }
    if ($paired.Status -ne 200) { throw "pairing failed: $($paired.Raw)" }
    $auth = @{ Authorization = "Bearer $($paired.Body.token)" }
    $again = Json "$relay/api/pair" "POST" @{} @{ code = $pair.Body.code; name = "x" }
    if ($again.Status -ne 403) { throw "code reuse should be 403, got $($again.Status)" }

    # Catalogue, status and icons arrive through the relay.
    WaitFor "relay has the catalogue" { (Json "$relay/api/apps" "GET" $auth).Body.apps.Count -gt 20 } 90
    $status = Json "$relay/api/status" "GET" $auth
    Write-Host "relay status: $($status.Raw)"
    if ($status.Body.pc -ne "online") { throw "pc should be online" }

    $apps = (Json "$relay/api/apps" "GET" $auth).Body.apps
    $target = $apps | Where-Object { $_.trackable -and $_.name -eq "Calculator" } | Select-Object -First 1
    if (-not $target) { $target = $apps | Where-Object { $_.trackable -and $_.name -match "Notepad|Paint" } | Select-Object -First 1 }
    if (-not $target) { throw "no suitable app to start" }
    Write-Host "target: $($target.name) ($($target.id))"

    WaitFor "icon is served by the relay" {
        $t = (Json "$relay/api/apps" "GET" $auth).Body.apps | Where-Object { $_.id -eq $target.id }
        if (-not $t.iconHash) { return $false }
        $r = Invoke-WebRequest "$relay/api/apps/$($target.id)/icon" -Headers $auth -UseBasicParsing -SkipHttpErrorCheck
        $r.StatusCode -eq 200 -and $r.Headers["Content-Type"] -match "image/png" -and $r.Content.Length -gt 100
    } 90

    # Actions go phone -> relay -> PC -> agent.
    foreach ($action in "start", "restart", "stop") {
        $res = Json "$relay/api/apps/$($target.id)/$action" "POST" $auth
        Write-Host "$action -> $($res.Status) $($res.Raw)"
        if ($res.Status -ne 200 -or -not $res.Body.ok) { throw "$action failed" }
    }
    $activity = (Json "$relay/api/activity?limit=10" "GET" $auth).Body.activity
    if ($activity.Count -lt 3 -or $activity[0].deviceName -ne "CI phone") { throw "activity log looks wrong" }

    # Favorites round trip.
    $fav = Json "$relay/api/favorites" "PUT" $auth @{ ids = @($target.id) }
    if ($fav.Status -ne 200) { throw "favorites failed: $($fav.Raw)" }
    WaitFor "favorite shows up" { (Json "$relay/api/apps?favorites=true" "GET" $auth).Body.apps.Count -eq 1 } 30

    # Device list and revocation from the PC side.
    $devices = Json "$debug/api/devices"
    if (-not ($devices.Body | Where-Object { $_.id -eq $paired.Body.deviceId })) { throw "device missing from PC list" }
    $null = Json "$debug/api/devices/$($paired.Body.deviceId)" "DELETE"
    if ((Json "$relay/api/status" "GET" $auth).Status -ne 401) { throw "revoked token should be rejected" }

    # Stopping the service takes the PC offline.
    Stop-Process -Id $svc.Id -Force
    $svc = $null
    $pairAgain = $null
    Write-Host "E2E PASSED"
}
catch {
    Write-Host "E2E FAILED: $_"
    $failed = $true
}
finally {
    if ($svc) { Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process ReLaunch.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
    taskkill /T /F /PID $wrangler.Id 2>$null | Out-Null
    foreach ($f in "wrangler.out.log", "wrangler.err.log", "service.out.log", "service.err.log") {
        Write-Host "---- $f"
        if (Test-Path "$data/$f") { Get-Content "$data/$f" -Tail 40 }
    }
}
exit $(if ($failed) { 1 } else { 0 })
