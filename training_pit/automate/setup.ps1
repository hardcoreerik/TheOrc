<#
.SYNOPSIS
    One-time setup for HARDCOREPC automate suite.
    Run this once before using menu.cmd.
#>
$ErrorActionPreference = "Stop"

Write-Host "`n[SETUP] ORC ACADEMY — HARDCOREPC Automate Setup"
Write-Host "================================================`n"

# 1. Check Python
Write-Host "[1/6] Checking Python..."
try {
    $pyVer = python --version 2>&1
    Write-Host "  OK: $pyVer"
} catch {
    Write-Error "Python not found. Install Python 3.10+ from python.org"
    exit 1
}

# 2. Check Ollama
Write-Host "[2/6] Checking Ollama..."
try {
    $resp = Invoke-RestMethod "http://localhost:11434/api/tags" -TimeoutSec 5
    $models = $resp.models | Select-Object -ExpandProperty name
    Write-Host "  OK: Ollama running. Models: $($models -join ', ')"
} catch {
    Write-Warning "  Ollama not running on localhost:11434. Start Ollama first."
}

# 3. Check ML Python packages
Write-Host "[3/6] Checking ML packages..."
$pkgs = @("torch", "transformers", "peft", "trl", "bitsandbytes", "datasets")
$missing = @()
foreach ($pkg in $pkgs) {
    $result = python -c "import $pkg; print($pkg.__version__)" 2>&1
    if ($LASTEXITCODE -ne 0) {
        $missing += $pkg
        Write-Host "  MISSING: $pkg"
    } else {
        Write-Host "  OK: $pkg $result"
    }
}
if ($missing.Count -gt 0) {
    Write-Host "`n  Installing missing packages..."
    python -m pip install $missing
}

# 4. Create local workspace dirs
Write-Host "[4/6] Creating local workspace..."
$dirs = @(
    "C:\OrcWork",
    "C:\OrcWork\goals",
    "C:\OrcWork\synthetic_sft",
    "C:\OrcWork\adapters",
    "C:\OrcWork\eval_results"
)
foreach ($d in $dirs) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    Write-Host "  OK: $d"
}

# 5. Test main machine connectivity
Write-Host "[5/6] Testing main machine connection..."
$mainHost = "http://100.102.190.112:11434"
try {
    $resp = Invoke-RestMethod "$mainHost/api/tags" -TimeoutSec 5
    Write-Host "  OK: Main Ollama reachable at $mainHost"
    $bossModels = $resp.models | Where-Object { $_.name -like "*theorc*" -or $_.name -like "*gemma*" }
    if ($bossModels) {
        Write-Host "  Boss models: $($bossModels.name -join ', ')"
    } else {
        Write-Warning "  No theorc-boss model found on main machine"
    }
} catch {
    Write-Warning "  Main machine not reachable at $mainHost (check VPN/network)"
}

# 6. Test workspace access
Write-Host "[6/6] Testing workspace access..."
$ws = "\\HARDCORERIK\F$\Ai\OrchestratorIDE"
if (Test-Path $ws) {
    Write-Host "  OK: Workspace accessible at $ws"
} else {
    Write-Warning "  Workspace not accessible at $ws"
    Write-Host "  To map: net use \\HARDCORERIK\F$ /user:HARDCORERIK\<username>"
}

Write-Host "`n[SETUP] Complete. Edit config.env if any IPs/paths need updating."
Write-Host "        Run menu.cmd to start."
