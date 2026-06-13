# gate-review.ps1 — Reviewer Quality Gate for a swarm staging directory.
#
# Reviews files produced by a swarm run via Codex CLI. Because staged files are
# not yet in git, this script creates a temporary git repository, copies the
# staged files in, commits them, then runs Codex against that commit. The temp
# repo is deleted on exit regardless of outcome.
#
# Called automatically by SwarmSession when SwarmReviewGateEnabled = true.
# Can also be run manually to review any directory of code files.
#
# Usage:
#   tools\gate-review.ps1 -StagingDir ".orc\swarm\runs\20260613_120000\staging"
#   tools\gate-review.ps1 -StagingDir <path> -Focus "async void paths and null refs"
#   tools\gate-review.ps1 -StagingDir <path> -TimeoutSec 900
#
# Output: verdict on stdout + saved to .orc\reviews\gate_<timestamp>.md
# Exit codes: 0 = reviewed   2 = timed out   3 = codex.exe not found   4 = staging dir empty/missing
param(
    [string] $StagingDir,
    [string] $Focus      = "",
    [int]    $TimeoutSec = 600
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $StagingDir -or -not (Test-Path $StagingDir)) {
    Write-Host "gate-review: staging dir not found: $StagingDir" -ForegroundColor Red
    exit 4
}

$stagedFiles = Get-ChildItem $StagingDir -Recurse -File -ErrorAction SilentlyContinue
if ($stagedFiles.Count -eq 0) {
    Write-Host "gate-review: no files in staging dir — gate skipped." -ForegroundColor Yellow
    exit 4
}

# ── Locate codex.exe (npm shim deadlocks under redirected stdio) ──────────────
$exe = Get-ChildItem "$env:APPDATA\npm\node_modules\@openai\codex" -Recurse `
        -Filter "codex.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { $exe = (Get-Command codex.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $exe) {
    Write-Host "codex.exe not found — install with: npm i -g @openai/codex" -ForegroundColor Red
    exit 3
}

# ── Create temp git repo, copy staged files in, commit ───────────────────────
$tempRepo = Join-Path $env:TEMP "orc-gate-$(Get-Random)"
try {
    New-Item -ItemType Directory -Force $tempRepo | Out-Null

    $StagingDir = $StagingDir.TrimEnd('\', '/')
    $stagedFiles | ForEach-Object {
        $rel = $_.FullName.Substring($StagingDir.Length + 1)
        $dst = Join-Path $tempRepo $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item $_.FullName $dst -Force
    }

    git -C $tempRepo init -q 2>$null
    git -C $tempRepo config user.email "gate@orc"     2>$null
    git -C $tempRepo config user.name  "OrcGate"      2>$null
    git -C $tempRepo add .                             2>$null
    git -C $tempRepo commit -q -m "swarm output"      2>$null

    $stat = "$($stagedFiles.Count) file(s) staged for review"
    Write-Host "gate reviewing: $stat" -ForegroundColor Cyan

    $prompt = @"
You are a senior engineer reviewing code produced by an AI coding swarm.
Review the commit at HEAD (run: git log -p HEAD -- . ':!*.psv' ':!*.tsv' ':!*.jsonl').
These are NEW files — not patches against existing code. Treat all content as new production code.

Repo context: TheOrc — WPF/.NET 10 local AI orchestration tool.

Check in order of severity:
1. Crash risk — null refs, async void exception paths, init order, binding failures, reentrancy
2. Correctness — logic errors, off-by-one, type mismatches, missing error handling
3. Security — command injection, path traversal, XSS, hardcoded secrets
4. Resource leaks — undisposed streams/processes, missing using blocks
5. Convention — AutomationProperties.AutomationId on interactive WPF controls, NUnit tests named T##_*.cs
$(if ($Focus) { "Extra attention: $Focus" })

Output format: one line per finding —
"BLOCKER <file>:<line> — <issue>" or "MINOR <file>:<line> — <issue>" —
or the single word CLEAN if no issues found. No other prose.
"@

    # ── Run codex with closed stdin + hard timeout ────────────────────────────
    $reviewDir = Join-Path $root ".orc\reviews"
    New-Item -ItemType Directory -Force $reviewDir | Out-Null
    $ts      = Get-Date -Format yyyyMMdd_HHmmss
    $outFile = Join-Path $reviewDir "gate_$ts.md"

    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($a in @('exec', '--sandbox', 'read-only', '-C', $tempRepo,
                     '--output-last-message', "$outFile.last", $prompt)) {
        $psi.ArgumentList.Add($a)
    }
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false

    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Close()
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill($true) } catch {}
        Write-Host "gate review TIMED OUT after ${TimeoutSec}s" -ForegroundColor Red
        exit 2
    }

    $verdict = if (Test-Path "$outFile.last") {
        Get-Content "$outFile.last" -Raw
    } else {
        $stdout.Result
    }
    Remove-Item "$outFile.last" -ErrorAction SilentlyContinue

    @("# Gate review — $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
      "Staging: $StagingDir",
      "", $verdict.Trim()) | Set-Content $outFile -Encoding utf8

    Write-Host ""
    Write-Host $verdict.Trim()
    Write-Host ""
    Write-Host "saved -> $outFile" -ForegroundColor DarkGray
    exit 0
}
finally {
    Remove-Item $tempRepo -Recurse -Force -ErrorAction SilentlyContinue
}
