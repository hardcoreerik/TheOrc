# grok-review.ps1 — Grok Build (xAI) code review of git changes in this repo.
#
# Mirrors codex-review.ps1 in structure; run both via dual-review.ps1 for parallel results.
#
# Prerequisites:
#   1. Install Grok Build CLI:  irm https://x.ai/cli/install.ps1 | iex
#   2. Set API key:             $env:XAI_API_KEY = "xai-..."
#                               (or run `grok login` once for browser auth)
#
# Usage:
#   tools\grok-review.ps1                        # review HEAD~1..HEAD (latest commit)
#   tools\grok-review.ps1 -Range "a1b2..HEAD"    # specific commit range
#   tools\grok-review.ps1 -Staged                # staged changes
#   tools\grok-review.ps1 -Model grok-composer-2.5-fast  # use heavier model
#   tools\grok-review.ps1 -Focus "async safety, resource leaks"
#   tools\grok-review.ps1 -TimeoutSec 900
#
# Output: findings on stdout and in .orc\reviews\grok_<timestamp>.md. Exit codes:
#   0 = review completed   2 = timed out   3 = grok exe not found   4 = no API key
param(
    [string]$Range      = "HEAD~1..HEAD",
    [switch]$Staged,
    [string]$Focus      = "",
    [string]$Model      = "grok-build",           # swap to grok-composer-2.5-fast for heavier review
    [int]   $TimeoutSec = 600,
    [int]   $MaxDiffKB  = 512                    # Grok has 256K token ctx — generous budget
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Verify prerequisites ──────────────────────────────────────────────────────
# Check PATH first, then the default install location
$grokExe = (Get-Command grok -ErrorAction SilentlyContinue)?.Source
if (-not $grokExe) {
    $defaultPath = Join-Path $env:USERPROFILE ".grok\bin\grok.exe"
    if (Test-Path $defaultPath) { $grokExe = $defaultPath }
}
if (-not $grokExe) {
    Write-Host "grok not found — install with: irm https://x.ai/cli/install.ps1 | iex" -ForegroundColor Red
    exit 3
}

if (-not $env:XAI_API_KEY) {
    # Grok Build falls back to cached login token (~/.grok/); warn but don't abort.
    Write-Host "XAI_API_KEY not set — using cached grok login (run 'grok login' if this fails)" -ForegroundColor Yellow
}

# ── Pre-generate the diff ─────────────────────────────────────────────────────
if ($Staged) {
    $logLines = @()
    $diffRaw  = git diff --cached -- . ':!publish' ':!*.psv' ':!*.tsv' ':!*.jsonl' 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git diff --cached failed. Nothing staged?" -ForegroundColor Red
        exit 1
    }
    $statLine = git diff --cached --stat | Select-Object -Last 1
    $scopeTag = "staged changes"
} else {
    $logLines = git log --oneline "$Range" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git log failed for range '$Range'. Invalid range?" -ForegroundColor Red
        exit 1
    }
    $diffRaw  = git diff "$Range" -- . ':!publish' ':!*.psv' ':!*.tsv' ':!*.jsonl' 2>$null
    $statLine = git diff --stat "$Range" 2>$null | Select-Object -Last 1
    $scopeTag = "commit range $Range"
}

if (-not $statLine -and -not $diffRaw) {
    Write-Host "Nothing to review ($scopeTag is empty)." -ForegroundColor Yellow
    exit 0
}
$statDisplay = if ($statLine) { $statLine.Trim() } else { $scopeTag }
Write-Host "grok reviewing: $statDisplay" -ForegroundColor Magenta

# ── Build diff string with commit context ─────────────────────────────────────
$maxChars = $MaxDiffKB * 1024
$commitList = if ($logLines) { "Commits in range:`n" + ($logLines -join "`n") + "`n`n" } else { "" }
$diff = $commitList + ($diffRaw -join "`n")
$truncated = $false
if ($diff.Length -gt $maxChars) {
    $diff = $diff.Substring(0, $maxChars)
    $truncated = $true
}

# ── Context detection (mirrors codex-review.ps1) ─────────────────────────────
$branch = git branch --show-current 2>$null
$isAvalonia = $branch -like "*avalonia*" -or ($diffRaw -join "") -match "OrchestratorIDE\.Avalonia"

