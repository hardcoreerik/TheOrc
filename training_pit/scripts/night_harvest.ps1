# night_harvest.ps1 — NIGHT HARVEST: autonomous dataset farming until dawn.
#
#   "The goblins work while you sleep. You still sign the ledger in the morning."
#
# Each cycle: a local model authors a fresh goal tranche (generate_goals.py),
# the farm runs them headless (farm_batch.ps1), the deterministic pre-screen
# auto-rejects mechanical defects (prescreen_captures.py --apply), and the
# judge model triages survivors by fabrication risk (judge_captures.py).
# Loops until dawn, a duration limit, or the stop file appears.
#
# NIGHT HARVEST never approves training examples and never starts Phase 3.
# Morning-after: review the triage TSVs, approve, --export-train, commit.
#
# Stop it any time:   New-Item .orc\swarm\HARVEST_STOP
param(
    [string]$Until        = "06:00",          # local "dawn" HH:mm (used when -Hours is 0)
    [double]$Hours        = 0,                # run for N hours instead of until dawn
    [switch]$UntilStopped,                    # ignore the clock; only the stop file ends it
    [int]$GoalsPerCycle   = 25,
    [string]$GenModel     = "qwen2.5-coder:14b",
    [string]$JudgeModel   = "qwen2.5-coder:14b",
    [int]$TimeoutSec      = 300
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

$stopFile = Join-Path $root ".orc\swarm\HARVEST_STOP"
if (Test-Path $stopFile) {
    # A pre-existing stop file is an operator's standing stop request —
    # honor it, never silently delete it (codex audit finding).
    Write-Host "HARVEST_STOP already present — refusing to start. Remove it to allow harvesting:" -ForegroundColor Red
    Write-Host "  Remove-Item $stopFile" -ForegroundColor Yellow
    exit 1
}

if ($UntilStopped)      { $deadline = [datetime]::MaxValue }
elseif ($Hours -gt 0)   { $deadline = (Get-Date).AddHours($Hours) }
else {
    $deadline = Get-Date "$(Get-Date -Format yyyy-MM-dd) $Until"
    if ($deadline -le (Get-Date)) { $deadline = $deadline.AddDays(1) }
}

$logDir = Join-Path $root ".orc\swarm\night_harvest"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir "harvest_$(Get-Date -Format yyyyMMdd_HHmm).log"
function Log($msg, $color = "Gray") {
    $line = "$(Get-Date -Format HH:mm:ss)  $msg"
    Write-Host $line -ForegroundColor $color
    Add-Content $log $line
}

function StopRequested { Test-Path $stopFile }

Log "NIGHT HARVEST begins — until $(if ($UntilStopped) {'stopped'} else {$deadline.ToString('ddd HH:mm')})" Magenta
Log "stop file: $stopFile" DarkGray

$cycle = 0
$totals = @{ goals = 0; staged = 0; rejected = 0 }
# Run-unique stamp: a date-only prefix collides when a run crosses midnight or
# two runs share a day — the new run then "resumes" the old run's done files
# and farms nothing (2026-06-11 incident: cycles spun for 1.5 h staging zero).
$runStamp = Get-Date -Format "yyMMdd_HHmmss"
while ((Get-Date) -lt $deadline -and -not (StopRequested)) {
    $cycle++
    $prefix = "NH${runStamp}c$cycle"
    if (Test-Path "training_pit\batch_${prefix}_goals.psv") {
        Log "tranche file for $prefix already exists — refusing to overwrite, ending harvest" Red; break
    }
    Log "── cycle $cycle ── authoring $GoalsPerCycle goals ($GenModel)" Cyan

    # Fail closed at every phase: a nonzero native exit code ends the harvest
    # rather than farming a partial/garbage tranche (codex audit finding).
    python training_pit\scripts\generate_goals.py --count $GoalsPerCycle `
        --model $GenModel --prefix $prefix 2>&1 | Tee-Object -Append $log | Out-Null
    if ($LASTEXITCODE -ne 0) { Log "goal generation failed (exit $LASTEXITCODE) — ending harvest" Red; break }
    $goalsFile = "training_pit\batch_${prefix}_goals.psv"
    $goalCount = if (Test-Path $goalsFile) { @(Get-Content $goalsFile).Count } else { 0 }
    if ($goalCount -lt [Math]::Ceiling($GoalsPerCycle * 0.8)) {
        Log "tranche undersized ($goalCount/$GoalsPerCycle) — ending harvest" Red; break
    }
    $totals.goals += $goalCount
    if (StopRequested) { break }

    Log "farming $goalsFile" Cyan
    pwsh -ExecutionPolicy Bypass -File training_pit\scripts\farm_batch.ps1 `
        -GoalsFile $goalsFile -DoneFile "training_pit\batch_${prefix}_done.csv" `
        -TimeoutSec $TimeoutSec -StopFile $stopFile 2>&1 |
        Tee-Object -Append $log | Out-Null
    if ($LASTEXITCODE -ne 0) { Log "farm runner failed (exit $LASTEXITCODE) — ending harvest" Red; break }
    if (StopRequested) { break }

    Log "pre-screen (deterministic auto-reject)" Cyan
    $pre = python training_pit\scripts\prescreen_captures.py --goals $goalsFile --apply 2>&1
    if ($LASTEXITCODE -ne 0) { $pre | Add-Content $log; Log "pre-screen failed (exit $LASTEXITCODE) — ending harvest" Red; break }
    $pre | Add-Content $log
    $summary = ($pre | Select-String '^# pass=').Line
    if ($summary -match 'pass=(\d+).*reject=(\d+)') {
        $totals.staged += [int]$Matches[1]; $totals.rejected += [int]$Matches[2]
    }
    Log "  $summary" Gray
    if (StopRequested) { break }

    Log "judge triage ($JudgeModel)" Cyan
    python training_pit\scripts\judge_captures.py --goals $goalsFile `
        --model $JudgeModel --out "training_pit\batch_${prefix}_triage.tsv" 2>&1 |
        Tee-Object -Append $log | Out-Null
    if ($LASTEXITCODE -ne 0) { Log "judge failed (exit $LASTEXITCODE) — captures remain staged for manual triage" Yellow }

    Log "cycle $cycle done — running totals: $($totals.goals) farmed, $($totals.staged) awaiting review, $($totals.rejected) auto-rejected" Green
}

$why = if (StopRequested) { "stop file" } else { "dawn" }
Log "NIGHT HARVEST ends ($why) — $cycle cycle(s)" Magenta
Log "harvested: $($totals.goals) goals farmed · $($totals.staged) survivors awaiting your review · $($totals.rejected) auto-rejected" Magenta
Log "morning-after: review training_pit\batch_NH*_triage.tsv (high risk first), approve, then --export-train" Yellow
if (Test-Path $stopFile) { Remove-Item $stopFile -Confirm:$false }
