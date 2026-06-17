#!/usr/bin/env pwsh
Set-Location "F:\Ai\OrchestratorIDE-dev"
$grokExe = "$env:USERPROFILE\.grok\bin\grok.exe"
$env:GITHUB_TOKEN = (gh auth token 2>$null)
Write-Host "=== Grok CodeGraph v1 Step 2 ===" -ForegroundColor Cyan
Write-Host "  prompt: .grok\codegraph_step2_prompt.txt"
Write-Host "  log   : .grok\codegraph_step2_run.log"
Write-Host ""
& $grokExe --always-approve --no-alt-screen --prompt-file "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step2_prompt.txt" *>&1 |
    Tee-Object "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step2_run.log"
Write-Host ""
Write-Host "=== Grok Step 2 exit: $LASTEXITCODE ===" -ForegroundColor Cyan