$conventions = if ($isAvalonia) {
@"
Conventions to enforce:
- Avalonia project targets net10.0 (no -windows suffix); WPF assemblies must not be referenced.
- #if WPF guards WPF-specific code; #if WINDOWS guards OS-only code (DPAPI, SharpAvi, etc.).
- Shared service files use <Compile Include> from the WPF project — no copies, no new files.
- SecretProtection.Initialize() must be called before any HIVE store access.
- Phase gates from Avalonia_Migration.md must not be bypassed — only flag if the gate is currently marked 🔄 or ⬜ (not ✅ Done).
- NUnit tests in OrchestratorIDE.UITests/Tests as T##_*.cs.
"@
} else {
@"
Conventions to enforce:
- Every interactive WPF control gets AutomationProperties.AutomationId (FlaUI targets them).
- NUnit tests in OrchestratorIDE.UITests/Tests as T##_*.cs.
- Commit behavior claims must be backed by the diff.
"@
}

$planNote = ""
if (Test-Path "$root\docs\ROADMAP.md")       { $planNote += "Cross-check against docs/ROADMAP.md. " }
if (Test-Path "$root\Avalonia_Migration.md") { $planNote += "Cross-check against Avalonia_Migration.md. " }

$truncNote = if ($truncated) { "`n[DIFF TRUNCATED at ${MaxDiffKB}KB]" } else { "" }
$focusNote = if ($Focus) { "`nExtra attention: $Focus`n" } else { "" }

# ── Build prompt ──────────────────────────────────────────────────────────────
# ── Write prompt to a file (--prompt-file avoids Windows arg-length limits) ───
$reviewDir  = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$timestamp  = Get-Date -Format yyyyMMdd_HHmmss
$promptFile = Join-Path $reviewDir "current_prompt.txt"
$outFile    = Join-Path $reviewDir "grok_$timestamp.md"

$prompt = @"
You are performing a code review of TheOrc (WPF/.NET 10 local AI coding assistant) on branch '$branch'.
Scope: $scopeTag.

$conventions
$planNote
$focusNote
Review in order of severity:
1. Crash risk at runtime (init order, async void, reentrancy, null deref)
2. Correctness regressions vs pre-change code
3. Fail-open vs fail-closed on security/validation paths
4. Resource leaks (undisposed objects, unclosed handles)
5. Convention violations listed above

Here is the full git diff:

$diff$truncNote

Output format — one finding per line:
  BLOCKER <file>:<line> — <issue>
  MINOR   <file>:<line> — <issue>
Or the single word CLEAN if no issues found. No other prose.
"@

$prompt | Set-Content $promptFile -Encoding utf8

# ── Run grok headlessly ───────────────────────────────────────────────────────
# Flags sourced from `grok --help` (v0.2.54):
#   --prompt-file  single-turn prompt from file (avoids arg-length limit)
#   --no-alt-screen  inline output, no TUI alternate screen (required for scripting)
#   --disallowed-tools  restrict to read-only (no file edits / shell commands)
#   --always-approve  auto-approve read tool calls without prompting
$psi = [System.Diagnostics.ProcessStartInfo]::new($grokExe)
foreach ($a in @(
    '--always-approve',
    '--no-alt-screen',
    '--output-format', 'plain',
    '--disallowed-tools', 'write_file,edit_file,create_file,run_bash,run_command',
    '-m', $Model,
    '--prompt-file', $promptFile
)) {
    $psi.ArgumentList.Add($a)
}
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false
$psi.WorkingDirectory       = $root

$p = [System.Diagnostics.Process]::Start($psi)
$stdoutTask = $p.StandardOutput.ReadToEndAsync()
$stderrTask = $p.StandardError.ReadToEndAsync()

if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill($true) } catch {}
    Write-Host "grok review TIMED OUT after ${TimeoutSec}s" -ForegroundColor Red
    Remove-Item $promptFile -ErrorAction SilentlyContinue
    exit 2
}

Remove-Item $promptFile -ErrorAction SilentlyContinue

# Non-zero exit from grok = auth failure, model error, etc. — fail loudly.
if ($p.ExitCode -ne 0) {
    $errText = $stderrTask.Result.Trim()
    Write-Host "grok exited with code $($p.ExitCode)" -ForegroundColor Red
    if ($errText) { Write-Host $errText -ForegroundColor Red }
    exit 1
}

$verdict = $stdoutTask.Result.Trim()

# Strip ANSI escape sequences from plain output
$verdict = $verdict -replace '\x1b\[[0-9;]*[mGKHF]', ''
$verdict = ($verdict -split "`n" |
    Where-Object { $_ -match '^(BLOCKER|MINOR|CLEAN)' -or $_ -match '^\[' }) -join "`n"
$verdict = $verdict.Trim()

if (-not $verdict) {
    Write-Host "grok returned no parseable findings — check raw output below:" -ForegroundColor Yellow
    Write-Host $stdoutTask.Result
    exit 1
}

@("# Grok review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Model: $Model  |  Branch: $branch  |  Scope: $scopeTag",
  "", $verdict) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
