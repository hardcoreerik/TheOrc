# grok-review.ps1 — Grok (xAI) code review of git changes in this repo, at four cost tiers.
#
# Modes (pick the cheapest that answers the question — grok tokens are budgeted):
#   quick     (default) latest commit, diff-only, no tools, 64KB cap. Habitual/iteration reviews.
#   diff      uncommitted changes (staged+unstaged), diff-only, no tools, 128KB cap. Pre-commit check.
#   full      PR-scope review with repo READ access, conventions + roadmap cross-check, 512KB cap.
#             Run once before merge, not per iteration round.
#   adversary post-review red team with repo READ access: red-teams commit/PR claims and hunts
#             what a prior reviewer missed. Feed prior findings via -PriorReview.
#
# Tool policy (enforced via --deny permission rules — verified 2026-07-16 that the CLI's
# --disallowed-tools names were wrong and blocked NOTHING; --deny "Read(*)" style rules do block):
#   quick/diff:     deny Read/Grep/Glob/Bash/Write/Edit + web  → single-turn, predictable tokens
#   full/adversary: deny Bash/Write/Edit + web, ALLOW reads    → can chase callers/shared state
#
# Prerequisites:
#   1. Install Grok CLI:   irm https://x.ai/cli/install.ps1 | iex
#   2. Authenticate:       grok login   (browser OAuth, cached)  OR  $env:XAI_API_KEY = "xai-..."
#
# Usage:
#   tools\grok-review.ps1                                  # quick: HEAD~1..HEAD
#   tools\grok-review.ps1 -Mode diff                       # uncommitted changes
#   tools\grok-review.ps1 -Mode full -PR 64                # PR diff vs its base (three-dot)
#   tools\grok-review.ps1 -Mode adversary -PR 64 -PriorReview .orc\reviews\grok_full_xxx.md
#   tools\grok-review.ps1 -Range "a1b2..HEAD"              # explicit commit range (any mode)
#   tools\grok-review.ps1 -Staged                          # staged changes only (any mode)
#   tools\grok-review.ps1 -Focus "async safety, resource leaks"
#   tools\grok-review.ps1 -DryRun                          # build+show the prompt, don't call grok
#
# Scope precedence: -Staged  >  -Mode diff  >  -PR  >  -Range (default HEAD~1..HEAD)
#
# Exit codes (matches codex-review.ps1 semantics):
#   0 = CLEAN or MINORs only    1 = one or more BLOCKERs
#   2 = timed out    3 = grok exe not found    5 = tool error (git/gh/grok failure)
param(
    [ValidateSet('quick','diff','full','adversary')]
    [string]$Mode        = "quick",
    [string]$Range       = "",                   # empty = HEAD~1..HEAD
    [int]   $PR          = 0,                    # GitHub PR number — reviews origin/<base>...HEAD
    [switch]$Staged,
    [string]$Focus       = "",
    [string]$PriorReview = "",                   # adversary: file with prior findings to NOT repeat
    [string]$Model       = "grok-4.5",
    [int]   $TimeoutSec  = 0,                    # 0 = mode default (quick/diff 300, full/adversary 900)
    [int]   $MaxDiffKB   = 0,                    # 0 = mode default (quick 64, diff 128, full/adv 512)
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# -PR implies PR-scope review (repo reads, conventions, full diff budget) unless
# the caller explicitly asked for a specific mode — bare `-PR <n>` used to silently
# run as the cheap `quick` tier (64KB cap, no repo reads, no conventions block).
if ($PR -gt 0 -and -not $PSBoundParameters.ContainsKey('Mode')) {
    $Mode = 'full'
}

# ── Mode defaults ─────────────────────────────────────────────────────────────
$readsAllowed = $Mode -in @('full','adversary')
if ($TimeoutSec -le 0) { $TimeoutSec = if ($readsAllowed) { 900 } else { 300 } }
if ($MaxDiffKB  -le 0) {
    $MaxDiffKB = switch ($Mode) { 'quick' { 64 } 'diff' { 128 } default { 512 } }
}
if (-not $Range) { $Range = "HEAD~1..HEAD" }

# ── Verify prerequisites ──────────────────────────────────────────────────────
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
    Write-Host "XAI_API_KEY not set — using cached grok login (run 'grok login' if this fails)" -ForegroundColor Yellow
}

