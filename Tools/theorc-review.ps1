# theorc-review.ps1 — TheOrc's own scripted code review, modeled on
# tools/codex-review.ps1. Sends a structured review prompt to an Ollama
# model and saves the verdict to .orc\reviews\theorc_<timestamp>.md.
#
# Default reviewer model is qwen2.5-coder:14b — strong code understanding,
# fits in 16 GB VRAM, and is the planned base for the future
# theorc-reviewer:v1 adapter. Override with -Model.
#
# Usage:
#   tools\theorc-review.ps1                          # review staged changes
#   tools\theorc-review.ps1 -Range "HEAD~1..HEAD"    # review a commit range
#   tools\theorc-review.ps1 -Range "a1b2c3..HEAD" -Focus "WPF bindings"
#   tools\theorc-review.ps1 -Model qwen2.5-coder:14b -TimeoutSec 600
#
# Output: findings ("BLOCKER file:line — issue" / "MINOR ..." / "CLEAN") on
# stdout and in .orc\reviews\theorc_<timestamp>.md. Exit codes:
#   0 = review completed
#   3 = ollama call failed for any reason (unreachable, HTTP error, timeout)
param(
    [string]$Range      = "",      # git range; empty = staged changes
    [string]$Focus      = "",      # optional extra reviewer attention
    [string]$Model      = "qwen2.5-coder:14b",
    [string]$OllamaHost = "http://localhost:11434",
    [int]   $TimeoutSec = 600
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Describe what to review (same shape as codex-review.ps1 so a future
#    judge script can diff the two verdicts on a level playing field) ────
$what = if ($Range) {
    "the git commit range $Range"
} else {
    "the STAGED git diff"
}
$stat = if ($Range) { git diff --stat "$Range" 2>$null | Select-Object -Last 1 }
        else        { git diff --cached --stat  | Select-Object -Last 1 }
if (-not $stat) {
    Write-Host "Nothing to review ($(if ($Range) {"empty range $Range"} else {'no staged changes'}))." -ForegroundColor Yellow
    exit 0
}
Write-Host "reviewing: $($stat.Trim())  (model: $Model)" -ForegroundColor Cyan

# ── Gather the actual diff text (capped so we don't blow the model's context) ──
$diffArgs = if ($Range) {
    @("log", "-p", "$Range", "--", ".", ":!publish", ":!*.psv", ":!*.tsv", ":!*.jsonl")
} else {
    @("diff", "--cached")
}
$diffText = & git @diffArgs 2>&1 | Out-String

# Soft cap at 100 KB — qwen2.5-coder:14b has a 32K context (~128 KB chars).
# 100 KB of diff leaves comfortable room for the prompt + a multi-finding
# reply. Larger diffs get truncated with a clear marker so the model
# knows it's incomplete.
$maxDiffBytes = 100000
if ($diffText.Length -gt $maxDiffBytes) {
    $diffText = $diffText.Substring(0, $maxDiffBytes) + "`n`n[... diff truncated at $maxDiffBytes chars ...]"
    Write-Host "(diff truncated to $maxDiffBytes chars to fit model context)" -ForegroundColor DarkYellow
}

$prompt = @"
You are reviewing changes in a WPF/.NET 10 local AI coding assistant
called TheOrc. Review $what.

You MUST fill in the semi-formal certificate below in order. Do not skip
sections. Do not output "CLEAN" until every preceding section has been
filled in with evidence from the diff. The template is adapted from Meta's
Agentic Code Reasoning protocol (arxiv 2603.01896) — structure forces
evidence before conclusion and prevents the "looks fine, ship it" shortcut.

Repo conventions in scope:
- Every interactive/testable WPF control gets AutomationProperties.AutomationId
  (FlaUI test suite targets them)
- NUnit tests live in OrchestratorIDE.UITests/Tests as T##_*.cs; pure-logic
  tests must not require FlaUI or a live model
- Commit behavior claims ("byte-identical", "verified", etc.) must be backed
  by the diff
$(if ($Focus) { "- Extra attention from user: $Focus" })

Severity definitions:
- BLOCKER: runtime crash, correctness regression, data loss, security hole,
  or breaks an explicit contract (parameter name mismatch, public API drift
  with callers in this diff)
- MINOR: convention violation, doc/code drift, missed mutual-exclusion check,
  resource-leak risk, future-proofing concern

Fill in the certificate now:

=== SEMI-FORMAL CERTIFICATE ===

DEFINITIONS:
  D1: A change is CORRECT iff executable semantics and repo conventions hold
      after the change is applied.
  D2: An issue is BLOCKER iff it satisfies the BLOCKER severity above.
  D3: An issue is MINOR iff it satisfies the MINOR severity above.

PREMISES (what does this diff actually do?):
  P1: Files modified — [list each file with the substantive line ranges]
  P2: New functions, classes, or API surface added — [list with signatures]
  P3: Renamed or restructured public surface — [list, mark callers in diff]

IMPORTS AND DEPENDENCIES:
  For each modified file:
    - Cross-references introduced or changed (caller -> callee)
    - Parameter names or signatures changed (CRITICAL — check every caller)
    - Public surface changes touched by other files in this diff

EXECUTION TRACE (per substantive change):
  For each substantive change:
    - Function: [name] at [file:line]
    - Pre-change behavior: [trace what it did]
    - Post-change behavior: [trace what it does now]
    - Divergence: [SAME / DIFFERENT — for what input class]

CONVENTION CHECKS:
  - New interactive WPF controls have AutomationProperties.AutomationId? [YES / NO at file:line / N/A]
  - NUnit tests follow T##_*.cs naming? [YES / NO / N/A]
  - Commit-message behavior claims supported by the diff? [YES / NO / N/A]
  - Mutual-exclusion checks symmetric (if A blocks B, does B block A)? [YES / NO / N/A]

FINDINGS (zero or more — each must cite a premise or trace entry above):
  BLOCKER <file>:<line> — <issue>, justified by <P# or trace entry>
  MINOR   <file>:<line> — <issue>, justified by <P# or trace entry>

FORMAL CONCLUSION:
  By D1, with [N BLOCKERs and M MINORs found / no traced path producing
  divergent behavior], this diff is [INCORRECT / CORRECT].

=== END CERTIFICATE ===

Output the certificate above. Then on a final line, output a compact
summary list of every finding in this exact format (this is the machine-
readable section a downstream tool will parse):

FINDINGS_SUMMARY:
BLOCKER <file>:<line> — <issue>
MINOR <file>:<line> — <issue>
(or the single line: CLEAN — no findings)

=== DIFF ===
$diffText
=== END DIFF ===
"@

# ── Send to Ollama /api/generate (single-shot, no streaming needed) ──────
$body = @{
    model   = $Model
    prompt  = $prompt
    stream  = $false
    options = @{
        temperature = 0.1     # tight — review is not creative writing
        # 32K is qwen2.5-coder:14b's native max. The 16K we started with left
        # only ~800 tokens for the model to think AND write a verdict on a
        # 60K-char diff, which collapses to "CLEAN" under that pressure.
        num_ctx     = 32768
    }
} | ConvertTo-Json -Depth 5 -Compress

$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$outFile = Join-Path $reviewDir "theorc_$(Get-Date -Format yyyyMMdd_HHmmss).md"

try {
    $resp = Invoke-RestMethod -Uri "$OllamaHost/api/generate" -Method Post `
        -Body $body -ContentType "application/json" -TimeoutSec $TimeoutSec
} catch {
    Write-Host "ollama call failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 3
}

$verdict = ($resp.response ?? "").Trim()
if (-not $verdict) { $verdict = "(empty response from $Model)" }

@("# TheOrc review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Model: $Model",
  "Scope: $(if ($Range) { $Range } else { 'staged changes' })",
  "", $verdict) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
