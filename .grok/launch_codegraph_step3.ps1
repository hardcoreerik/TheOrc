#!/usr/bin/env pwsh
Set-Location "F:\Ai\OrchestratorIDE-dev"
$grokExe = "$env:USERPROFILE\.grok\bin\grok.exe"
$env:GITHUB_TOKEN = (gh auth token 2>$null)
Write-Host "=== Grok CodeGraph v1 Step 3 ===" -ForegroundColor Cyan
Write-Host "  ComplexityAnalyzer + trace_path + get_architecture"
Write-Host ""
& $grokExe --always-approve --no-alt-screen --prompt-file "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step3_prompt.txt" *>&1 |
    Tee-Object "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_step3_run.log"
Write-Host ""
Write-Host "=== Grok Step 3 exit: $LASTEXITCODE ===" -ForegroundColor Cyan