# ── Resolve -PR to a range against the PR's base branch ──────────────────────
# Three-dot (merge-base) diff is required: two-dot origin/<base>..HEAD includes
# reversed base-side commits when the branch is behind, inflating the scope.
$logRange  = $Range
$diffRange = $Range
$rangeTag  = "commit range $Range"
$prMeta    = ""
# -Staged / -Mode diff ignore the PR range, so don't require a working gh for them.
if ($PR -gt 0 -and -not $Staged -and $Mode -ne 'diff') {
    $base = gh pr view $PR --json baseRefName --jq .baseRefName 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $base) {
        Write-Host "Could not resolve base branch for PR #$PR (is gh authenticated?)" -ForegroundColor Red
        exit 5
    }
    git fetch origin $base --quiet 2>$null
    $logRange  = "origin/$base..HEAD"     # two-dot: PR-side commits only
    $diffRange = "origin/$base...HEAD"    # three-dot: diff vs merge-base
    $rangeTag  = "PR #$PR ($diffRange)"
    if ($readsAllowed) {
        # PR title+body feed claim verification in full/adversary prompts.
        $prMeta = gh pr view $PR --json title,body --jq '"PR title: " + .title + "\nPR description:\n" + .body' 2>$null
        if ($LASTEXITCODE -ne 0) { $prMeta = "" }
    }
}

# ── Pre-generate the diff (precedence: -Staged > -Mode diff > -PR > -Range) ──
$excl = @('.', ':!publish', ':!*.psv', ':!*.tsv', ':!*.jsonl')
if ($Staged) {
    $logLines = @()
    $diffRaw  = git diff --cached -- @excl 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git diff --cached failed. Nothing staged?" -ForegroundColor Red
        exit 5
    }
    $statLine = git diff --cached --stat | Select-Object -Last 1
    $scopeTag = "staged changes"
} elseif ($Mode -eq 'diff') {
    $logLines = @()
    $diffRaw  = git diff HEAD -- @excl 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git diff HEAD failed." -ForegroundColor Red
        exit 5
    }
    $statLine = git diff HEAD --stat 2>$null | Select-Object -Last 1
    $scopeTag = "uncommitted changes (staged + unstaged)"
    # git diff HEAD never shows untracked files — list their names so the pre-commit
    # review can flag strays (secrets, logs, files that should be committed/ignored).
    $untracked = git ls-files --others --exclude-standard -- @excl 2>$null
    if ($untracked) {
        $untrackedNote = "Untracked files present but contents not shown — flag any that look like secrets, logs, or files that belong in the commit or .gitignore:`n" +
            (($untracked | Select-Object -First 50) -join "`n") + "`n`n"
    }
} else {
    $logLines = git log --oneline "$logRange" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "git log failed for range '$logRange'. Invalid range?" -ForegroundColor Red
        exit 5
    }
    $diffRaw  = git diff "$diffRange" -- @excl 2>$null
    $statLine = git diff --stat "$diffRange" 2>$null | Select-Object -Last 1
    $scopeTag = $rangeTag
}

if (-not $untrackedNote) { $untrackedNote = "" }
# Untracked-only working trees still get a diff-mode review (of the stray-file list).
if (-not $statLine -and -not $diffRaw -and -not $untrackedNote) {
    Write-Host "Nothing to review ($scopeTag is empty)." -ForegroundColor Yellow
    exit 0
}
$statDisplay = if ($statLine) { $statLine.Trim() } else { $scopeTag }
Write-Host "grok $Mode reviewing: $statDisplay" -ForegroundColor Magenta

# ── Build diff string with commit context ─────────────────────────────────────
$maxChars = $MaxDiffKB * 1024
$commitList = if ($logLines) { "Commits in range:`n" + ($logLines -join "`n") + "`n`n" } else { "" }
$diff = $commitList + $untrackedNote + ($diffRaw -join "`n")
$truncated = $false
if ($diff.Length -gt $maxChars) {
    $diff = $diff.Substring(0, $maxChars)
    $truncated = $true
}

# ── Context detection ─────────────────────────────────────────────────────────
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
if ($Mode -eq 'full') {
    if (Test-Path "$root\docs\ROADMAP.md")       { $planNote += "Cross-check against docs/ROADMAP.md. " }
    if (Test-Path "$root\Avalonia_Migration.md") { $planNote += "Cross-check against Avalonia_Migration.md. " }
}

