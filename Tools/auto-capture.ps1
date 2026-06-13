# auto-capture.ps1 — perpetual training-data capture for TheOrc's fine-tune pipelines.
#
# The "perpetual capture" pattern: instead of calling review-capture.ps1 manually
# with an explicit range, call this script after any meaningful commit. It reads
# .orc/capture-marker.json, computes the range since the last captured SHA, runs
# the Codex review-capture pipeline on that range, then advances the marker.
# Over time this builds a continuously-growing, multi-purpose dataset:
#
#   reviewer       — teaches theorc-reviewer:v1 to match Codex findings
#   worker-quality — CLEAN Codex verdict on a swarm task's diff = positive training
#   boss-closure   — links plan → code → review outcome (boss adapter v3+)
#
# TheOrc review is opt-in via -IncludeTheOrc (requires local Ollama + VRAM).
# By default only Codex runs — no GPU required during normal development.
#
# The capture JSON lands in .orc/swarm/review-staging/ alongside manually-triggered
# captures. The Training Pit UI reads the same directory for its progress bar.
#
# Usage:
#   tools\auto-capture.ps1                            # Codex only, all commits since last marker
#   tools\auto-capture.ps1 -SourceRole coder          # tag diff as worker output
#   tools\auto-capture.ps1 -IncludeTheOrc             # Codex + TheOrc side-by-side
#   tools\auto-capture.ps1 -Model qwen2.5-coder:32b -IncludeTheOrc  # override model
#
# Exit codes (mirrors review-capture.ps1):
#   0  = capture saved, marker advanced
#   1  = no new commits since last marker, nothing to capture
#   2  = partial capture saved (one reviewer failed), marker advanced
#   3  = both reviewers failed, marker NOT advanced
#   4  = git error or workspace not a repo
param(
    [string]  $Model          = "qwen2.5-coder:14b",
    [string]  $OllamaHost     = "http://localhost:11434",
    [string]  $Focus          = "",
    [int]     $TimeoutSec     = 600,
    [switch]  $SkipCodex,
    [switch]  $SkipTheOrc,      # legacy no-op — TheOrc is now off by default
    [switch]  $IncludeTheOrc,   # opt-in: run TheOrc alongside Codex (requires Ollama + VRAM)
    [string]  $SourceRole     = "human",
    [string[]]$TrainingTargets = @("reviewer"),
    [string]  $SessionLabel   = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Locate the capture marker ─────────────────────────────────────────────────
$orcDir     = Join-Path $root ".orc"
$markerFile = Join-Path $orcDir "capture-marker.json"
New-Item -ItemType Directory -Force $orcDir | Out-Null

$marker = if (Test-Path $markerFile) {
    Get-Content $markerFile | ConvertFrom-Json
} else {
    # Bootstrap: treat the initial commit as the baseline (nothing captured yet)
    $initialSha = git rev-parse --short (git rev-list --max-parents=0 HEAD 2>$null) 2>$null
    [PSCustomObject]@{
        last_captured_sha = $initialSha ?? ""
        last_captured_at  = ""
        total_captures    = 0
    }
}

# ── Compute the range since last capture ─────────────────────────────────────
$headSha = git rev-parse HEAD 2>$null
if (-not $headSha) {
    Write-Host "auto-capture: workspace is not a git repository." -ForegroundColor Red
    exit 4
}

$lastSha = $marker.last_captured_sha
if ($lastSha -and $lastSha -eq (git rev-parse --short HEAD 2>$null)) {
    Write-Host "auto-capture: no new commits since last capture ($lastSha). Nothing to do." -ForegroundColor Yellow
    exit 1
}

$range = if ($lastSha) { "$lastSha..HEAD" } else { "HEAD" }

# Count commits in range (informational)
$commitCount = (git log --oneline "$range" 2>$null | Measure-Object).Count
Write-Host ""
Write-Host "── auto-capture ────────────────────────────────" -ForegroundColor Cyan
Write-Host "  range        : $range ($commitCount commit$(if($commitCount -ne 1){'s'}))"
Write-Host "  source_role  : $SourceRole"
Write-Host "  targets      : $($TrainingTargets -join ', ')"
if ($SessionLabel) { Write-Host "  session      : $SessionLabel" }
Write-Host ""

# ── Delegate to review-capture.ps1 ───────────────────────────────────────────
$captureScript = Join-Path $PSScriptRoot "review-capture.ps1"
$captureArgs = @(
    "-File", $captureScript,
    "-Range", $range,
    "-Model", $Model,
    "-OllamaHost", $OllamaHost,
    "-TimeoutSec", $TimeoutSec,
    "-SourceRole", $SourceRole,
    "-TrainingTargets", ($TrainingTargets -join " "),
    "-SessionLabel", $SessionLabel
)
if ($Focus)        { $captureArgs += @("-Focus", $Focus) }
if ($SkipCodex)    { $captureArgs += "-SkipCodex"         }
if ($IncludeTheOrc){ $captureArgs += "-IncludeTheOrc"     }

& pwsh -NoProfile -ExecutionPolicy Bypass @captureArgs
$captureExit = $LASTEXITCODE

# ── Advance the marker (only on success or partial success) ──────────────────
if ($captureExit -le 2) {
    $newMarker = [ordered]@{
        last_captured_sha = git rev-parse --short HEAD 2>$null
        last_captured_at  = (Get-Date).ToString("o")
        total_captures    = ([int]($marker.total_captures ?? 0)) + 1
    }
    $newMarker | ConvertTo-Json | Set-Content $markerFile -Encoding utf8
    Write-Host ""
    Write-Host "  marker advanced → $($newMarker.last_captured_sha)" -ForegroundColor Green
    Write-Host "  total captures  : $($newMarker.total_captures)"
}

exit $captureExit
