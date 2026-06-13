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
    [int]   $TimeoutSec = 600,
    # ── Experimental knobs (B-3b/c/d) ────────────────────────────────────
    # RagAnchor: prepend a single most-similar past Codex review as an
    # in-context calibration anchor (RARe, arxiv 2511.05302 — top-1 only).
    [switch]$UseRagAnchor,
    [string]$EmbedModel = "nomic-embed-text:latest",
    # SelfConsistencyN > 1 runs the same prompt N times and majority-votes
    # on the FINDINGS_SUMMARY block (ICLR 2026 majority-voting workshop).
    [int]   $SelfConsistencyN = 1,
    # DiffFile: bypass git entirely and review a saved diff (e.g. extracted
    # from a past review_capture_*.json). Used to A/B prompting techniques
    # against a fixed known-bad diff with established Codex gold findings.
    [string]$DiffFile   = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# ── Describe what to review (same shape as codex-review.ps1 so a future
#    judge script can diff the two verdicts on a level playing field) ────
if ($DiffFile) {
    if (-not (Test-Path $DiffFile)) {
        Write-Host "DiffFile not found: $DiffFile" -ForegroundColor Red
        exit 3
    }
    $what    = "the diff loaded from $($DiffFile | Split-Path -Leaf) (replay mode)"
    $diffRaw = Get-Content $DiffFile -Raw -Encoding UTF8
    # If the file is a saved review_capture_*.json, extract the diff field;
    # otherwise treat it as a raw diff.
    if ($DiffFile -like "*.json") {
        try {
            $cap     = $diffRaw | ConvertFrom-Json
            $diffRaw = $cap.diff
            Write-Host "loaded diff from JSON capture: $($cap.example_id)" -ForegroundColor DarkCyan
        } catch {
            Write-Host "could not parse $DiffFile as a capture JSON; treating as raw diff" -ForegroundColor DarkYellow
        }
    }
    $stat = "(replay) $($diffRaw.Length) chars"
}
elseif ($Range) {
    $what = "the git commit range $Range"
    $stat = git diff --stat "$Range" 2>$null | Select-Object -Last 1
}
else {
    $what = "the STAGED git diff"
    $stat = git diff --cached --stat | Select-Object -Last 1
}
if (-not $stat) {
    Write-Host "Nothing to review ($(if ($Range) {"empty range $Range"} else {'no staged changes'}))." -ForegroundColor Yellow
    exit 0
}
Write-Host "reviewing: $($stat.Trim())  (model: $Model)" -ForegroundColor Cyan

# ── Gather the actual diff text (capped so we don't blow the model's context) ──
if ($DiffFile) {
    $diffText = $diffRaw
} else {
    $diffArgs = if ($Range) {
        @("log", "-p", "$Range", "--", ".", ":!publish", ":!*.psv", ":!*.tsv", ":!*.jsonl")
    } else {
        @("diff", "--cached")
    }
    $diffText = & git @diffArgs 2>&1 | Out-String
}

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