$priorNote = ""
if ($PriorReview) {
    if (-not (Test-Path $PriorReview)) {
        Write-Host "-PriorReview file not found: $PriorReview" -ForegroundColor Red
        exit 5
    }
    $priorText = "$(Get-Content $PriorReview -Raw)".Trim()
    if (-not $priorText) {
        Write-Host "-PriorReview file is empty: $PriorReview" -ForegroundColor Red
        exit 5
    }
    if ($priorText.Length -gt 32KB) { $priorText = $priorText.Substring(0, 32KB) }
    $priorNote = @"

A prior reviewer already reported the findings below. Do NOT repeat them — your value is what they MISSED:
--- PRIOR FINDINGS ---
$priorText
--- END PRIOR FINDINGS ---
"@
}

$prMetaNote = if ($prMeta) { "`n$prMeta`n" } else { "" }
$truncNote  = if ($truncated) { "`n[DIFF TRUNCATED at ${MaxDiffKB}KB]" } else { "" }
$focusNote  = if ($Focus) { "`nExtra attention: $Focus`n" } else { "" }

# ── Mode-specific review instructions ─────────────────────────────────────────
$modeBlock = switch ($Mode) {
    'quick' {
@"
This is a QUICK review. Tools are disabled — review ONLY the diff below; do not attempt to read files.
Review in order of severity:
1. Crash risk at runtime (init order, async void, reentrancy, null deref)
2. Correctness regressions vs pre-change code
3. Fail-open vs fail-closed on security/validation paths
4. Resource leaks (undisposed objects, unclosed handles)
Be terse. Skip style nits.
"@
    }
    'diff' {
@"
This is a PRE-COMMIT sanity check of uncommitted changes. Tools are disabled — review ONLY the diff below.
Review in order of severity:
1. Crash risk at runtime (init order, async void, reentrancy, null deref)
2. Incomplete changes: TODO stubs, half-renamed symbols, debug leftovers (Console.WriteLine, commented-out code)
3. Accidental deletions or unrelated files swept into the change
4. Secrets, credentials, or machine-local paths in the diff
Be terse. Skip style nits.
"@
    }
    'full' {
@"
This is a FULL pre-merge review.
$conventions
$planNote
Review in order of severity:
1. Crash risk at runtime (init order, async void, reentrancy, null deref)
2. Correctness regressions vs pre-change code
3. Merge hazards: interactions with code OUTSIDE the diff — callers, shared state, event subscriptions
4. Fail-open vs fail-closed on security/validation paths
5. Resource leaks (undisposed objects, unclosed handles)
6. Convention violations listed above
You MAY read repository files to verify callers and shared state affected by the diff.
Be economical: prefer a few targeted reads of the most-affected files over broad exploration.
"@
    }
    'adversary' {
@"
You are an ADVERSARIAL second reviewer. The author and a prior reviewer believe this change is
correct and complete. Assume they are wrong somewhere; your job is to prove it.
$conventions
1. Red-team every behavioral claim in the commit messages / PR description: verify each against
   the actual diff. Flag any claim that is asserted but not implemented, or that masks a regression.
2. Hunt what a first-pass reviewer misses: edge cases, boundary conditions, error/cancel paths,
   concurrency, ordering assumptions.
3. Check interactions with code OUTSIDE the diff — callers, shared state, conventions.
You MAY read repository files; be economical with reads.
$priorNote
"@
    }
}

# ── Write prompt to a file (--prompt-file avoids Windows arg-length limits) ───
$reviewDir  = Join-Path $root ".orc\reviews"
New-Item -ItemType Directory -Force $reviewDir | Out-Null
$timestamp  = Get-Date -Format yyyyMMdd_HHmmss
$promptFile = Join-Path $reviewDir "current_prompt.txt"
$outFile    = Join-Path $reviewDir "grok_${Mode}_$timestamp.md"

$prompt = @"
You are performing a code review of TheOrc (WPF/.NET 10 local AI coding assistant) on branch '$branch'.
Scope: $scopeTag.
$prMetaNote
$modeBlock
$focusNote
Here is the full git diff:

$diff$truncNote

