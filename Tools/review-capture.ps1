# review-capture.ps1 — run Codex + TheOrc reviews side by side and stage
# the pair as a training example for the future theorc-reviewer adapter.
#
# This is the passive-capture pattern: every time you'd normally run a
# code review, run THIS instead. Codex acts as the gold reference;
# TheOrc's current attempt is captured so we can measure agreement
# and (eventually) fine-tune a reviewer adapter that closes the gap.
#
# Usage:
#   tools\review-capture.ps1                            # both, staged diff
#   tools\review-capture.ps1 -Range "HEAD~3..HEAD"      # both, commit range
#   tools\review-capture.ps1 -SkipTheOrc                # Codex only (still capture)
#   tools\review-capture.ps1 -Model qwen2.5-coder:14b   # override TheOrc model
#
# Output: a JSON capture in .orc\swarm\review-staging\review_<timestamp>.json
# plus the two raw .md verdict files in .orc\reviews\. Exit codes:
#   0 = both verdicts captured (or only the one not skipped)
#   2 = exactly one reviewer failed for any reason (partial capture still saved)
#   3 = both reviewers failed
param(
    [string]  $Range          = "",
    [string]  $Focus          = "",
    [string]  $Model          = "qwen2.5-coder:14b",
    [string]  $OllamaHost     = "http://localhost:11434",
    [int]     $TimeoutSec     = 600,
    [switch]  $SkipCodex,
    [switch]  $SkipTheOrc,
    # ── Training metadata ──────────────────────────────────────────────────
    # Who produced the diff being reviewed.
    #   "human"    — code written directly by the developer (default)
    #   "coder"    — output from a CODER swarm task
    #   "ui-dev"   — output from a UIDEVELOPER swarm task
    #   "tester"   — output from a TESTER swarm task
    #   "boss"     — boss plan output (meta-review)
    [string]  $SourceRole     = "human",
    # Which training pipelines this capture feeds. Any combination of:
    #   reviewer       — teaches TheOrc to match Codex findings
    #   worker-quality — scores worker-produced code; CLEAN=good worker signal
    #   boss-closure   — links back to the boss plan that produced this task
    [string[]]$TrainingTargets = @("reviewer"),
    # Free-text tag for grouping captures by work session or feature.
    [string]  $SessionLabel   = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Resolve diff scope + capture pre-state ────────────────────────────────
$stat = if ($Range) { git diff --stat "$Range" 2>$null | Select-Object -Last 1 }
        else        { git diff --cached --stat | Select-Object -Last 1 }
if (-not $stat) {
    Write-Host "Nothing to review — capture skipped." -ForegroundColor Yellow
    exit 0
}

# Track which review files were present before each run so we can grab
# the new ones afterwards without parsing the script's stdout.
$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$beforeCodex   = Get-ChildItem $reviewDir -Filter "codex_*.md"   -ErrorAction SilentlyContinue | ForEach-Object Name
$beforeTheOrc  = Get-ChildItem $reviewDir -Filter "theorc_*.md"  -ErrorAction SilentlyContinue | ForEach-Object Name

# ── Run Codex (gold reference) ────────────────────────────────────────────
$codexVerdict = $null
$codexFailed  = $false
if (-not $SkipCodex) {
    Write-Host ""
    Write-Host "── Codex review ───────────────────────────────" -ForegroundColor Magenta
    $codexArgs = @("-File", "$PSScriptRoot\codex-review.ps1", "-TimeoutSec", $TimeoutSec)
    if ($Range) { $codexArgs += @("-Range", $Range) }
    if ($Focus) { $codexArgs += @("-Focus", $Focus) }
    & pwsh -ExecutionPolicy Bypass @codexArgs
    $codexFailed = $LASTEXITCODE -ne 0

    $newCodex = Get-ChildItem $reviewDir -Filter "codex_*.md" -ErrorAction SilentlyContinue |
                Where-Object { $beforeCodex -notcontains $_.Name } |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newCodex) { $codexVerdict = Get-Content $newCodex.FullName -Raw }
}

