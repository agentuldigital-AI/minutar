# install.ps1 - one-time machine setup (M0 version; refined at M1/M6).
# Publishes the .NET components and registers Task Scheduler autostart entries
# with "run with highest privileges" (locked decision #3 - HKCU Run can't launch elevated).
#
# Run manually from an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1
#
# What it does:
#   1. Verifies the .NET SDK is present.
#   2. dotnet publish Watcher + Daemon to %LOCALAPPDATA%\time-tracker\bin.
#   3. Registers scheduled tasks (at logon, highest privileges, restart on failure):
#        TimeTracker-AwServer, TimeTracker-Watcher, TimeTracker-Daemon
#      (the Supervisor takes over process management at M6; tasks are the M1 interim watchdog.)

param([switch]$SkipTasks)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
# guard: an install run from an ephemeral Claude worktree would bind the task's --config
# to a path that disappears with the worktree (taking the live config edits with it)
if ($repo -like "*\.claude\worktrees\*") {
    throw "install.ps1 was started from a git worktree ($repo). Run it from the main repo checkout."
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Task Scheduler 'highest privileges' registration needs admin)."
}

# --- 1. Prerequisites -------------------------------------------------------
# NEVER set a persistent DOTNET_ROOT here. It is a GLOBAL switch that tells every .NET
# apphost of this user where to look for a runtime — pointing it at a private user-scope
# folder (which only carries one version) breaks unrelated .NET apps with "You must install
# or update .NET to run this application". Our exes need nothing: apphosts fall back to the
# machine-wide install, verified below. The supervisor still passes DOTNET_ROOT to its CHILD
# processes only, in their own environment block (ManagedProcess.cs) — scoped, never persisted.
Write-Host "Running as: $env:USERNAME | LOCALAPPDATA: $env:LOCALAPPDATA"

# clean up after older installs that did persist it (both hives, and only when it is a
# private path — a DOTNET_ROOT the user set deliberately elsewhere is left alone)
foreach ($scope in @("User", "Machine")) {
    $stale = [Environment]::GetEnvironmentVariable("DOTNET_ROOT", $scope)
    if (-not $stale) { continue }
    if ($stale -like "*\AppData\Local\Programs\dotnet*" -or $stale -like "*\AppData\Local\Microsoft\dotnet*") {
        [Environment]::SetEnvironmentVariable("DOTNET_ROOT", $null, $scope)
        Write-Host "Removed stale $scope DOTNET_ROOT ($stale) - it broke other .NET apps"
    } else {
        Write-Host "NOTE: $scope DOTNET_ROOT = $stale (not ours, left untouched)"
    }
}

# the STACK needs a machine-wide runtime, because the scheduled task launches an apphost
# with no environment of ours. Checked explicitly so a missing runtime fails here, loudly,
# instead of as a silent 0x80008096 at logon. (Setup.exe installs it; this dev path does not.)
$sharedRoot = "$env:ProgramFiles\dotnet\shared"
foreach ($fx in @("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App")) {
    if (-not (Test-Path (Join-Path $sharedRoot "$fx\10.*"))) {
        throw "$fx 10.x missing from $sharedRoot. Install the .NET 10 Desktop Runtime (x64) " +
              "from https://dotnet.microsoft.com/download/dotnet/10.0 and re-run this script."
    }
}
Write-Host "Machine-wide .NET 10 runtimes: OK ($sharedRoot)"

# The SDK is only needed to PUBLISH. Prefer the user-scope one: "C:\Program Files\dotnet"
# may exist with NO SDK (runtime-only). dotnet.exe locates its own runtime next to itself.
# candidates: user-scope SDK locations, then PATH
$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\dotnet\dotnet.exe"),
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
)
$dotnet = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) { throw ".NET SDK not found - see README.md" }
    $dotnet = $cmd.Source
}
$sdks = & $dotnet --list-sdks 2>$null
if (-not $sdks) { throw "dotnet at $dotnet has NO SDKs installed - see README.md" }
Write-Host "dotnet: $dotnet (SDK $(($sdks | Select-Object -First 1) -split ' ')[0])"

# --- config: sursa de adevăr trăiește în %LOCALAPPDATA%\TimeTracker (2026-07-14) -----
# decuplat de repo: git-ul nu mai calcă peste editările live, iar utilizatorii publici
# n-au repo. Prima instalare copiază template-ul din repo; upgrade-urile NU îl ating.
$dataDir = Join-Path $env:LOCALAPPDATA "TimeTracker"
$configPath = Join-Path $dataDir "tracker.toml"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
if (-not (Test-Path $configPath)) {
    Copy-Item (Join-Path $repo "config\tracker.toml") $configPath
    Write-Host "Config seeded: config\tracker.toml -> $configPath"
} else {
    Write-Host "Config: $configPath (existent - editarile live se pastreaza)"
}