# ── Helper: embed text via Ollama (for RAG anchor selection) ─────────────
function Get-Embedding($text) {
    $body = @{ model = $EmbedModel; prompt = $text } | ConvertTo-Json -Compress
    try {
        $r = Invoke-RestMethod -Uri "$OllamaHost/api/embeddings" -Method Post `
            -Body $body -ContentType "application/json" -TimeoutSec 30
        return ,$r.embedding   # comma forces array-of-array result on the call site
    } catch { return $null }
}

function Get-Cosine($a, $b) {
    if ($null -eq $a -or $null -eq $b -or $a.Count -ne $b.Count) { return 0 }
    $dot = 0.0; $na = 0.0; $nb = 0.0
    for ($i = 0; $i -lt $a.Count; $i++) {
        $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nb += $b[$i] * $b[$i]
    }
    if ($na -eq 0 -or $nb -eq 0) { return 0 }
    return $dot / ([math]::Sqrt($na) * [math]::Sqrt($nb))
}

# ── Optional: RAG anchor (top-1 most similar past Codex review) ──────────
# RARe (arxiv 2511.05302) finding: a single in-context example beats no
# example by 111-153% BLEU-4. More than one HURTS (redundancy + conflicting
# cues). So we use top-1, hard-stop.
$ragAnchor = ""
if ($UseRagAnchor) {
    $stagingDir = Join-Path $root ".orc\swarm\review-staging"
    if (Test-Path $stagingDir) {
        Write-Host "RAG: searching past captures for the most similar diff..." -ForegroundColor DarkCyan
        # Hash the current diff so we can refuse to pick our own past
        # capture as the anchor — that would leak the gold answer key into
        # the prompt and invalidate every measurement.
        $sha   = [System.Security.Cryptography.SHA256]::Create()
        $curHash = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($diffText))).Replace("-","")

        $candidates = @()
        Get-ChildItem $stagingDir -Filter "review_capture_*.json" | ForEach-Object {
            try {
                $cap = Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($cap.verdicts.codex -and $cap.diff) {
                    $capHash = [BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($cap.diff))).Replace("-","")
                    if ($capHash -eq $curHash) {
                        Write-Host "RAG: skipping $($_.Name) — same diff as the one under review (no leakage)" -ForegroundColor DarkYellow
                        return
                    }
                    # First 4 KB is enough for semantic similarity — captures
                    # file paths + the headline changes without blowing the
                    # embedding endpoint's token budget.
                    $sample = if ($cap.diff.Length -gt 4096) { $cap.diff.Substring(0, 4096) } else { $cap.diff }
                    $candidates += [PSCustomObject]@{
                        File = $_.Name; Diff = $cap.diff; Codex = $cap.verdicts.codex; Sample = $sample
                    }
                }
            } catch { }
        }
        if ($candidates.Count -gt 0) {
            $currentSample = if ($diffText.Length -gt 4096) { $diffText.Substring(0, 4096) } else { $diffText }
            $currentEmb = Get-Embedding $currentSample
            if ($currentEmb) {
                $best = $null; $bestScore = -1.0
                foreach ($c in $candidates) {
                    $e = Get-Embedding $c.Sample
                    if ($null -eq $e) { continue }
                    $s = Get-Cosine $currentEmb $e
                    if ($s -gt $bestScore) { $bestScore = $s; $best = $c }
                }
                if ($best) {
                    Write-Host "RAG: anchor = $($best.File) (cosine $($bestScore.ToString('F3')))" -ForegroundColor DarkCyan
                    # Trim the example's verdict so it fits — we want the
                    # FINDINGS_SUMMARY block, not the whole certificate.
                    $exVerdict = $best.Codex
                    $exDiff    = if ($best.Diff.Length -gt 8000) { $best.Diff.Substring(0, 8000) + "`n[...truncated]" } else { $best.Diff }
                    $ragAnchor = @"
=== CALIBRATION EXAMPLE ===
This is a similar past diff and the gold-standard Codex review of it.
Use this to anchor what real BLOCKER/MINOR findings look like — concrete
file:line citations with crash, correctness, or convention justification.
DO NOT copy this example's findings into your output; this is for
calibration only.

PAST DIFF:
$exDiff

GOLD CODEX REVIEW OF THE PAST DIFF:
$exVerdict
=== END CALIBRATION EXAMPLE ===

Now apply the same standard to the CURRENT diff below.

"@
                }
            }
        }
        else {
            Write-Host "RAG: no past captures with a codex verdict found — running without anchor." -ForegroundColor DarkYellow
        }
    }
}

# ── Build the final prompt ───────────────────────────────────────────────
$finalPrompt = $ragAnchor + $prompt

# ── Single-shot OR self-consistency vote ─────────────────────────────────
$reviewDir = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$outFile = Join-Path $reviewDir "theorc_$(Get-Date -Format yyyyMMdd_HHmmss).md"

