# Installs the MSI silently, checks the real Windows service (LocalSystem) starts the agent in the user session
# and the desktop app can talk to it, then uninstalls.
param([Parameter(Mandatory)][string]$Msi)

$ErrorActionPreference = "Stop"
$msi = (Resolve-Path $Msi).Path
$log = Join-Path $PWD "installer-test.log"
$installDir = Join-Path $env:ProgramFiles "reLAUNCH"

function Fail($msg) { Write-Host "INSTALLER TEST FAILED: $msg"; if (Test-Path $log) { Get-Content $log -Tail 60 }; exit 1 }

Write-Host "Installing $msi"
$p = Start-Process msiexec.exe -ArgumentList "/i", "`"$msi`"", "/qn", "/l*v", "`"$log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "msiexec /i exited with $($p.ExitCode)" }

$svc = Get-Service reLAUNCH -ErrorAction SilentlyContinue
if (-not $svc) { Fail "service reLAUNCH was not installed" }
if ($svc.Status -ne "Running") { Fail "service is $($svc.Status), expected Running" }
Write-Host "Service is running (start type $((Get-CimInstance Win32_Service -Filter "Name='reLAUNCH'").StartMode))."

$failureActions = (sc.exe qfailure reLAUNCH) -join "`n"
if ($failureActions -notmatch "RESTART") { Fail "service has no restart-on-failure actions:`n$failureActions" }
Write-Host "Recovery: restart on failure is configured."

$agent = $null
for ($i = 0; $i -lt 40 -and -not $agent; $i++) {
    Start-Sleep -Seconds 1
    $agent = Get-Process ReLaunch.Agent -ErrorAction SilentlyContinue
}
if (-not $agent) { Fail "the service did not start ReLaunch.Agent in the user session" }
$session = (Get-CimInstance Win32_Process -Filter "ProcessId=$($agent[0].Id)").SessionId
Write-Host "Agent is running, session $session (service session: 0)."
if ($session -eq 0) { Fail "agent runs in session 0 instead of the user session" }

Write-Host "Running the desktop app self-test against the installed service"
$appLog = Join-Path $installDir "selftest.log"
Remove-Item $appLog -ErrorAction SilentlyContinue
$app = Start-Process (Join-Path $installDir "ReLaunch.App.exe") -ArgumentList "--selftest" -PassThru
if (-not $app.WaitForExit(120000)) { Stop-Process -Id $app.Id -Force; Fail "app self-test timed out" }
if (Test-Path $appLog) { Get-Content $appLog | ForEach-Object { Write-Host "app: $_" } }
if ($app.ExitCode -ne 0) { Fail "app self-test exit code $($app.ExitCode)" }

$shortcut = Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu" -Recurse -Filter "reLAUNCH*.lnk" -ErrorAction SilentlyContinue
if (-not $shortcut) { Fail "Start menu shortcut is missing" }
Write-Host "Start menu shortcut: $($shortcut[0].FullName)"

Write-Host "Uninstalling"
$u = Start-Process msiexec.exe -ArgumentList "/x", "`"$msi`"", "/qn", "/l*v", "`"$log.uninstall`"" -Wait -PassThru
if ($u.ExitCode -ne 0) { Fail "msiexec /x exited with $($u.ExitCode)" }
if (Get-Service reLAUNCH -ErrorAction SilentlyContinue) { Fail "service still registered after uninstall" }
if (Test-Path (Join-Path $installDir "ReLaunch.Service.exe")) { Fail "files left behind after uninstall" }
Write-Host "INSTALLER TEST PASSED"
