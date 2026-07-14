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
Get-Process Tracker.Supervisor, Tracker.Daemon, Tracker.Watcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

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
Write-Host " - variabila de mediu DOTNET_ROOT: partajata cu alte aplicatii .NET, NU o atingem"
Write-Host "Dezinstalare completa."