function Invoke-Reviewer($prompt, $temperature) {
    $body = @{
        model   = $Model
        prompt  = $prompt
        stream  = $false
        options = @{
            temperature = $temperature
            num_ctx     = 32768
        }
    } | ConvertTo-Json -Depth 5 -Compress
    try {
        $r = Invoke-RestMethod -Uri "$OllamaHost/api/generate" -Method Post `
            -Body $body -ContentType "application/json" -TimeoutSec $TimeoutSec
        return ($r.response ?? "").Trim()
    } catch {
        Write-Host "ollama call failed: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

# Parse the FINDINGS_SUMMARY block at the bottom of a verdict.
# Returns an array of [pscustomobject] with Severity, File, Line, Issue.
function Get-Findings($verdict) {
    if (-not $verdict) { return @() }
    $marker = $verdict.IndexOf("FINDINGS_SUMMARY:")
    if ($marker -lt 0) { return @() }
    $tail = $verdict.Substring($marker + "FINDINGS_SUMMARY:".Length)
    $out = @()
    foreach ($line in $tail -split "`n") {
        $t = $line.Trim()
        if ($t -match '^(BLOCKER|MINOR)\s+(\S+?):(\d+)\s*[—–\-]\s*(.+)$') {
            $out += [pscustomobject]@{
                Severity = $Matches[1]
                File     = $Matches[2]
                Line     = [int]$Matches[3]
                Issue    = $Matches[4].Trim()
            }
        }
    }
    return $out
}

# Majority vote across N rollouts. A finding "matches" another if same
# severity, same file, line within ±5 (handles model's line-number drift).
function Merge-Findings($allRuns, $majorityCount) {
    $all = $allRuns | ForEach-Object { $_ } | Where-Object { $_ }
    if ($all.Count -eq 0) { return @() }

    # Group by severity + file, then bucket by line-proximity.
    $groups = $all | Group-Object { "$($_.Severity)|$($_.File)" }
    $consensus = @()
    foreach ($g in $groups) {
        $sorted = $g.Group | Sort-Object Line
        $bucket = @()
        foreach ($f in $sorted) {
            if ($bucket.Count -eq 0 -or [math]::Abs($bucket[-1].Line - $f.Line) -le 5) {
                $bucket += $f
            } else {
                if ($bucket.Count -ge $majorityCount) { $consensus += $bucket[0] }
                $bucket = @($f)
            }
        }
        if ($bucket.Count -ge $majorityCount) { $consensus += $bucket[0] }
    }
    return $consensus
}

$allVerdicts = @()
$allFindings = @()
$rolls = [math]::Max(1, $SelfConsistencyN)
$majority = [math]::Floor($rolls / 2) + 1   # > N/2

for ($i = 1; $i -le $rolls; $i++) {
    if ($rolls -gt 1) {
        Write-Host "rollout $i of $rolls (temp $((0.1 + 0.15 * ($i - 1)).ToString('F2')))..." -ForegroundColor DarkCyan
    }
    # Vary temperature across rollouts so the votes come from genuinely
    # different reasoning paths, not the same path with sampling noise.
    $temp    = if ($rolls -eq 1) { 0.1 } else { 0.1 + 0.15 * ($i - 1) }
    $v       = Invoke-Reviewer $finalPrompt $temp
    if ($null -eq $v) { continue }
    $allVerdicts += $v
    $allFindings += ,(Get-Findings $v)
}

if ($allVerdicts.Count -eq 0) { exit 3 }

# Build the consensus output. Single-rollout = use the verdict as-is.
# Multi-rollout = pick a representative full verdict (rollout 1) + a
# majority-voted FINDINGS_SUMMARY appended below it.
$verdict = $allVerdicts[0]
if ($rolls -gt 1) {
    $consensus = Merge-Findings $allFindings $majority
    $voteBlock = if ($consensus.Count -eq 0) {
        "CLEAN — no findings reached the majority threshold ($majority of $rolls rollouts)"
    } else {
        ($consensus | ForEach-Object { "$($_.Severity) $($_.File):$($_.Line) — $($_.Issue)" }) -join "`n"
    }
    $verdict += "`n`n=== SELF-CONSISTENCY VOTE ($rolls rollouts, majority = $majority) ===`n$voteBlock"
}

@("# TheOrc review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Model: $Model",
  "Scope: $(if ($Range) { $Range } else { 'staged changes' })",
  "RAG anchor: $(if ($UseRagAnchor) { 'on' } else { 'off' })",
  "Self-consistency: $rolls rollout(s)",
  "", $verdict) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray
exit 0