# ── Run TheOrc (the apprentice) ───────────────────────────────────────────
$theorcVerdict = $null
$theorcFailed  = $false
if (-not $SkipTheOrc) {
    Write-Host ""
    Write-Host "── TheOrc review ──────────────────────────────" -ForegroundColor Cyan
    $theorcArgs = @("-File", "$PSScriptRoot\theorc-review.ps1",
                    "-Model", $Model, "-OllamaHost", $OllamaHost, "-TimeoutSec", $TimeoutSec)
    if ($Range) { $theorcArgs += @("-Range", $Range) }
    if ($Focus) { $theorcArgs += @("-Focus", $Focus) }
    & pwsh -ExecutionPolicy Bypass @theorcArgs
    $theorcFailed = $LASTEXITCODE -ne 0

    $newTheOrc = Get-ChildItem $reviewDir -Filter "theorc_*.md" -ErrorAction SilentlyContinue |
                 Where-Object { $beforeTheOrc -notcontains $_.Name } |
                 Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newTheOrc) { $theorcVerdict = Get-Content $newTheOrc.FullName -Raw }
}

# ── Stage the capture (training example shape) ───────────────────────────
# Mirrors .orc/swarm/dataset-staging/ — review_capture_<ts>.json. A future
# review_dataset.py script will convert these to a JSONL training set for
# the reviewer adapter.
$stagingDir = Join-Path $root ".orc\swarm\review-staging"
New-Item -ItemType Directory -Force $stagingDir | Out-Null

$ts        = Get-Date -Format yyyyMMdd_HHmmss
$exampleId = "review_$ts"

# Pull the actual diff text (single source of truth — re-running git here
# avoids capturing whatever each reviewer scraped).
$diffArgs = if ($Range) {
    @("log", "-p", "$Range", "--", ".", ":!publish", ":!*.psv", ":!*.tsv", ":!*.jsonl")
} else {
    @("diff", "--cached")
}
$diffText = & git @diffArgs 2>&1 | Out-String

$capture = [ordered]@{
    example_id       = $exampleId
    captured_at      = (Get-Date).ToString("o")
    scope            = if ($Range) { "range" } else { "staged" }
    range            = $Range
    focus            = $Focus
    stats            = $stat.Trim()
    diff             = $diffText
    verdicts         = [ordered]@{
        codex  = $codexVerdict
        theorc = $theorcVerdict
        # Claude verdict slot reserved — a future capture in TheOrc UI will
        # fill it by running the same prompt against an API-key configured
        # Claude. Leaving the field present keeps the schema stable.
        claude = $null
    }
    gold             = "codex"   # which verdict is the training target
    review_model     = $Model
    # ── Training routing metadata ─────────────────────────────────────────
    # source_role    — who produced the diff (human / coder / ui-dev / etc.)
    # training_targets — which fine-tune pipelines consume this capture:
    #     reviewer       → teaches TheOrc to find what Codex finds
    #     worker-quality → CLEAN verdict = positive worker-quality signal
    #     boss-closure   → links review outcome back to the boss plan
    # session_label  — free-text tag for grouping by feature or work session
    source_role      = $SourceRole
    training_targets = $TrainingTargets
    session_label    = $SessionLabel
    versions         = [ordered]@{
        theorc_app    = (Select-String -Path OrchestratorIDE\OrchestratorIDE.csproj -Pattern '<Version>(.+)</Version>' |
                         Select-Object -First 1).Matches.Groups[1].Value
        git_head      = (git rev-parse --short HEAD 2>$null)
        git_branch    = (git rev-parse --abbrev-ref HEAD 2>$null)
    }
}
$captureFile = Join-Path $stagingDir "review_capture_${ts}.json"
$capture | ConvertTo-Json -Depth 8 | Set-Content $captureFile -Encoding utf8

Write-Host ""
Write-Host "── Capture ────────────────────────────────────" -ForegroundColor Green
Write-Host "  staged: $captureFile"
$codexLine  = if ($codexVerdict)  { "✓ $($codexVerdict.Length) chars"  } else { "(none)" }
$theorcLine = if ($theorcVerdict) { "✓ $($theorcVerdict.Length) chars" } else { "(none)" }
Write-Host "  codex : $codexLine"
Write-Host "  theorc: $theorcLine"

if ($codexFailed -and $theorcFailed) { exit 3 }
if ($codexFailed -or  $theorcFailed) { exit 2 }
exit 0
