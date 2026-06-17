#!/usr/bin/env pwsh
Set-Location "F:\Ai\OrchestratorIDE-dev"
$grokExe = "$env:USERPROFILE\.grok\bin\grok.exe"
$env:GITHUB_TOKEN = (gh auth token 2>$null)
Write-Host "=== Grok — Training Pit adversarial gap analysis ===" -ForegroundColor Cyan
Write-Host "  Read-only; writes .grok/TRAINING_PIT_GAP_ANALYSIS.md"
Write-Host ""
& $grokExe --always-approve --no-alt-screen --prompt-file "F:\Ai\OrchestratorIDE-dev\.grok\training_pit_gap_analysis_prompt.txt" *>&1 |
    Tee-Object "F:\Ai\OrchestratorIDE-dev\.grok\gap_analysis_run.log"
Write-Host ""
Write-Host "=== Grok gap analysis exit: $LASTEXITCODE ===" -ForegroundColor Cyan
