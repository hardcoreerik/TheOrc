# codex-task.ps1 — scripted, non-interactive Codex CLI task dispatch (write-enabled).
#
# Sibling to codex-review.ps1, which is read-only by design (it only ever reviews a diff).
# This script is for actual coding/writing tasks — uses --sandbox workspace-write so Codex
# can create/edit files, not just read them.
#
# Solves the same two problems as codex-review.ps1:
#   1. stdin hang — process is started with stdin closed immediately after writing the prompt
#   2. npm shim deadlock — the native codex.exe is located directly, not via the npm wrapper
#
# Usage:
#   tools\codex-task.ps1 -Prompt "Do X, Y, Z..."
#   tools\codex-task.ps1 -PromptFile path\to\task.txt
#   tools\codex-task.ps1 -Prompt "..." -TimeoutSec 3600 -Sandbox workspace-write
#
# Output: Codex's final message on stdout and in .orc\tasks\codex_<timestamp>.md.
# Exit codes: 0 = completed   2 = timed out   3 = codex exe not found   4 = no prompt given
param(
    [string]$Prompt      = "",
    [string]$PromptFile  = "",
    [int]   $TimeoutSec  = 1800,
    [ValidateSet("read-only", "workspace-write", "danger-full-access")]
    [string]$Sandbox     = "workspace-write"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if ($PromptFile) {
    if (-not (Test-Path $PromptFile)) {
        Write-Host "PromptFile not found: $PromptFile" -ForegroundColor Red
        exit 4
    }
    $Prompt = Get-Content $PromptFile -Raw
}
if (-not $Prompt) {
    Write-Host "No prompt given. Pass -Prompt '...' or -PromptFile path.txt" -ForegroundColor Red
    exit 4
}

# ── Locate the native codex.exe (npm shim deadlocks under redirected stdio) ──
$exe = Get-ChildItem "$env:APPDATA\npm\node_modules\@openai\codex" -Recurse `
        -Filter "codex.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { $exe = (Get-Command codex.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $exe) { $exe = "$env:USERPROFILE\.codex\.sandbox-bin\codex.exe" }
if (-not (Test-Path $exe)) {
    Write-Host "codex.exe not found — install with: npm i -g @openai/codex" -ForegroundColor Red
    exit 3
}

$taskDir = Join-Path $root ".orc\tasks"
New-Item -ItemType Directory -Force $taskDir | Out-Null
$outFile = Join-Path $taskDir "codex_$(Get-Date -Format yyyyMMdd_HHmmss).md"

Write-Host "dispatching codex task (sandbox=$Sandbox, timeout=${TimeoutSec}s)..." -ForegroundColor Cyan

$psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
# '-' tells codex exec to read the prompt from stdin (avoids Windows 32K cmd-line limit)
foreach ($a in @('exec', '--sandbox', $Sandbox, '-C', $root,
                 '--output-last-message', "$outFile.last", '-')) {
    $psi.ArgumentList.Add($a)
}
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false

$p = [System.Diagnostics.Process]::Start($psi)
try {
    $p.StandardInput.Write($Prompt)   # write prompt via stdin (no cmd-line length limit)
    $p.StandardInput.Close()          # EOF tells codex stdin is done
    $stdoutTask = $p.StandardOutput.ReadToEndAsync()
    $stderrTask = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill($true) } catch {}
        Write-Host "codex task TIMED OUT after ${TimeoutSec}s" -ForegroundColor Red
        exit 2
    }

    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
}
finally {
    $p.Dispose()
}

# Prefer the clean last-message file; fall back to raw stdout
$result = if (Test-Path "$outFile.last") {
    Get-Content "$outFile.last" -Raw
} else {
    $stdout
}
Remove-Item "$outFile.last" -ErrorAction SilentlyContinue

# stderr is captured and saved even when $result is non-empty, not just on failure -- an empty
# result with non-empty stderr (or vice versa) is itself a useful diagnostic signal that was
# previously discarded.
@("# Codex task — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Sandbox: $Sandbox", "",
  "## Prompt", "", $Prompt, "",
  "## Result", "", $result.Trim(), "",
  "## Stderr", "", $(if ($stderr.Trim()) { $stderr.Trim() } else { "(empty)" })) |
  Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $result.Trim()
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
