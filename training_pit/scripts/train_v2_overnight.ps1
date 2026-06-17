# train_v2_overnight.ps1
# Launches the ORC ACADEMY v2 boss LoRA run (1,784 train / 200 eval) and tees
# all output to a timestamped log. Designed for an unattended overnight run on
# the RTX 5070 Ti (full GPU; ~5 h at 3 epochs).
#
# Usage:
#   pwsh training_pit/scripts/train_v2_overnight.ps1            # full run
#   pwsh training_pit/scripts/train_v2_overnight.ps1 -DryRun    # load + 1 step, then exit
#   pwsh training_pit/scripts/train_v2_overnight.ps1 -Epochs 2  # shorter run
#
# Progress is also streamed to training_pit/outputs/lora_v2/progress.json
# (the Forge panel polls this; a stale mtime while the process lives = hung).

param(
    [int]   $Epochs  = 3,
    [double]$VramCap = 0,      # 0 = full GPU; set e.g. 13 to coexist with Ollama
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = "F:\Ai\OrchestratorIDE-dev"
Set-Location $RepoRoot

# Dataset key "v2gold" — matches the Training Pit registry pairing convention,
# so the SAME files launch from the Forge dropdown (the intended GUI workflow).
$Train = "training_pit/datasets/train_v2gold.jsonl"
$Eval  = "training_pit/datasets/eval_v2gold.jsonl"
$Out   = "training_pit/outputs/lora_v2/adapter"

$LogDir = "training_pit/outputs/lora_v2"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$Log   = Join-Path $LogDir "train_v2_$Stamp.log"

$Args = @(
    "training_pit/scripts/train_lora.py",
    "--train", $Train,
    "--eval",  $Eval,
    "--out",   $Out,
    "--epochs", $Epochs,
    "--vram-cap", $VramCap
)
if ($DryRun) { $Args += "--dry-run" }

Write-Host "=== ORC ACADEMY v2 LoRA ===" -ForegroundColor Cyan
Write-Host "  train : $Train"
Write-Host "  eval  : $Eval"
Write-Host "  out   : $Out"
Write-Host "  epochs: $Epochs   vram-cap: $VramCap   dry-run: $DryRun"
Write-Host "  log   : $Log"
Write-Host ""

# Tee stdout+stderr to both console and the log file.
python @Args 2>&1 | Tee-Object -FilePath $Log
$code = $LASTEXITCODE
Write-Host ""
Write-Host "=== exit code: $code  (log: $Log) ===" -ForegroundColor Cyan
exit $code
