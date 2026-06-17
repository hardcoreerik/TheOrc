# dual-review.ps1 — run Codex + Grok code reviews in parallel, merge findings.
#
# Both reviewers analyse the same diff simultaneously and their findings are
# combined into a single report ranked by severity. A BLOCKER from EITHER
# reviewer is surfaced as a BLOCKER in the merged output.
#
# Usage:
#   tools\dual-review.ps1                        # latest commit
#   tools\dual-review.ps1 -Range "HEAD~5..HEAD"  # commit range
#   tools\dual-review.ps1 -Staged                # staged changes
#   tools\dual-review.ps1 -GrokModel grok-composer-2.5-fast  # use heavier model
#   tools\dual-review.ps1 -Focus "async safety"
#   tools\dual-review.ps1 -SkipCodex             # Grok only
#   tools\dual-review.ps1 -SkipGrok              # Codex only
#
# Exit codes:
#   0 = both clean    1 = one or more BLOCKERs    2 = one reviewer timed out
param(
    [string]$Range      = "HEAD~1..HEAD",
    [switch]$Staged,
    [string]$Focus      = "",
    [string]$GrokModel  = "grok-build",
    [int]   $TimeoutSec = 600,
    [switch]$SkipCodex,
    [switch]$SkipGrok
)

$ErrorActionPreference = "Stop"
$root  = Split-Path $PSScriptRoot -Parent
$tools = $PSScriptRoot

$rangeArg = if ($Staged) { @('-Staged') } else { @('-Range', $Range) }
$focusArg = if ($Focus)  { @('-Focus', $Focus) } else { @() }

# ── Launch both reviewers as background jobs ──────────────────────────────────
$jobs = @()

if (-not $SkipCodex) {
    Write-Host "[dual-review] Starting Codex reviewer..." -ForegroundColor Cyan
    $jobs += Start-Job -Name "Codex" -ScriptBlock {
        param($tools, $range, $focus, $timeout)
        & pwsh -NonInteractive -File "$tools\codex-review.ps1" @range @focus -TimeoutSec $timeout
        $LASTEXITCODE
    } -ArgumentList $tools, $rangeArg, $focusArg, $TimeoutSec
}

if (-not $SkipGrok) {
    Write-Host "[dual-review] Starting Grok reviewer ($GrokModel)..." -ForegroundColor Magenta
    $jobs += Start-Job -Name "Grok" -ScriptBlock {
        param($tools, $range, $focus, $model, $timeout)
        & pwsh -NonInteractive -File "$tools\grok-review.ps1" @range @focus -Model $model -TimeoutSec $timeout
        $LASTEXITCODE
    } -ArgumentList $tools, $rangeArg, $focusArg, $GrokModel, $TimeoutSec
}

if ($jobs.Count -eq 0) {
    Write-Host "Both reviewers skipped — nothing to do." -ForegroundColor Yellow
    exit 0
}

# ── Wait and stream output as each finishes ───────────────────────────────────
Write-Host "[dual-review] Waiting for reviewers (timeout ${TimeoutSec}s each)..." -ForegroundColor DarkGray
$jobs | Wait-Job -Timeout ($TimeoutSec + 30) | Out-Null

$timedOut    = $false
$codexOutput = ""
$grokOutput  = ""

$reviewerFailed = $false

