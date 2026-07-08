<#
.SYNOPSIS
    Run the CF-7 gate (expanded corpus, 120 held-out questions) on this machine.

.DESCRIPTION
    Builds the context-fabric-bench tool, then runs the full cf7-gate-expanded suite
    against the 128-segment expanded corpus and the frozen 120-question held-out set.

    This script is the canonical re-run recipe for the NEWCOREPC CF-7 benchmark.
    Hand it to Codex, Grok, or another agent as the starting point for a fresh run.

    PREREQUISITES
    -------------
    - .NET 8 SDK (dotnet build)
    - Gemma 4 12B QAT Q4_0 model installed in the OrchestratorIDE model directory
      (gemma-4-12B-it-qat-q4_0.gguf or equivalent admitted model; 7B+ Admitted is required)
    - Windows with CUDA-capable GPU is recommended. CPU-only is possible but very slow
      (~10-20x longer per inference call).

    VERDICT GUIDANCE
    ----------------
    The gate criteria are defined in the suite itself. Look for these lines in stdout:

        B3 verdict: PASS/FAIL, segments X/128, questions Y/120
        Verdict (expanded corpus, real held-out suite): GO / NO-GO

    A GO result means:
    - segment_terminal_coverage above threshold
    - question_pass_rate above threshold
    - citation_precision above threshold
    - boundary_stitch_pass_rate above threshold
    - B0/B1/B2 baselines all ran to completion (Succeeded=true for every question)

    EXPECTED DURATION
    -----------------
    NEWCOREPC (RTX 5070 Ti 16GB, Gemma 12B): ~4-6 hours for 120 questions
    Lower-VRAM machines or smaller models: longer or may hit KV-slot limits

.PARAMETER RepoRoot
    Path to the OrchestratorIDE-dev repository root.
    Defaults to the parent of this script's directory (Tools/ContextFabricBench -> repo root).

.PARAMETER ModelRoot
    Path to the local model directory.
    Defaults to %APPDATA%\OrchestratorIDE\Models (standard install location).

.PARAMETER OutputDir
    Directory to write JSON/Markdown results into.
    Defaults to .orc/adversarial under the repo root.
    Each run writes its own timestamped files and does NOT overwrite prior results.

.PARAMETER MaxQuestions
    Cap the question count for a smoke test. Default 0 = run all 120.
    Example: -MaxQuestions 3 for a quick sanity check.

.PARAMETER Context
    KV context length in tokens. Default 8192.
    Lower values (4096) reduce VRAM pressure but may hurt recall.

.PARAMETER GpuLayers
    GPU layers to offload. Default -1 (auto: offload as many as VRAM allows).
    Set to 0 to force CPU-only.

.PARAMETER SkipBuild
    Skip dotnet build and use whatever exe is already in publish/.
    Use this when you know the last build matches the current source.

.PARAMETER LogFile
    Path to write a copy of stdout. Defaults to OutputDir/cf7_expanded_<timestamp>_console.log.
    Set to empty string to disable log capture.

.EXAMPLE
    # Full 120-question run (the standard closure run)
    .\Run-CF7GateExpanded.ps1

.EXAMPLE
    # Quick 3-question smoke test to verify setup before committing GPU time
    .\Run-CF7GateExpanded.ps1 -MaxQuestions 3

.EXAMPLE
    # Skip rebuild (source unchanged) and target a different model directory
    .\Run-CF7GateExpanded.ps1 -SkipBuild -ModelRoot "D:\Models\CF"

.EXAMPLE
    # Force CPU, useful for checking tool logic without a GPU
    .\Run-CF7GateExpanded.ps1 -MaxQuestions 3 -GpuLayers 0

