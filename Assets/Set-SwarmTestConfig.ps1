<#
.SYNOPSIS
    Pre-configures TheOrc settings for a specific swarm benchmark run.
    Edit settings.json directly so the app launches with the right models and trust level.

.DESCRIPTION
    Reads %APPDATA%\OrchestratorIDE\settings.json, updates the specified fields,
    and writes it back. Optionally launches TheOrc.exe afterward.

    Use this to quickly switch between benchmark model combinations without
    manually navigating the UI between runs.

.EXAMPLE
    # Round 1 — qwen14b boss + nemotron workers (default setup)
    .\Set-SwarmTestConfig.ps1 -Boss "qwen2.5-coder:14b" -Workers "nemotron-3-nano:4b-q8_0" -Slots 3 -Trust Standard -Workspace "C:\TheOrcTests\R1_CleanCSV"

.EXAMPLE
    # Round 2 — gemma12b boss + nemotron workers
    .\Set-SwarmTestConfig.ps1 -Boss "gemma4:12b" -Workers "nemotron-3-nano:4b-q8_0" -Slots 3 -Trust Standard -Workspace "C:\TheOrcTests\R2_CleanCSV" -Launch

.EXAMPLE
    # Round 3 — nano x nano
    .\Set-SwarmTestConfig.ps1 -Boss "nemotron-3-nano:4b-q8_0" -Workers "nemotron-3-nano:4b-q8_0" -Slots 3 -Trust Standard -Workspace "C:\TheOrcTests\R3_CleanCSV" -Launch
#>

param(
    [Parameter(Mandatory)]
    [string] $Boss,             # Orchestrator model (planning + merging)

    [Parameter(Mandatory)]
    [string] $Workers,          # Worker model (Researcher, Coder, UIDev)

    [Parameter()]
    [int]    $Slots    = 3,     # OLLAMA_NUM_PARALLEL — swarm needs ≥ 3

    [Parameter()]
    [ValidateSet("Plan","Guarded","Standard","FullAuto")]
    [string] $Trust    = "Standard",

    [Parameter()]
    [string] $Workspace = "",   # Pre-set workspace root (leave empty to use existing)

    [Parameter()]
    [string] $Mode     = "swarm",  # "single", "swarm", or "chat"

    [Parameter()]
    [switch] $Launch,           # If set, launch TheOrc.exe after writing settings

    [Parameter()]
    [switch] $WhatIf            # Dry run — show what would be changed without writing
)

# ── Resolve paths ─────────────────────────────────────────────────────────────

$settingsPath = Join-Path $env:APPDATA "OrchestratorIDE\settings.json"
$exePath      = "F:\Ai\OrchestratorIDE\OrchestratorIDE\bin\Debug\net10.0-windows\OrchestratorIDE.exe"

# Also check Release
if (-not (Test-Path $exePath)) {
    $exePath = "F:\Ai\OrchestratorIDE\OrchestratorIDE\bin\Release\net10.0-windows\OrchestratorIDE.exe"
}

# ── Read current settings ─────────────────────────────────────────────────────

if (-not (Test-Path $settingsPath)) {
    Write-Error "Settings file not found at: $settingsPath"
    Write-Error "Launch TheOrc at least once to create the settings file."
    exit 1
}

$json     = Get-Content $settingsPath -Raw
$settings = $json | ConvertFrom-Json

# ── Map Trust enum string to integer (matches C# TrustLevel enum) ─────────────
# Plan=0, Guarded=1, Standard=2, FullAuto=3

$trustMap = @{
    "Plan"     = 0
    "Guarded"  = 1
    "Standard" = 2
    "FullAuto" = 3
}
$trustValue = $trustMap[$Trust]

# ── Show what will change ─────────────────────────────────────────────────────

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGreen
Write-Host "  TheOrc Swarm Test Config" -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGreen
Write-Host ""
Write-Host "  Settings file : $settingsPath"
Write-Host ""
Write-Host "  CHANGES:" -ForegroundColor Yellow
Write-Host "    lastSwarmModel   : $($settings.lastSwarmModel) -> $Boss" -ForegroundColor Cyan
Write-Host "    lastWorkerModel  : $($settings.lastWorkerModel) -> $Workers" -ForegroundColor Cyan
Write-Host "    ollamaParallelSlots: $($settings.ollamaParallelSlots) -> $Slots" -ForegroundColor Cyan
Write-Host "    trustLevel       : $($settings.trustLevel) -> $trustValue ($Trust)" -ForegroundColor Cyan
Write-Host "    lastMode         : $($settings.lastMode) -> $Mode" -ForegroundColor Cyan
if ($Workspace) {
Write-Host "    defaultWorkspace : $($settings.defaultWorkspace) -> $Workspace" -ForegroundColor Cyan
}
Write-Host ""

if ($WhatIf) {
    Write-Host "  [WhatIf] No changes written." -ForegroundColor Gray
    exit 0
}

# ── Apply changes ─────────────────────────────────────────────────────────────

$settings.lastSwarmModel        = $Boss
$settings.lastWorkerModel       = $Workers
$settings.ollamaParallelSlots   = $Slots
$settings.trustLevel            = $trustValue
$settings.lastMode              = $Mode

if ($Workspace) {
    $settings.defaultWorkspace = $Workspace
    # Add to recent workspaces (prepend, deduplicate, cap at 10)
    $recent = @($Workspace) + @($settings.recentWorkspaces | Where-Object { $_ -ne $Workspace })
    $settings.recentWorkspaces = $recent | Select-Object -First 10
}

# Write back (preserve camelCase by converting with depth)
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

Write-Host "  [OK] Settings written." -ForegroundColor Green

# ── Create workspace folder if needed ────────────────────────────────────────

if ($Workspace -and -not (Test-Path $Workspace)) {
    New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
    Write-Host "  [OK] Workspace folder created: $Workspace" -ForegroundColor Green
}

# ── Launch ────────────────────────────────────────────────────────────────────

if ($Launch) {
    if (Test-Path $exePath) {
        Write-Host "  [OK] Launching TheOrc: $exePath" -ForegroundColor Green
        Start-Process $exePath
    } else {
        Write-Host "  [WARN] OrchestratorIDE.exe not found at expected paths." -ForegroundColor Yellow
        Write-Host "         Build the project first, then re-run with -Launch." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGreen
Write-Host "  Next step: enter the benchmark goal in Swarm panel" -ForegroundColor Green
Write-Host "  then click Launch Swarm." -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGreen
Write-Host ""
