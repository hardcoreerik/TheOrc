# Run this from a PowerShell window inside the RDP session on the target machine.
# Repo must be synced to fix/nokvslot-cache-exhaustion (commit 28f881fc or later -- the
# force-recycle-on-NoKvSlot fix) on both machines before running this.

param(
    [Parameter(Mandatory)]
    [string]$RepoPath,
    [int]$MaxQuestions = 3,
    # Defaults to the dedicated CF-7 root (hardlinked Meta-Llama) when present, because
    # models in the shared depot keep getting toggled to .gguf.disabled mid-evening,
    # which fails preflight with exit 66. Falls back to the standard depot.
    [string]$ModelRoot = ""
)

if (-not $ModelRoot) {
    $cf7Root = Join-Path $env:APPDATA "OrchestratorIDE\Models-CF7"
    $ModelRoot = if (Test-Path $cf7Root) { $cf7Root } else { Join-Path $env:APPDATA "OrchestratorIDE\Models" }
}

Set-Location $RepoPath

Write-Host "=== Repo state ===" -ForegroundColor Cyan
git branch --show-current
git log --oneline -1

Write-Host "`n=== Running CF-7 gate ($MaxQuestions questions, KV-cache diagnostics ON) ===" -ForegroundColor Cyan
$logPath = Join-Path $RepoPath "cf7-run-$MaxQuestions.log"
$env:THEORC_KVCACHE_DIAGNOSTICS = "1"
Write-Host "Model root: $ModelRoot"
& .\Tools\ContextFabricBench\Run-CF7GateExpanded.ps1 -MaxQuestions $MaxQuestions -ModelRoot $ModelRoot *>&1 | Tee-Object -FilePath $logPath
Remove-Item Env:\THEORC_KVCACHE_DIAGNOSTICS -ErrorAction SilentlyContinue

Write-Host "`n=== Result summary ===" -ForegroundColor Cyan
if (Select-String -Path $logPath -Pattern "NoKvSlot" -Quiet) {
    Write-Host "NoKvSlot occurred -- see lines below:" -ForegroundColor Red
    Select-String -Path $logPath -Pattern "NoKvSlot" -Context 1,1
} else {
    Write-Host "No NoKvSlot occurrences found." -ForegroundColor Green
}
Write-Host "`n=== Force-recycle diagnostic lines ===" -ForegroundColor Cyan
Select-String -Path $logPath -Pattern "KvCacheDiag" | Select-Object -First 40
Write-Host "`n=== Verdict ===" -ForegroundColor Cyan
Select-String -Path $logPath -Pattern "GO|NO-GO|=== Run complete|Error|questions \d+/\d+" | Select-Object -Last 15
Write-Host "`nFull log: $logPath" -ForegroundColor DarkGray