Output format — one finding per line, each on its OWN line starting at column 0:
  BLOCKER <file>:<line> — <issue>
  MINOR   <file>:<line> — <issue>
Or the single word CLEAN on its own line if no issues found. No other prose, no narration.
"@

$prompt | Set-Content $promptFile -Encoding utf8

# ── Tool policy via --deny permission rules ───────────────────────────────────
# NOTE: the CLI's --disallowed-tools takes internal names (read_file, write, ...) but does NOT
# actually block them (verified with a secret-file probe). --deny "Read(*)"-style rules DO block.
$denyRules = @('Bash(*)', 'Write(*)', 'Edit(*)')
if (-not $readsAllowed) { $denyRules += @('Read(*)', 'Grep(*)', 'Glob(*)') }

$grokArgs = @('--always-approve', '--no-alt-screen', '--output-format', 'plain', '--disable-web-search')
foreach ($rule in $denyRules) { $grokArgs += @('--deny', $rule) }
$grokArgs += @('-m', $Model, '--prompt-file', $promptFile)

if ($DryRun) {
    Write-Host "── DRY RUN ──" -ForegroundColor Cyan
    Write-Host "Mode:       $Mode"
    Write-Host "Scope:      $scopeTag"
    Write-Host "Model:      $Model   Timeout: ${TimeoutSec}s   DiffCap: ${MaxDiffKB}KB   Truncated: $truncated"
    Write-Host "Deny rules: $($denyRules -join ', ')"
    Write-Host "Prompt:     $($prompt.Length) chars -> $promptFile (kept for inspection)"
    Write-Host "Args:       grok $($grokArgs -join ' ')"
    exit 0
}

# ── Run grok headlessly ───────────────────────────────────────────────────────
$psi = [System.Diagnostics.ProcessStartInfo]::new($grokExe)
foreach ($a in $grokArgs) { $psi.ArgumentList.Add($a) }
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

# Non-zero exit from grok = auth failure, model error, etc. — exit 5 (tool error).
if ($p.ExitCode -ne 0) {
    $errText = $stderrTask.Result.Trim()
    Write-Host "grok exited with code $($p.ExitCode)" -ForegroundColor Red
    if ($errText) { Write-Host $errText -ForegroundColor Red }
    exit 5
}

$verdict = $stdoutTask.Result.Trim()

# Strip ANSI escape sequences and markdown bold markers from plain output
$verdict = $verdict -replace '\x1b\[[0-9;]*[mGKHF]', ''
$verdict = $verdict -replace '\*\*', ''   # strip ** bold markers (safe; single * may appear in issue text)
$rawLines = $verdict -split "`n"
# The model sometimes emits progress narration and glues findings/verdict onto
# the end of a narration line without a newline ("...merge hazard.CLEAN"), so
# accept findings mid-line (anchored to the <file>:<line> shape) as well as
# line-anchored ones.
$findings = foreach ($line in $rawLines) {
    if ($line -match '^(BLOCKER|MINOR|CLEAN)' -or $line -match '^\[') { $line }
    elseif ($line -match '(?<f>(BLOCKER|MINOR)\s+\S+:\d+.*)$') { $Matches.f }
}
$verdict = ($findings -join "`n").Trim()

# No findings extracted: a trailing CLEAN token on the last non-empty line
# (possibly glued to narration) still means a clean review.
if (-not $verdict) {
    $lastLine = $rawLines | Where-Object { $_.Trim() } | Select-Object -Last 1
    if ($lastLine -match '\bCLEAN\s*$') { $verdict = 'CLEAN' }
}

if (-not $verdict) {
    Write-Host "grok returned no parseable findings — check raw output below:" -ForegroundColor Yellow
    Write-Host $stdoutTask.Result
    exit 5
}

@("# Grok $Mode review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "Model: $Model  |  Branch: $branch  |  Scope: $scopeTag",
  "", $verdict) | Set-Content $outFile -Encoding utf8

Write-Host ""
Write-Host $verdict
Write-Host ""
Write-Host "saved -> $outFile" -ForegroundColor DarkGray

# Exit 1 if any line is a BLOCKER (matches codex-review.ps1 semantics); 0 if clean/minor-only.
$hasBlocker = ($verdict -split "`n") | Where-Object { $_ -match '^BLOCKER\s' }
if ($hasBlocker) { exit 1 }
exit 0
