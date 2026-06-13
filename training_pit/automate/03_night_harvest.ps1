param(
    [int]    $Hours          = 8,
    [int]    $GoalsPerCycle  = 25,
    [string] $GenModel       = "qwen2.5-coder:7b",
    [string] $MainHost       = "http://100.102.190.112:11434",
    [string] $BossModel      = "theorc-boss:gemma4",
    [string] $Workspace      = "\\HARDCORERIK\F$\Ai\OrchestratorIDE",
    [string] $LocalHost      = "http://localhost:11434",
    [int]    $MaxGoalsTotal  = 240,
    [switch] $UntilStopped
)
$ErrorActionPreference = "Continue"

$stopFile  = Join-Path $Workspace ".orc\swarm\HARVEST_STOP"
$logDir    = Join-Path $Workspace ".orc\swarm\night_harvest"
$deadline  = (Get-Date).AddHours($Hours)
$prefix    = "HCNH" + (Get-Date -Format "yyMMdd_HHmm")
$logFile   = Join-Path $logDir "hc_harvest_${prefix}.log"
$scriptDir = $PSScriptRoot

New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss"
    $line = "[$ts] $msg"
    Write-Host $line
    Add-Content $logFile $line -Encoding UTF8
}

if (Test-Path $stopFile) {
    Log "ERROR: HARVEST_STOP already exists. Remove it first."
    exit 1
}

Log "=== HARDCOREPC Night Harvest starting ==="
Log "  Hours:         $Hours (deadline $($deadline.ToString('HH:mm')))"
Log "  Goals/cycle:   $GoalsPerCycle"
Log "  Gen model:     $GenModel @ $LocalHost"
Log "  Boss model:    $BossModel @ $MainHost"
Log "  Max goals:     $MaxGoalsTotal"
Log "  Log:           $logFile"

$cycle = 0
$totalGoals = 0
$prevTotal  = -1

while ($true) {
    # Time check
    if (-not $UntilStopped -and (Get-Date) -ge $deadline) {
        Log "Deadline reached — stopping cleanly."
        break
    }

    # Stop signal check
    if (Test-Path $stopFile) {
        Log "HARVEST_STOP detected — stopping cleanly."
        break
    }

    # Novelty exhaustion: 2 consecutive cycles with no new goals
    if ($totalGoals -gt 0 -and $totalGoals -eq $prevTotal) {
        Log "SWEET SPOT — novelty exhausted, stopping."
        break
    }
    if ($totalGoals -ge $MaxGoalsTotal) {
        Log "Max goals ($MaxGoalsTotal) reached — stopping."
        break
    }

    $cycle++
    $cyclePrefix = "${prefix}c${cycle}"
    Log "── Cycle $cycle starting (prefix: $cyclePrefix) ──"

    # Step 1: Generate goals
    Log "Step 1: Generating $GoalsPerCycle goals..."
    & pwsh -ExecutionPolicy Bypass -File "$scriptDir\01_scout_goals.ps1" `
        -GoalsPerBatch $GoalsPerCycle `
        -GenModel $GenModel `
        -LocalHost $LocalHost `
        -Workspace $Workspace `
        -Prefix $cyclePrefix

    $goalsFile = Join-Path $Workspace "training_pit\batch_${cyclePrefix}_goals.psv"
    if (-not (Test-Path $goalsFile)) {
        Log "No goals file produced — skipping farming this cycle."
        continue
    }

    $newGoals = (Get-Content $goalsFile -ErrorAction SilentlyContinue | Where-Object {$_}).Count
    $prevTotal = $totalGoals
    $totalGoals += $newGoals
    Log "  $newGoals new goals (total: $totalGoals)"

    # Step 2: Farm them
    Log "Step 2: Farming goals through boss..."
    & pwsh -ExecutionPolicy Bypass -File "$scriptDir\02_farm_remote.ps1" `
        -GoalsFile $goalsFile `
        -MainHost $MainHost `
        -BossModel $BossModel `
        -Workspace $Workspace `
        -TimeoutSec 300 `
        -PlanOnly

    Log "Cycle $cycle complete."
}

Log "=== Harvest done. Cycles: $cycle | Goals generated: $totalGoals ==="
Log "Next: review captures on main machine with review_captures.py"
