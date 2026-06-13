param(
    [string] $GoalsFile  = "",
    [string] $MainHost   = "http://100.102.190.112:11434",
    [string] $BossModel  = "theorc-boss:gemma4",
    [string] $Workspace  = "\\HARDCORERIK\F$\Ai\OrchestratorIDE",
    [int]    $TimeoutSec = 300,
    [int]    $MaxGoals   = 0,
    [switch] $PlanOnly
)
$ErrorActionPreference = "Continue"

$swarmcli = Join-Path $Workspace "Tools\SwarmCli\bin\Release\net10.0-windows\swarmcli.exe"
if (-not (Test-Path $swarmcli)) {
    Write-Error "swarmcli not found at $swarmcli"
    exit 1
}

# Find latest goals file if not specified
if (-not $GoalsFile) {
    $pitDir = Join-Path $Workspace "training_pit"
    $GoalsFile = Get-ChildItem (Join-Path $pitDir "batch_*_goals.psv") -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Select-Object -ExpandProperty FullName
    if (-not $GoalsFile) {
        Write-Error "No goals file found. Run 01_scout_goals.ps1 first."
        exit 1
    }
    Write-Host "[FARM] Using latest goals file: $GoalsFile"
}

$doneFile = $GoalsFile -replace "_goals\.psv$", "_done.csv"
Write-Host "[FARM] Goals:    $GoalsFile"
Write-Host "[FARM] Done CSV: $doneFile"
Write-Host "[FARM] Boss:     $BossModel at $MainHost"

# Load already-done goals
$done = @{}
if (Test-Path $doneFile) {
    Import-Csv $doneFile | ForEach-Object { $done[$_.goal_id] = $_.exit_code }
    Write-Host "[FARM] Resuming — $($done.Count) goals already done"
}

# Load goals PSV
$rows = Get-Content $GoalsFile -Encoding UTF8 | Where-Object { $_ -ne "" }
$total = $rows.Count
Write-Host "[FARM] Total goals in file: $total"

$count = 0; $pass = 0; $fail = 0; $skip = 0

foreach ($row in $rows) {
    if ($MaxGoals -gt 0 -and $count -ge $MaxGoals) { break }

    $parts = $row -split "\|", 2
    $goalId = $parts[0].Trim()
    $goal   = if ($parts.Count -gt 1) { $parts[1].Trim() } else { $parts[0].Trim() }

    if ($done.ContainsKey($goalId)) {
        $skip++
        continue
    }

    $count++
    Write-Host "`n[$count/$total] $($goal.Substring(0, [Math]::Min(80, $goal.Length)))..."

    # Check for stop signal
    $stopFile = Join-Path $Workspace ".orc\swarm\HARVEST_STOP"
    if (Test-Path $stopFile) {
        Write-Warning "[FARM] HARVEST_STOP found — stopping cleanly."
        break
    }

    $farmArgs = @(
        "--goal",      $goal,
        "--workspace", $Workspace,
        "--boss",      $BossModel,
        "--host",      $MainHost,
        "--timeout",   $TimeoutSec
    )
    if ($PlanOnly) { $farmArgs += "--plan-only" }

    $proc = Start-Process $swarmcli -ArgumentList $farmArgs -Wait -PassThru -NoNewWindow
    $exitCode = $proc.ExitCode

    $result = switch ($exitCode) {
        0 { "staged";   $pass++ }
        1 { "error";    $fail++ }
        2 { "marginal"; $fail++ }
        3 { "timeout";  $fail++ }
        default { "unknown"; $fail++ }
    }

    Write-Host "  -> $result (exit $exitCode)"

    # Append to done CSV
    $row = [pscustomobject]@{
        goal_id   = $goalId
        exit_code = $exitCode
        result    = $result
        time      = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    }
    if (-not (Test-Path $doneFile)) {
        $row | Export-Csv $doneFile -NoTypeInformation -Encoding UTF8
    } else {
        $row | Export-Csv $doneFile -NoTypeInformation -Encoding UTF8 -Append
    }
}

Write-Host "`n[FARM] Summary: $pass staged | $fail failed/marginal | $skip skipped (already done)"
Write-Host "[FARM] Done file: $doneFile"
