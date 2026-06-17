#!/usr/bin/env pwsh
Set-Location "F:\Ai\OrchestratorIDE-dev"
$grokExe = "$env:USERPROFILE\.grok\bin\grok.exe"
$env:GITHUB_TOKEN = (gh auth token 2>$null)
Write-Host "=== Grok — review of gap-analysis fixes ===" -ForegroundColor Cyan
Write-Host "  Read-only; writes .grok/FIX_REVIEW.md"
Write-Host ""
& $grokExe --always-approve --no-alt-screen --prompt-file "F:\Ai\OrchestratorIDE-dev\.grok\fix_review_prompt.txt" *>&1 |
    Tee-Object "F:\Ai\OrchestratorIDE-dev\.grok\fix_review_run.log"
Write-Host ""
Write-Host "=== Grok fix-review exit: $LASTEXITCODE ===" -ForegroundColor Cyan