foreach ($job in $jobs) {
    $output = Receive-Job $job 2>&1
    # The last item emitted by the job script block is $LASTEXITCODE (an integer).
    # Receive-Job drains ChildJobs[0].Output, so read it from $output instead.
    $lastItem = $output | Select-Object -Last 1
    $exitCode = if ("$lastItem" -match '^\d+$') { [int]"$lastItem" } else { $null }
    $textItems = if ($null -ne $exitCode) { $output | Select-Object -SkipLast 1 } else { $output }
    $text     = ($textItems | ForEach-Object { "$_" }) -join "`n"

    if ($job.State -eq 'Running') {
        Stop-Job $job
        $timedOut = $true
        Write-Host "[$($job.Name)] TIMED OUT" -ForegroundColor Red
        $reviewerFailed = $true
    } elseif ($exitCode -notin @(0, 1)) {
        # Exit code 1 = findings present (normal); anything else = tool error
        Write-Host "[$($job.Name)] exited with code $exitCode — reviewer may have failed" -ForegroundColor Red
        $reviewerFailed = $true
        Write-Host $text -ForegroundColor DarkRed
    } else {
        Write-Host ""
        Write-Host "═══ $($job.Name) findings ═══" -ForegroundColor $(if ($job.Name -eq 'Codex') { 'Cyan' } else { 'Magenta' })
        Write-Host $text
    }

    if ($job.Name -eq 'Codex') { $codexOutput = $text }
    if ($job.Name -eq 'Grok')  { $grokOutput  = $text }
    Remove-Job $job
}

if ($reviewerFailed) {
    Write-Host ""
    Write-Host "WARNING: one or more reviewers failed — merged results may be incomplete." -ForegroundColor Yellow
}

# ── Parse and merge findings ──────────────────────────────────────────────────
function Get-Findings([string]$text, [string]$source) {
    $text -split "`n" |
        Where-Object { $_ -match '^(BLOCKER|MINOR)\s' } |
        ForEach-Object { "[$source] $_" }
}

$allBlockers = @()
$allMinors   = @()

foreach ($src in @(@{Name='CODEX'; Text=$codexOutput}, @{Name='GROK'; Text=$grokOutput})) {
    if ($src.Text) {
        $allBlockers += Get-Findings $src.Text $src.Name | Where-Object { $_ -match '^\[.*\] BLOCKER' }
        $allMinors   += Get-Findings $src.Text $src.Name | Where-Object { $_ -match '^\[.*\] MINOR' }
    }
}

# ── Merged report ─────────────────────────────────────────────────────────────
$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$mergedFile = Join-Path $reviewDir "dual_$(Get-Date -Format yyyyMMdd_HHmmss).md"

$mergedLines = @(
    "# Dual Review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
    "Range: $Range  |  Grok model: $GrokModel",
    ""
)

if ($allBlockers.Count -gt 0) {
    $mergedLines += "## BLOCKERs ($($allBlockers.Count))"
    $mergedLines += $allBlockers
    $mergedLines += ""
}
if ($allMinors.Count -gt 0) {
    $mergedLines += "## MINORs ($($allMinors.Count))"
    $mergedLines += $allMinors
    $mergedLines += ""
}
if ($allBlockers.Count -eq 0 -and $allMinors.Count -eq 0) {
    $mergedLines += "## CLEAN — no findings from either reviewer"
}

$mergedLines += "---"
$mergedLines += "### Codex raw"
$mergedLines += $codexOutput
$mergedLines += ""
$mergedLines += "### Grok raw"
$mergedLines += $grokOutput

$mergedLines | Set-Content $mergedFile -Encoding utf8

Write-Host ""
Write-Host "═══ MERGED SUMMARY ═══" -ForegroundColor White
if ($allBlockers.Count -gt 0) {
    Write-Host "BLOCKERs: $($allBlockers.Count)" -ForegroundColor Red
    $allBlockers | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
if ($allMinors.Count -gt 0) {
    Write-Host "MINORs: $($allMinors.Count)" -ForegroundColor Yellow
    $allMinors | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
if ($allBlockers.Count -eq 0 -and $allMinors.Count -eq 0) {
    Write-Host "CLEAN — both reviewers found nothing." -ForegroundColor Green
}
Write-Host ""
Write-Host "saved -> $mergedFile" -ForegroundColor DarkGray

if ($timedOut)            { exit 2 }
if ($allBlockers.Count -gt 0) { exit 1 }
exit 0