.NOTES
    Branch: feat/cf-benchmark-remediation (or any branch that includes the
    JSON recovery fix and Cf7GateExpanded suite in Program.cs).

    Key artifacts produced:
      <OutputDir>/cf0_<timestamp>_<hash>.json          (B3 single-node CF report)
      <OutputDir>/cf7_baseline_b0_<timestamp>.json     (B0 closed-book baseline)
      <OutputDir>/cf7_baseline_b1_<timestamp>.json     (B1 truncated-prompt baseline)
      <OutputDir>/cf7_baseline_b2_<timestamp>.json     (B2 top-k RAG baseline)
      <OutputDir>/cf7_gate_<timestamp>_<hash>.json     (composite gate report)
      <OutputDir>/cf7_gate_<timestamp>_<hash>.md       (human-readable summary)

    B4 is loaded from the frozen CF-6 HIVE acceptance artifact; it is not re-run.
    The artifact path is .orc/cf6-acceptance/cf6-acceptance-*.json (auto-detected).
#>

[CmdletBinding()]
param(
    [string]$RepoRoot    = "",
    [string]$ModelRoot   = (Join-Path $env:APPDATA "OrchestratorIDE\Models"),
    [string]$OutputDir   = "",
    [int]   $MaxQuestions = 0,
    [int]   $Context      = 8192,
    [int]   $GpuLayers    = -1,
    [switch]$SkipBuild,
    [string]$LogFile      = "",
    # Pin role resolution to a GGUF whose filename contains this substring (e.g. "Meta-Llama").
    # Protects the run from other models being added/disabled in a shared depot mid-run.
    [string]$Model        = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
# $PSScriptRoot is not reliably populated while evaluating param() default
# values (observed empty under Windows PowerShell 5.1), so the RepoRoot
# default is resolved here in the body instead, where it is always set.
if (-not $RepoRoot) {
    $RepoRoot = Join-Path $PSScriptRoot "..\.."
}
$RepoRoot   = Resolve-Path $RepoRoot | Select-Object -ExpandProperty Path
$BenchDir   = Join-Path $RepoRoot "Tools\ContextFabricBench"
$PublishDir = Join-Path $BenchDir "publish"
$BenchExe   = Join-Path $PublishDir "context-fabric-bench.exe"

if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot ".orc\adversarial"
}

$HeldOutPath = Join-Path $RepoRoot ".orc\adversarial\expanded-question-suite-heldout.json"

# Locate the frozen B4 artifact (CF-6 acceptance JSON).
$B4ArtifactDir = Join-Path $RepoRoot ".orc\cf6-acceptance"
$B4Artifact = Get-ChildItem $B4ArtifactDir -Filter "cf6-acceptance-*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName

# ---------------------------------------------------------------------------
# Pre-flight checks
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== CF-7 Gate Expanded - Re-run Script ===" -ForegroundColor Cyan
Write-Host "Repo root  : $RepoRoot"
Write-Host "Model root : $ModelRoot"
Write-Host "Output dir : $OutputDir"
Write-Host "Questions  : $(if ($MaxQuestions -gt 0) { $MaxQuestions } else { '120 (all)' })"
Write-Host "Context    : $Context tokens"
Write-Host "GPU layers : $(if ($GpuLayers -eq -1) { 'auto' } else { $GpuLayers })"
Write-Host ""

# Held-out questions
if (-not (Test-Path $HeldOutPath)) {
    Write-Error ("Held-out question file not found: $HeldOutPath`n" +
                "Expected at .orc/adversarial/expanded-question-suite-heldout.json in the repo root.`n" +
                "This file is generated by the question-suite build process and must be present.")
    exit 1
}

# B4 artifact
if (-not $B4Artifact) {
    Write-Error ("No CF-6 acceptance artifact found in: $B4ArtifactDir`n" +
                "Expected a file matching cf6-acceptance-*.json.`n" +
                "Ensure the CF-6 HIVE acceptance run has been completed and its artifact is checked in.")
    exit 1
}
Write-Host "B4 artifact: $B4Artifact"

