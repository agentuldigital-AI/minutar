# uninstall.ps1 - opreste si dezinstaleaza stack-ul Minutar.
# DATELE (events.db + config + backups, %LOCALAPPDATA%\TimeTracker) se PASTREAZA implicit;
# ruleaza cu -RemoveData ca sa le stergi si pe ele.
#
# Ruleaza dintr-un PowerShell ELEVAT:
#   powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1 [-RemoveData]

param([switch]$RemoveData)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (task unregistration needs admin)."
}

# 1. scheduled tasks (curent + interimarele M1, daca mai exista)
foreach ($t in @("TimeTracker-Supervisor", "TimeTracker-AwServer", "TimeTracker-Watcher", "TimeTracker-Daemon")) {
    $task = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    if ($task) {
        $task | Stop-ScheduledTask -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $t -Confirm:$false
        Write-Host "Task scos: $t"
    }
}

# 2. procese
Get-Process Tracker.Supervisor, Tracker.Daemon, Tracker.Watcher, Tracker.Launcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# 2b. shortcut-uri (Start Menu + Desktop)
foreach ($lnkDir in @(
        (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"),
        [Environment]::GetFolderPath("Desktop"))) {
    $lnk = Join-Path $lnkDir "Minutar.lnk"
    if (Test-Path $lnk) {
        Remove-Item -Force $lnk
        Write-Host "Shortcut sters: $lnk"
    }
}

# 2c. DOTNET_ROOT lasat in urma de instalarile vechi: era setat persistent si rupea ALTE
# aplicatii .NET ale userului (nu doar ale noastre). Se sterge doar cand arata spre un folder
# privat de dotnet — o valoare pusa deliberat de user pentru altceva ramane pe loc.
foreach ($scope in @("User", "Machine")) {
    $val = [Environment]::GetEnvironmentVariable("DOTNET_ROOT", $scope)
    if (-not $val) { continue }
    if ($val -like "*\AppData\Local\Programs\dotnet*" -or $val -like "*\AppData\Local\Microsoft\dotnet*") {
        [Environment]::SetEnvironmentVariable("DOTNET_ROOT", $null, $scope)
        Write-Host "Sters: variabila $scope DOTNET_ROOT ($val)"
    } else {
        Write-Host "PASTRAT: $scope DOTNET_ROOT = $val (nu e a noastra)"
    }
}

# 3. binare + log-uri + stare UI/coach (%LOCALAPPDATA%\time-tracker)
$bin = Join-Path $env:LOCALAPPDATA "time-tracker"
if (Test-Path $bin) {
    Remove-Item -Recurse -Force $bin
    Write-Host "Sters: $bin (binare, log-uri, stare UI)"
}

# 4. datele (%LOCALAPPDATA%\TimeTracker: events.db, tracker.toml, backups) - doar la cerere
$data = Join-Path $env:LOCALAPPDATA "TimeTracker"
if ($RemoveData) {
    if (Test-Path $data) {
        Remove-Item -Recurse -Force $data
        Write-Host "Sters: $data (events.db + config + backups)"
    }
} elseif (Test-Path $data) {
    Write-Host "PASTRAT: $data (events.db + config + backups). Sterge cu: uninstall.ps1 -RemoveData"
}

Write-Host ""
Write-Host "Ramase MANUAL (daca e cazul):"
Write-Host " - extensia de browser: chrome://extensions / edge://extensions -> Remove"
Write-Host " - hook-urile Claude Code: ~/.claude/settings.json (restaureaza din .bak-ul creat de installer)"
Write-Host " - .NET Desktop Runtime: partajat cu alte aplicatii, NU il atingem"
Write-Host "Dezinstalare completa."
