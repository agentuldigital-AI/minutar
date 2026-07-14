# dev-run.ps1 - run individual components from source during development.
# Usage: .\scripts\dev-run.ps1 watcher | daemon | supervisor | aw-server | smoke | print-config

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("watcher", "daemon", "supervisor", "aw-server", "smoke", "print-config")]
    [string]$Component
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# Prefer dotnet on PATH; fall back to the user-scope SDK install.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) { $dotnet = $dotnet.Source }
else {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw ".NET SDK not found - see docs/SETUP.md" }
}

switch ($Component) {
    "watcher"      { & $dotnet run --project "$repo\src\Tracker.Watcher" }
    "daemon"       { & $dotnet run --project "$repo\src\Tracker.Daemon" }
    "supervisor"   { & $dotnet run --project "$repo\src\Tracker.Supervisor" }
    "smoke"        { & $dotnet run --project "$repo\src\Tracker.Watcher" -- --smoke }
    "print-config" { & $dotnet run --project "$repo\src\Tracker.Watcher" -- --print-config }
    "aw-server"    {
        $aw = Join-Path $env:LOCALAPPDATA "Programs\ActivityWatch\aw-server\aw-server.exe"
        if (-not (Test-Path $aw)) { throw "aw-server.exe not found - see docs/SETUP.md (clean install v0.13.2)" }
        & $aw
    }
}
