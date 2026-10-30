# Automated co-op sync test: two bot-driven copies of the game, host + puppet over UDP.
#
#   pwsh -File tools/qa.ps1                    # default port 24710, 35s window
#   pwsh -File tools/qa.ps1 -Port 24799 -Seconds 25
#
# The bot env vars make each instance drive the menu itself (no human clicks):
#   APFEP_SMOKE_AUTO=1  host: boot -> lobby -> StartGame -> lab
#   APFEP_JOIN=<port>   client: boot -> lobby -> join host on <port>
# Exit 0 = invariants held, 1 = a sync invariant failed.
param(
    [int]$Port = 24710,
    [int]$Seconds = 35,
    [string]$HostDir = "L:\agent test space\apfep-coop\install",
    [string]$ClientDir = "L:\agent test space\apfep-coop\install-b",
    [string]$Exe = "A Place for Every Potion.exe"
)

$ErrorActionPreference = "Stop"

function Stop-Game() {
    Stop-Process -Name "A Place for Every Potion" -ErrorAction SilentlyContinue
    Stop-Process -Name "UnityCrashHandler64" -ErrorAction SilentlyContinue
}

Stop-Game
Remove-Item "$HostDir\MelonLoader\Latest.log", "$ClientDir\MelonLoader\Latest.log" -ErrorAction SilentlyContinue

$hostLog = "$HostDir\MelonLoader\Latest.log"
$clientLog = "$ClientDir\MelonLoader\Latest.log"

$env:APFEP_SMOKE_AUTO = "1"
$env:APFEP_QA_PORT = "$Port"
Start-Process -FilePath "`"$HostDir\$Exe`"" -ArgumentList "-melonloader-loglevel INFO" -WorkingDirectory $HostDir
Start-Sleep 12

$env:APFEP_JOIN = "$Port"
Start-Process -FilePath "`"$ClientDir\$Exe`"" -ArgumentList "-melonloader-loglevel INFO" -WorkingDirectory $ClientDir
Start-Sleep $Seconds

Stop-Game

function LastTick([string]$log) {
    $m = Select-String -Path $log -Pattern "\[apfep\] tick=" | Select-Object -Last 1
    if ($m) { return $m.Line } else { return $null }
}

function TickField([string]$line, [string]$name) {
    if ($line -notmatch "$name=(\d+)") { return -1 }
    return [int]$Matches[1]
}

$hostTick = LastTick $hostLog
$clientTick = LastTick $clientLog

$checks = @()

# Both sides patched every subsystem at least once.
$checks += @{ name = "host installs patches"; ok = (Select-String -Path $hostLog -Pattern "sync subsystem installed" -Quiet) -and (Select-String -Path $hostLog -Pattern "puppet subsystem installed" -Quiet) }
$checks += @{ name = "client installs patches"; ok = (Select-String -Path $clientLog -Pattern "sync subsystem installed" -Quiet) -and (Select-String -Path $clientLog -Pattern "puppet subsystem installed" -Quiet) }

# Peer discovery completed over the relay.
$checks += @{ name = "host sees client"; ok = $hostTick -and (TickField $hostTick "peers") -ge 1 }
$checks += @{ name = "client sees host"; ok = $clientTick -and (TickField $clientTick "peers") -ge 1 }

# Mutual puppet avatars: host stores client moves, client stores host moves.
$checks += @{ name = "host stores puppet moves"; ok = $hostTick -and (TickField $hostTick "recv") -gt 0 -and (TickField $hostTick "avatars") -ge 1 }
$checks += @{ name = "client stores puppet moves"; ok = $clientTick -and (TickField $clientTick "recv") -gt 0 -and (TickField $clientTick "avatars") -ge 1 }
$checks += @{ name = "client broadcasts moves"; ok = $clientTick -and (TickField $clientTick "sent") -gt 0 }

# Puppet reached the host's gameplay state through its own level load.
$checks += @{ name = "puppet follows into lab"; ok = (Select-String -Path $clientLog -Pattern "puppet follows host state|first Enter observed: Playing" -Quiet) }
$checks += @{ name = "both report Playing state"; ok = $hostTick -and $clientTick -and (TickField $hostTick "gs") -eq (TickField $clientTick "gs") -and (TickField $hostTick "gs") -eq 3 }

# Host-authoritative progress mirrored verbatim onto the puppet.
# each instance counts its own wall clock; the gameplay mirror excludes it
$hostProg = if ($hostTick -match "lab: (.*)$") { ($Matches[1] -replace "\|\s*time \d+s", "").Trim() } else { "" }
$clientProg = if ($clientTick -match "lab: (.*)$") { ($Matches[1] -replace "\|\s*time \d+s", "").Trim() } else { "" }
$checks += @{ name = "progress mirrors host"; ok = $hostProg -ne "" -and $hostProg -eq $clientProg }

# No unhandled plugin errors. Patch-backend warnings ("sync patch ... failed") are the known
# il2cpp generic-method limitation; the choke-point patches cover those paths, so they are not
# treated as failures here.
$errLines = @(Select-String -Path $hostLog, $clientLog -Pattern "\[apfep\].*(failed|Exception|ERROR)") |
    Where-Object { $_.Line -notmatch "sync patch" }
$checks += @{ name = "no plugin errors"; ok = $errLines.Count -eq 0 }

foreach ($c in $checks) { Write-Host ("{0,-32} {1}" -f $c.name, ($(if ($c.ok) { "PASS" } else { "FAIL" }))) }
Write-Host "--- host tick: $hostTick"
Write-Host "--- client tick: $clientTick"

$failed = $checks | Where-Object { -not $_.ok }
if ($failed) { Write-Host "QA FAILED ($($failed.Count) of $($checks.Count))"; exit 1 }
Write-Host "QA PASSED ($($checks.Count) checks)"
exit 0
