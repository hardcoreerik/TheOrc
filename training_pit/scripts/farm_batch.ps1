# farm_batch.ps1 — unattended swarmcli capture farming over a goals file.
# Resumable: goal IDs already in the done-file are skipped on re-run.
param(
    [string]$GoalsFile = "training_pit\batch_v3_goals.psv",
    [string]$DoneFile  = "training_pit\batch_v3_done.csv",
    [int]$TimeoutSec   = 300,
    [int]$MaxGoals     = 0,    # 0 = all
    [string]$StopFile  = ""    # touch this file to stop cleanly between goals
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

$cli = Join-Path $root "Tools\SwarmCli\bin\Release\net10.0-windows\swarmcli.exe"
if (-not (Test-Path $cli)) { Write-Error "swarmcli not found: $cli"; exit 1 }

$logDir = Join-Path $root ".orc\swarm\farm_logs"
New-Item -ItemType Directory -Force $logDir | Out-Null

$done = @{}
if (Test-Path $DoneFile) {
    Import-Csv $DoneFile | ForEach-Object { $done[$_.id] = $true }
} else {
    "id,exit,staged,seconds,timestamp" | Set-Content $DoneFile
}

$goals = Get-Content $GoalsFile | Where-Object { $_ -match '\|' }
$ran = 0
foreach ($line in $goals) {
    $parts = $line -split '\|', 3
    $id = $parts[0].Trim(); $goal = ($parts[2].Trim()) -replace '"', "'"
    if ($done.ContainsKey($id)) { continue }
    if ($MaxGoals -gt 0 -and $ran -ge $MaxGoals) { break }
    if ($StopFile -and (Test-Path $StopFile)) {
        Write-Host "Stop file found — halting farm cleanly." -ForegroundColor Yellow; break
    }

    Write-Host "[$id] farming..." -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $logFile = Join-Path $logDir "$id.log"
    $ErrorActionPreference = "Continue"  # swarmcli writes progress to stderr; not fatal
    & $cli --goal $goal --workspace $root --plan-only --timeout $TimeoutSec *> $logFile
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    $sw.Stop()

    $staged = ""
    $m = Select-String -Path $logFile -Pattern 'dataset-staging\\(plan_capture_\S+\.json)' | Select-Object -Last 1
    if ($m) { $staged = $m.Matches[0].Groups[1].Value }

    "$id,$code,$staged,$([int]$sw.Elapsed.TotalSeconds),$(Get-Date -Format o)" | Add-Content $DoneFile
    $color = if ($code -eq 0) { "Green" } elseif ($code -eq 2) { "Yellow" } else { "Red" }
    Write-Host "[$id] exit=$code staged=$staged ($([int]$sw.Elapsed.TotalSeconds)s)" -ForegroundColor $color
    $ran++
}

$rows = @(Import-Csv $DoneFile)
$stagedCount = ($rows | Where-Object { $_.staged }).Count
Write-Host "`nFarm pass complete: $($rows.Count)/$($goals.Count) goals run, $stagedCount staged." -ForegroundColor Cyan
