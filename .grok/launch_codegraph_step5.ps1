#!/usr/bin/env pwsh
Set-Location "F:\Ai\OrchestratorIDE-dev"
$grokExe = "$env:USERPROFILE\.grok\bin\grok.exe"
$env:GITHUB_TOKEN = (gh auth token 2>$null)
Write-Host "=== Grok CodeGraph v1 Step 5 ===" -ForegroundColor Cyan
Write-Host "  Wire GraphTools into AgentLoop registration"
Write-Host ""
& $grokExe --always-approve --no-alt-screen --prompt-file "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step5_prompt.txt" *>&1 |
    Tee-Object "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step5_run.log"
Write-Host ""
Write-Host "=== Grok Step 5 exit: $LASTEXITCODE ===" -ForegroundColor Cyan
