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
# Prefer the user-scope SDK: "C:\Program Files\dotnet" may exist with NO SDK installed
# (runtime-only), which breaks publish. The user-scope install also needs DOTNET_ROOT
# so apphost exes (supervisor task!) find the runtime (else exit 0x80008096).
Write-Host "Running as: $env:USERNAME | LOCALAPPDATA: $env:LOCALAPPDATA"
# candidates: user-scope SDK locations, then PATH
$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\dotnet\dotnet.exe"),
    (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
)
$dotnet = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($dotnet) {
    $env:DOTNET_ROOT = Split-Path $dotnet
    [Environment]::SetEnvironmentVariable("DOTNET_ROOT", $env:DOTNET_ROOT, "User")
    Write-Host "Set user env DOTNET_ROOT -> $env:DOTNET_ROOT"
} else {
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
foreach ($proj in @("Tracker.Watcher", "Tracker.Daemon", "Tracker.Supervisor")) {
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
Get-Process Tracker.Supervisor, Tracker.Daemon, Tracker.Watcher -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# swap: mirror stage -> live per componenta (robocopy: exit code <8 = succes)
foreach ($proj in @("Tracker.Watcher", "Tracker.Daemon", "Tracker.Supervisor")) {
    robocopy (Join-Path $stageDir $proj) (Join-Path $binDir $proj) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy swap failed for $proj (exit $LASTEXITCODE)" }
}
Remove-Item -Recurse -Force $stageDir

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
# would kill the whole stack). DOTNET_ROOT (user env, set above) lets the apphost find the
# runtime; the supervisor propagates it to its children too.
$exe = Join-Path $binDir "Tracker.Supervisor\Tracker.Supervisor.exe"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--config `"$configPath`""

Register-ScheduledTask -TaskName "TimeTracker-Supervisor" -Action $action -Trigger $trigger `
    -User $taskUser -RunLevel Highest -Settings $settings -Force | Out-Null
Write-Host "Registered task TimeTracker-Supervisor -> $exe (user: $taskUser)"

Start-ScheduledTask -TaskName "TimeTracker-Supervisor"
Write-Host "Done. Supervisor started (tray icon); dashboard: http://localhost:5601"
