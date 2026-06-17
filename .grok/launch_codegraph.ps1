#!/usr/bin/env pwsh
# Launcher for Grok CodeGraph v1 implementation run
Set-Location "F:\Ai\OrchestratorIDE-dev"

$grokExe  = "$env:USERPROFILE\.grok\bin\grok.exe"
$promptFile = "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_implement_prompt.txt"
$log      = "F:\Ai\OrchestratorIDE-dev\.grok\codegraph_run.log"

# Inject GitHub token so Grok MCP can use it
$env:GITHUB_TOKEN = (gh auth token 2>$null)

Write-Host "=== Grok CodeGraph v1 implement ===" -ForegroundColor Cyan
Write-Host "  prompt : $promptFile"
Write-Host "  log    : $log"
Write-Host "  grok   : $grokExe"
Write-Host ""

& $grokExe --always-approve --no-alt-screen --prompt-file $promptFile *>&1 | Tee-Object -FilePath $log

Write-Host ""
Write-Host "=== Grok exit: $LASTEXITCODE ===" -ForegroundColor Cyan
