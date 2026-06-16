# codex-review.ps1 — scripted Codex CLI review of git changes in this repo.
#
# Solves TWO problems with the naive "codex exec $prompt" approach:
#   1. stdin hang — process is started with stdin closed immediately
#   2. "Unable to access git range" — Codex cannot run git in --sandbox read-only;
#      the diff is pre-generated here in PowerShell and embedded in the prompt
#
# Usage:
#   tools\codex-review.ps1                       # review HEAD~1..HEAD (latest commit)
#   tools\codex-review.ps1 -Range "a1b2..HEAD"   # specific commit range
#   tools\codex-review.ps1 -Staged               # staged changes instead of a commit
#   tools\codex-review.ps1 -Focus "async init order, resource leaks"
#   tools\codex-review.ps1 -TimeoutSec 900
#
# Output: findings ("BLOCKER file:line — issue" / "MINOR ..." / "CLEAN") on
# stdout and in .orc\reviews\codex_<timestamp>.md. Exit codes:
#   0 = review completed   2 = timed out   3 = codex exe not found
param(
    [string]$Range      = "HEAD~1..HEAD",  # default = latest commit
    [switch]$Staged,                        # override: review staged changes
    [string]$Focus      = "",              # optional extra reviewer attention
    [int]   $TimeoutSec = 600,
    [int]   $MaxDiffKB  = 256              # truncate diff if it exceeds this size
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Locate the native codex.exe (npm shim deadlocks under redirected stdio) ──
$exe = Get-ChildItem "$env:APPDATA\npm\node_modules\@openai\codex" -Recurse `
        -Filter "codex.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { $exe = (Get-Command codex.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $exe) {
    Write-Host "codex.exe not found — install with: npm i -g @openai/codex" -ForegroundColor Red
    exit 3
}

# ── Pre-generate the diff (Codex can't run git in --sandbox read-only) ────────
# Use `git diff` for the net tree-to-tree patch (avoids per-commit intermediate
# hunks from `git log -p` that confuse multi-commit reviews) and prepend a short
# commit list from `git log --oneline` for context.
if ($Staged) {
    $logLines = @()
    $diffRaw  = git diff --cached -- . ':!publish' ':!*.psv' ':!*.tsv' ':!*.jsonl' 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git diff --cached failed (exit $LASTEXITCODE). Nothing staged?" -ForegroundColor Red
        exit 1
    }
    $statLine = git diff --cached --stat | Select-Object -Last 1
    $scopeTag = "staged changes"
} else {
    $logLines = git log --oneline "$Range" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git log failed for range '$Range' (exit $LASTEXITCODE). Invalid range?" -ForegroundColor Red
        exit 1
    }
    # Net tree-to-tree diff — correct for any range length, no intermediate hunks.
    $diffRaw  = git diff "$Range" -- . ':!publish' ':!*.psv' ':!*.tsv' ':!*.jsonl' 2>$null
    $statLine = git diff --stat "$Range" 2>$null | Select-Object -Last 1
    $scopeTag = "commit range $Range"
}

if (-not $statLine -and -not $diffRaw) {
    Write-Host "Nothing to review ($scopeTag is empty)." -ForegroundColor Yellow
    exit 0
}
$statDisplay = if ($statLine) { $statLine.Trim() } else { $scopeTag }
Write-Host "reviewing: $statDisplay" -ForegroundColor Cyan

# Truncate if the diff is very large so we don't blow the context window
$maxChars = $MaxDiffKB * 1024
$commitList = if ($logLines) { "Commits in range:`n" + ($logLines -join "`n") + "`n`n" } else { "" }
$diff = $commitList + ($diffRaw -join "`n")
$truncated = $false
if ($diff.Length -gt $maxChars) {
    $diff = $diff.Substring(0, $maxChars)
    $truncated = $true
}

# ── Auto-detect relevant plan/roadmap files on this branch ────────────────────
$planFiles = @()
if (Test-Path "$root\docs\ROADMAP.md")        { $planFiles += "docs/ROADMAP.md" }
if (Test-Path "$root\Avalonia_Migration.md")  { $planFiles += "Avalonia_Migration.md" }

$planNote = if ($planFiles.Count -gt 0) {
    $list = $planFiles -join ", "
    "Cross-check the diff against the current phase gates and intentions in: $list (read these files)."
} else { "" }

# ── Detect context (Avalonia branch vs WPF-only) ─────────────────────────────
$branch = git branch --show-current 2>$null
$isAvalonia = $branch -like "*avalonia*" -or ($diffRaw -join "") -match "OrchestratorIDE\.Avalonia"

$conventions = if ($isAvalonia) {
@"
Conventions to enforce:
- Avalonia project targets net10.0 (no -windows suffix); WPF assemblies must not be referenced.
- #if WPF guards WPF-specific code; #if WINDOWS guards OS-only code (DPAPI, SharpAvi, etc.).
- Shared service files use <Compile Include> from the WPF project — no copies, no new files.
- SecretProtection.Initialize() must be called before any HIVE store access (App.axaml.cs init).
- Phase gates from Avalonia_Migration.md must not be bypassed — no panel code in Phase 1.
- NUnit tests live in OrchestratorIDE.UITests/Tests as T##_*.cs.
"@
} else {
@"
Conventions to enforce:
- Every interactive/testable WPF control gets AutomationProperties.AutomationId (FlaUI targets them).
- NUnit tests live in OrchestratorIDE.UITests/Tests as T##_*.cs; pure-logic tests must not require FlaUI or a live model.
- Commit behavior claims (e.g. "byte-identical", "verified") must be backed by the diff.
"@
}

# ── Build the prompt with the diff embedded ────────────────────────────────────
$truncNote = if ($truncated) { "`n[DIFF TRUNCATED at ${MaxDiffKB}KB — review visible portion only]" } else { "" }

$prompt = @"
You are reviewing changes in TheOrc (WPF/.NET 10 local AI coding assistant on branch '$branch').
Scope: $scopeTag.

$conventions
$planNote

Check in order of severity:
1. Crash risk at runtime (init order, async void exception paths, reentrancy, null deref)
2. Correctness regressions vs pre-change code
3. Fail-open vs fail-closed on security/validation paths
4. Resource leaks (undisposed objects, unclosed handles)
5. Convention violations listed above

$(if ($Focus) { "Extra attention: $Focus`n" })
Here is the full diff to review:

$diff$truncNote

Output format: one finding per line —
  BLOCKER <file>:<line> — <issue>
  MINOR   <file>:<line> — <issue>
or the single word CLEAN if nothing is found. No other prose.
"@

# ── Run codex with closed stdin + hard timeout ─────────────────────────────────
$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$outFile = Join-Path $reviewDir "codex_$(Get-Date -Format yyyyMMdd_HHmmss).md"

$psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
# '-' tells codex exec to read the prompt from stdin (avoids Windows 32K cmd-line limit)
foreach ($a in @('exec', '--sandbox', 'read-only', '-C', $root,
                 '--output-last-message', "$outFile.last", '-')) {
    $psi.ArgumentList.Add($a)
}
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false

$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Write($prompt)   # write prompt via stdin (no cmd-line length limit)
$p.StandardInput.Close()          # EOF tells codex stdin is done
$stdout = $p.StandardOutput.ReadToEndAsync()
$stderr = $p.StandardError.ReadToEndAsync()

if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill($true) } catch {}
    Write-Host "codex review TIMED OUT after ${TimeoutSec}s" -ForegroundColor Red
    exit 2
}

# Prefer the clean last-message file; fall back to raw stdout
$verdict = if (Test-Path "$outFile.last") {
    Get-Content "$outFile.last" -Raw
} else {
    $stdout.Result
}
Remove-Item "$outFile.last" -ErrorAction SilentlyContinue

@("# Codex review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Branch: $branch  |  Scope: $scopeTag",
  "", $verdict.Trim()) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict.Trim()
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