# --- 2. Publish (STAGED: live bin ramane intact daca publish-ul pica) --------
# intai se publica TOT in bin-stage (fara sa oprim nimic); abia dupa succes complet
# oprim stack-ul si facem swap — un publish esuat nu mai lasa un bin mixt care crapa.
$binDir = Join-Path $env:LOCALAPPDATA "time-tracker\bin"
$stageDir = Join-Path $env:LOCALAPPDATA "time-tracker\bin-stage"
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
foreach ($proj in @("Tracker.Watcher", "Tracker.Daemon", "Tracker.Supervisor", "Tracker.Launcher")) {
    Write-Host "Publishing $proj (stage) ..."
    & $dotnet publish "$repo\src\$proj" -c Release -o (Join-Path $stageDir $proj) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj (live bin untouched)" }
}

# stop everything ONLY now: the scheduled task must be stopped AND disabled before
# killing processes — its RestartCount=999/1min watchdog would otherwise relaunch the
# supervisor mid-swap and re-lock the files.
$task = Get-ScheduledTask -TaskName "TimeTracker-Supervisor" -ErrorAction SilentlyContinue
if ($task) {
    $task | Stop-ScheduledTask -ErrorAction SilentlyContinue
    $task | Disable-ScheduledTask | Out-Null
}
Get-Process Tracker.Supervisor, Tracker.Daemon, Tracker.Watcher, Tracker.Launcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# swap: mirror stage -> live per componenta (robocopy: exit code <8 = succes)
foreach ($proj in @("Tracker.Watcher", "Tracker.Daemon", "Tracker.Supervisor", "Tracker.Launcher")) {
    robocopy (Join-Path $stageDir $proj) (Join-Path $binDir $proj) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy swap failed for $proj (exit $LASTEXITCODE)" }
}
Remove-Item -Recurse -Force $stageDir

# --- 2b. Shortcuts (Start Menu + Desktop) -----------------------------------
# They point at Tracker.Launcher.exe, NOT at the supervisor: launched from Explorer the
# supervisor would run de-elevated (silently losing elevated-window titles) or prompt for
# UAC every time. The launcher triggers the scheduled task instead, which elevates cleanly.
$launcherExe = Join-Path $binDir "Tracker.Launcher\Tracker.Launcher.exe"
$shell = New-Object -ComObject WScript.Shell
foreach ($lnkDir in @(
        (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"),
        [Environment]::GetFolderPath("Desktop"))) {
    if (-not (Test-Path $lnkDir)) { continue }
    $lnk = $shell.CreateShortcut((Join-Path $lnkDir "Minutar.lnk"))
    $lnk.TargetPath = $launcherExe
    $lnk.IconLocation = "$launcherExe,0"
    $lnk.Description = "Pornește Minutar si deschide dashboard-ul"
    $lnk.WorkingDirectory = Split-Path $launcherExe
    $lnk.Save()
    Write-Host "Shortcut: $(Join-Path $lnkDir 'Minutar.lnk')"
}
[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null

# --- 3. Scheduled task (M6): the SUPERVISOR owns everything else -------------
# The supervisor starts and watchdogs the watcher + daemon (decision #10),
# so a single elevated task is enough.
if ($SkipTasks) {
    # publish disabled the existing task above — bring the stack back up
    if ($task) {
        $task | Enable-ScheduledTask | Out-Null
        Start-ScheduledTask -TaskName "TimeTracker-Supervisor"
    }
    Write-Host "Skipping task registration (-SkipTasks); existing task re-enabled."
    exit 0
}

# clean up interim M1 tasks if they exist
foreach ($old in @("TimeTracker-AwServer", "TimeTracker-Watcher", "TimeTracker-Daemon")) {
    Unregister-ScheduledTask -TaskName $old -Confirm:$false -ErrorAction SilentlyContinue
}

$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 3650) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
# the logged-in user owns the task (single-user app)
$taskUser = $env:USERNAME
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
# launch the WinExe apphost directly: no console window in the taskbar (closing a console
# would kill the whole stack). It resolves the runtime from the machine-wide install
# (verified in step 1) — no environment variable is involved, for any user.
$exe = Join-Path $binDir "Tracker.Supervisor\Tracker.Supervisor.exe"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--config `"$configPath`""

Register-ScheduledTask -TaskName "TimeTracker-Supervisor" -Action $action -Trigger $trigger `
    -User $taskUser -RunLevel Highest -Settings $settings -Force | Out-Null
Write-Host "Registered task TimeTracker-Supervisor -> $exe (user: $taskUser)"

Start-ScheduledTask -TaskName "TimeTracker-Supervisor"
Write-Host "Done. Supervisor started (tray icon); dashboard: http://localhost:5601"
