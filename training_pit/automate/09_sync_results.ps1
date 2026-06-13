param(
    [string] $LocalWorkspace = "C:\OrcWork",
    [string] $MainWorkspace  = "\\HARDCORERIK\F$\Ai\OrchestratorIDE",
    [switch] $DryRun
)
$ErrorActionPreference = "Continue"

$robocopyFlags = @("/E", "/NP", "/R:2", "/W:5")
if ($DryRun) { $robocopyFlags += "/L" }

Write-Host "`n[SYNC] HARDCOREPC -> Main Machine"
Write-Host "  Local:  $LocalWorkspace"
Write-Host "  Main:   $MainWorkspace"
if ($DryRun) { Write-Host "  ** DRY RUN (no files copied) **" }
Write-Host ""

function Sync($src, $dst, $label) {
    if (-not (Test-Path $src)) {
        Write-Host "  [$label] SKIP — source not found: $src"
        return
    }
    Write-Host "  [$label] Syncing $src -> $dst"
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    $result = robocopy $src $dst @robocopyFlags
    $rc = $LASTEXITCODE
    if ($rc -le 7) {
        Write-Host "  [$label] OK (robocopy exit $rc)"
    } else {
        Write-Host "  [$label] WARNING: robocopy exit $rc (check output above)"
    }
}

# 1. Sync synthetic SFT examples generated on HARDCOREPC
Sync (Join-Path $LocalWorkspace "synthetic_sft")  `
     (Join-Path $MainWorkspace  "training_pit\datasets\staging\synthetic_hc") `
     "Synthetic SFT"

# 2. Sync goals batches generated on HARDCOREPC
Sync (Join-Path $LocalWorkspace "goals") `
     (Join-Path $MainWorkspace  "training_pit") `
     "Goals batches"

# 3. Sync trained 7B adapter (if any)
Sync (Join-Path $LocalWorkspace "adapters") `
     (Join-Path $MainWorkspace  "training_pit\adapters\imported") `
     "7B adapters"

# 4. Sync eval results from HARDCOREPC subset eval
Sync (Join-Path $LocalWorkspace "eval_results") `
     (Join-Path $MainWorkspace  "training_pit\outputs\hc_eval") `
     "Eval results"

Write-Host ""
Write-Host "[SYNC] Done. Review new files on main machine with review_captures.py"
