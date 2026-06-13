# codex-review.ps1 — scripted Codex CLI review of git changes in this repo.
#
# Solves the "codex exec hangs forever" problem: when stdin is a redirected
# pipe (any scripted invocation), codex waits for stdin EOF before starting
# ("Reading additional input from stdin..."). We start the native exe with a
# redirected stdin and close it immediately, enforce a hard timeout, and save
# the verdict to .orc\reviews\.
#
# Usage:
#   tools\codex-review.ps1                          # review staged changes
#   tools\codex-review.ps1 -Range "HEAD~1..HEAD"    # review a commit range
#   tools\codex-review.ps1 -Range "a1b2c3..HEAD" -Focus "WPF bindings and async voids"
#   tools\codex-review.ps1 -TimeoutSec 900
#
# Output: findings ("BLOCKER file:line — issue" / "MINOR ..." / "CLEAN") on
# stdout and in .orc\reviews\codex_<timestamp>.md. Exit codes:
#   0 = review completed   2 = timed out   3 = codex exe not found
param(
    [string]$Range      = "",      # git range; empty = staged changes
    [string]$Focus      = "",      # optional extra reviewer attention
    [int]   $TimeoutSec = 600
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

# ── Describe what to review ───────────────────────────────────────────────────
$what = if ($Range) {
    "the git commit range $Range (run: git log -p $Range -- . ':!publish' ':!*.psv' ':!*.tsv')"
} else {
    "the STAGED git diff (run: git diff --cached)"
}
$stat = if ($Range) { git diff --stat "$Range" 2>$null | Select-Object -Last 1 }
        else        { git diff --cached --stat  | Select-Object -Last 1 }
if (-not $stat) {
    Write-Host "Nothing to review ($(if ($Range) {"empty range $Range"} else {'no staged changes'}))." -ForegroundColor Yellow
    exit 0
}
Write-Host "reviewing: $($stat.Trim())" -ForegroundColor Cyan

$prompt = @"
You are reviewing changes in this repository (TheOrc — WPF/.NET 10 local AI
coding assistant). Review $what. Ignore generated noise (bin/obj, publish/,
training_pit batch psv/tsv data files).

Repo conventions to enforce:
- Every interactive/testable WPF control gets AutomationProperties.AutomationId
  (FlaUI test suite targets them).
- NUnit tests live in OrchestratorIDE.UITests/Tests as T##_*.cs; pure-logic
  tests must not require FlaUI or a live model.
- Commit behavior claims (e.g. "byte-identical", "verified") must be backed by
  the diff.

Check, in order of severity: crash risk at runtime (binding failures, init
order, async void exception paths, reentrancy), correctness regressions vs the
pre-change code, fail-open vs fail-closed on validation paths, resource leaks,
then convention violations.
$(if ($Focus) { "Extra attention: $Focus" })

Output format: one line per finding —
"BLOCKER <file>:<line> — <issue>" or "MINOR <file>:<line> — <issue>" —
or the single word CLEAN. No other prose.
"@

# ── Run codex with closed stdin + hard timeout ────────────────────────────────
$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$outFile = Join-Path $reviewDir "codex_$(Get-Date -Format yyyyMMdd_HHmmss).md"

$psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
foreach ($a in @('exec', '--sandbox', 'read-only', '-C', $root,
                 '--output-last-message', "$outFile.last", $prompt)) {
    $psi.ArgumentList.Add($a)
}
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false

$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.Close()                       # ← the fix: codex waits for stdin EOF
$stdout = $p.StandardOutput.ReadToEndAsync()
$stderr = $p.StandardError.ReadToEndAsync()

if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill($true) } catch {}
    Write-Host "codex review TIMED OUT after ${TimeoutSec}s" -ForegroundColor Red
    exit 2
}

# Prefer the clean last-message file; fall back to parsing stdout
$verdict = if (Test-Path "$outFile.last") {
    Get-Content "$outFile.last" -Raw
} else {
    $stdout.Result
}
Remove-Item "$outFile.last" -ErrorAction SilentlyContinue

@("# Codex review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Scope: $(if ($Range) { $Range } else { 'staged changes' })",
  "", $verdict.Trim()) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict.Trim()
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