# Model directory
if (-not (Test-Path $ModelRoot)) {
    Write-Error ("Model root not found: $ModelRoot`n" +
                "Install a qualifying model (7B+ Admitted for CF) and ensure the directory exists.")
    exit 1
}

$GgufCount = @(Get-ChildItem $ModelRoot -Filter "*.gguf" -Recurse -ErrorAction SilentlyContinue).Count
if ($GgufCount -eq 0) {
    Write-Error ("No .gguf files found under: $ModelRoot`n" +
                "The CF gate requires at least one 7B+ Admitted model (e.g. gemma-4-12B-it-qat-q4_0.gguf).")
    exit 1
}
Write-Host "GGUF models found: $GgufCount"

# Output directory
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Write-Host ""
    Write-Host "Skipping build (--SkipBuild)." -ForegroundColor Yellow
    if (-not (Test-Path $BenchExe)) {
        Write-Error "Bench exe not found at $BenchExe and -SkipBuild was specified. Run without -SkipBuild first."
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "Building context-fabric-bench..." -ForegroundColor Cyan
    Push-Location $BenchDir
    try {
        dotnet publish ContextFabricBench.csproj -c Release -r win-x64 --self-contained false -o publish /p:DebugType=none
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet publish failed (exit $LASTEXITCODE). Fix build errors before re-running."
            exit $LASTEXITCODE
        }
    } finally {
        Pop-Location
    }
    Write-Host "Build succeeded." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Assemble command arguments
# ---------------------------------------------------------------------------
$Timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

if (-not $LogFile) {
    $label = if ($MaxQuestions -gt 0) { "smoke${MaxQuestions}" } else { "full" }
    $LogFile = Join-Path $OutputDir "cf7_expanded_${label}_${Timestamp}_console.log"
}

$Args = @(
    "--suite",            "cf7-gate-expanded",
    "--model-root",       $ModelRoot,
    "--heldout-questions",$HeldOutPath,
    "--b4-artifact",      $B4Artifact,
    "--output",           $OutputDir,
    "--context",          $Context
)

if ($MaxQuestions -gt 0) {
    $Args += @("--max-questions", $MaxQuestions)
}

if ($GpuLayers -ne -1) {
    $Args += @("--gpu-layers", $GpuLayers)
}

if ($Model) {
    $Args += @("--model", $Model)
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Starting benchmark..." -ForegroundColor Cyan
Write-Host "Command: $BenchExe $($Args -join ' ')"
if ($LogFile) {
    Write-Host "Log    : $LogFile"
}
Write-Host ""

$StartTime = Get-Date

if ($LogFile) {
    # Tee stdout to both console and log file so you can tail the log separately.
    & $BenchExe @Args 2>&1 | Tee-Object -FilePath $LogFile
} else {
    & $BenchExe @Args
}

$ExitCode  = $LASTEXITCODE
$Elapsed   = (Get-Date) - $StartTime

# ---------------------------------------------------------------------------
# Result summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Run complete ===" -ForegroundColor Cyan
Write-Host ("Elapsed : {0:hh\:mm\:ss}" -f $Elapsed)
Write-Host "Exit    : $ExitCode"

if ($ExitCode -eq 0) {
    Write-Host "Verdict : GO  -- all gate thresholds met." -ForegroundColor Green
} elseif ($ExitCode -eq 2) {
    Write-Host "Verdict : NO-GO  -- one or more thresholds were not met." -ForegroundColor Red
    Write-Host "          Review the gate JSON and markdown in: $OutputDir"
} else {
    Write-Host "Verdict : ERROR  -- the tool exited with code $ExitCode." -ForegroundColor Red
    Write-Host "          Check the log for crash details: $LogFile"
}

Write-Host ""
Write-Host "Output artifacts:" -ForegroundColor Cyan
Get-ChildItem $OutputDir -Filter "cf7_*" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 8 |
    ForEach-Object { Write-Host "  $($_.Name)  ($([math]::Round($_.Length / 1024, 1)) KB)" }

exit $ExitCode
