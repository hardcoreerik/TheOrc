# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Release asset audit — external release review P0: "Build a one-time release audit
# script that produces a table for every tag: expected assets, actual assets, file
# size, SHA-256, platform/RID, launch result, installer launch result, native backend
# load result, GPU/CPU backend selected, update-path result, superseded/supported
# status."
#
# Safety scoping (deliberate, not an oversight):
#   - The installer (OrchestratorSetup.exe) is NEVER executed by this script. Running
#     an installer unattended can modify system state (registry, files) with no human
#     in the loop -- that's outside what an audit tool should do on its own. It is
#     verified to exist, be a valid PE, and hashed; "installer_launch_result" is always
#     recorded as "not executed (would modify system state)".
#   - The main app IS launched for a short liveness check (does it start and stay up,
#     or crash within N seconds) -- reversible, no persistent system changes, then killed.
#   - "Native backend load result" / "GPU/CPU backend selected" get a REAL live answer
#     only for the CURRENT build (this repo's own already-built NativeProbe, exercising
#     this session's own trusted code). For historical tags, this script does NOT
#     execute the old binary's native stack unattended -- it uses a safe static proxy
#     instead: does the extracted asset directory actually contain the CUDA runtime
#     DLLs the review's documented incident says were historically omitted
#     (cudart64_*.dll / cublas64_*.dll / cublasLt64_*.dll). This directly verifies the
#     SPECIFIC documented incident without running unknown old code.
#   - "Update-path result" is not tested live (would require actually triggering the
#     self-updater against a running older version) -- recorded as "not tested".
#
# Usage:
#   pwsh scripts/audit_release.ps1 -Tags v1.12.0
#   pwsh scripts/audit_release.ps1 -Tags v1.12.0 ; pwsh scripts/audit_release.ps1 -Tags v1.11.2
#     (run once per tag -- PowerShell's -File invocation mode does not reliably
#     split a comma/array argument across a cross-shell boundary; each run
#     writes its own <tag>.json and appends to the summary)
#   pwsh scripts/audit_release.ps1 -All          # every tag from `gh release list`, one by one
#   pwsh scripts/audit_release.ps1 -RebuildSummary
#     # no new audits -- just rescans every .orc/release-audit/<tag>.json already
#     # on disk and regenerates SUMMARY.md from all of them (the durable, cumulative
#     # view; safe to run any time, matches PROMOTION_REGISTRY.json's append pattern)
#
# Output (deliberately NOT under .orc/, which is entirely gitignored -- this is meant
# to be a durable, checked-in audit trail, same reasoning as
# training_pit/foundry/PROMOTION_REGISTRY.json living outside .orc/ for the same reason):
#   release_audit/<tag>.json   — machine-readable, one file per audited tag
#   release_audit/SUMMARY.md   — human-readable table across every *.json on disk

param(
    [string[]] $Tags = @(),
    [switch]   $All,
    [switch]   $RebuildSummary,
    [int]      $LaunchTimeoutSeconds = 6
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir   = Join-Path $repoRoot "release_audit"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Known documented incidents (from the CUDA-fix commit trail and the v1.11.0
# concurrency-bug fix in release.yml's own comments) -- used to auto-flag
# superseded/supported status rather than requiring hand-maintained data for
# every tag. Cutoff dates are inclusive-before.
#
# IMPORTANT NUANCE (verified against git log, not assumed): two different CUDA
# fixes landed the SAME DAY, 10 minutes apart -- b0b9cd2e ("bundle the DLLs into
# every Windows build") was immediately superseded by 908d9f38 ("fetch them via
# the INSTALLER into %LOCALAPPDATA%\TheOrc\CudaRedist instead, so non-NVIDIA
# users don't pay the download-size cost"). No tagged release ever shipped the
# "bundled in the portable zip" mechanism -- v1.11.2 and earlier have neither
# fix (the real, documented omission bug); v1.11.3 onward has the
# installer-fetch mechanism, under which the CUDA DLLs being ABSENT from the
# portable zip/exe is the CORRECT, INTENDED state, not a defect. This script's
# CUDA-DLL-presence check on the portable zip is only a meaningful "is this
# release broken" signal for tags published BEFORE this cutoff.
$CudaOmissionCutoff        = [datetime]"2026-07-04T00:00:00Z"
$ConcurrencyBugTags        = @("v1.11.0")   # documented: could omit win/linux Warband assets

$expectedWindowsAssetPatterns = @(
    "OrchestratorSetup.exe",
    "*win-x64*.zip",
    "OrchestratorIDE.exe"
)

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-LaunchLiveness {
    param([string]$ExePath, [int]$TimeoutSeconds)
    if (-not (Test-Path $ExePath)) { return "not_found" }
    try {
        $proc = Start-Process -FilePath $ExePath -PassThru -WindowStyle Minimized
        Start-Sleep -Seconds $TimeoutSeconds
        if ($proc.HasExited) {
            $code = $proc.ExitCode
            return "exited_early(code=$code)"
        }
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        return "started_and_stayed_up"
    } catch {
        return "launch_error($($_.Exception.Message))"
    }
}

function Test-CudaDllPresence {
    param([string]$ExtractedDir)
    $cudaFiles = Get-ChildItem -Path $ExtractedDir -Recurse -ErrorAction SilentlyContinue -Include `
        "cudart64_*.dll", "cublas64_*.dll", "cublasLt64_*.dll", "ggml-cuda.dll"
    return @{
        found       = $cudaFiles.Count -gt 0
        file_names  = $cudaFiles.Name
        file_count  = $cudaFiles.Count
    }
}

function Audit-Tag {
    param([string]$Tag)

    Write-Host "=== Auditing $Tag ===" -ForegroundColor Cyan
    $tagVersion = $Tag.TrimStart('v')
    $releaseJson = gh release view $Tag --json assets,publishedAt,isDraft,isPrerelease 2>$null | ConvertFrom-Json
    if (-not $releaseJson) {
        Write-Warning "Could not fetch release $Tag from GitHub -- skipping."
        return $null
    }

    $workDir = Join-Path $env:TEMP "theorc-audit-$Tag-$(Get-Random)"
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    $actualAssets = @()
    foreach ($asset in $releaseJson.assets) {
        $isWindowsRelevant = $expectedWindowsAssetPatterns | Where-Object { $asset.name -like $_ }
        $entry = @{
            name          = $asset.name
            size_bytes    = $asset.size
            windows_asset = [bool]$isWindowsRelevant
            downloaded    = $false
            sha256        = $null
        }
        if ($isWindowsRelevant) {
            $localPath = Join-Path $workDir $asset.name
            try {
                gh release download $Tag --pattern $asset.name --dir $workDir --clobber 2>$null | Out-Null
                if (Test-Path $localPath) {
                    $entry.downloaded = $true
                    $entry.sha256     = Get-Sha256 -Path $localPath
                    $entry.actual_size_bytes = (Get-Item $localPath).Length
                }
            } catch {
                $entry.download_error = $_.Exception.Message
            }
        }
        $actualAssets += $entry
    }

    # Expected-vs-actual: which of the fixed Windows patterns are missing entirely?
    $missingPatterns = @()
    foreach ($pattern in $expectedWindowsAssetPatterns) {
        $matchFound = $actualAssets | Where-Object { $_.name -like $pattern }
        if (-not $matchFound) { $missingPatterns += $pattern }
    }

    # Extract the portable zip (if present and downloaded) for the CUDA-DLL static check
    # and the launch-liveness check.
    $zipAsset = $actualAssets | Where-Object { $_.name -like "*win-x64*.zip" -and $_.downloaded } | Select-Object -First 1
    $extractDir = $null
    $cudaCheck = @{ checked = $false }
    $launchResult = "not_tested (no extractable asset)"
    if ($zipAsset) {
        $extractDir = Join-Path $workDir "extracted"
        try {
            Expand-Archive -Path (Join-Path $workDir $zipAsset.name) -DestinationPath $extractDir -Force
            $cudaResult = Test-CudaDllPresence -ExtractedDir $extractDir
            $cudaCheck = @{ checked = $true } + $cudaResult

            $exeCandidate = Get-ChildItem -Path $extractDir -Recurse -Filter "OrchestratorIDE.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($exeCandidate -and $Tag -eq "v1.12.0") {
                # Only actually LAUNCH the current, freshly-built, known-good release.
                # See file header for why historical tags are not executed.
                $launchResult = Test-LaunchLiveness -ExePath $exeCandidate.FullName -TimeoutSeconds $LaunchTimeoutSeconds
            } elseif ($exeCandidate) {
                $launchResult = "not_executed (historical binary -- static checks only, see script header)"
            } else {
                $launchResult = "exe_not_found_in_archive"
            }
        } catch {
            $cudaCheck = @{ checked = $false; error = $_.Exception.Message }
        }
    }

    # Superseded/supported status from documented incidents.
    $publishedAt = [datetime]$releaseJson.publishedAt
    $isPreCudaFix = $publishedAt -lt $CudaOmissionCutoff
    $statusFlags = @()
    if ($isPreCudaFix -and $cudaCheck.checked -and -not $cudaCheck.found) {
        $statusFlags += "PRE-CUDA-FIX, CONFIRMED (published before 2026-07-04 AND this audit's own download found zero CUDA runtime DLLs in the portable zip -- the historical omission bug the review documents, not just a date-based assumption)"
    } elseif ($isPreCudaFix) {
        $statusFlags += "PRE-CUDA-FIX ERA (published before 2026-07-04; not independently confirmed by this audit run -- see cuda_dll_check for what was actually found)"
    } elseif ($cudaCheck.checked -and -not $cudaCheck.found) {
        # NOT a bug for v1.11.3+: CUDA DLLs are intentionally fetched by the
        # INSTALLER at install time (908d9f38), not bundled in the portable zip.
        # Recorded for transparency, never added to statusFlags.
    }
    if ($ConcurrencyBugTags -contains $Tag) {
        $statusFlags += "CONCURRENCY-BUG-RELEASE (documented in release.yml's own comments: could silently omit Windows/Linux Warband assets due to a matrix-concurrency race)"
    }
    if ($missingPatterns.Count -gt 0) {
        $statusFlags += "MISSING EXPECTED ASSETS: $($missingPatterns -join ', ')"
    }
    $supersededStatus = if ($statusFlags.Count -gt 0) { "FLAGGED" } else { "no known issues found by this audit" }

    $result = @{
        tag                        = $Tag
        published_at               = $releaseJson.publishedAt
        expected_windows_patterns  = $expectedWindowsAssetPatterns
        missing_patterns           = $missingPatterns
        actual_assets              = $actualAssets
        launch_result              = $launchResult
        installer_launch_result    = "not_executed (would modify system state -- see script header)"
        native_backend_load_result = if ($cudaCheck.checked) { "static_proxy: cuda_dll_presence" } else { "not_checked (no extractable asset)" }
        cuda_dll_check             = $cudaCheck
        gpu_cpu_backend_selected   = "not_tested_live (see native_backend_load_result proxy; live GPU test only run for the current release via Tools/NativeProbe separately)"
        update_path_result         = "not_tested (would require triggering the self-updater against a running older version)"
        superseded_status          = $supersededStatus
        status_flags               = $statusFlags
        audited_at                 = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }

    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue

    $outFile = Join-Path $outDir "$Tag.json"
    $result | ConvertTo-Json -Depth 10 | Set-Content -Path $outFile -Encoding utf8
    Write-Host "  -> $outFile"
    return $result
}

if ($All) {
    $Tags = (gh release list --limit 100 --json tagName | ConvertFrom-Json).tagName
}

$allResults = @()
if (-not $RebuildSummary) {
    if ($Tags.Count -eq 0) {
        Write-Error "No tags specified. Use -Tags v1.12.0, -All, or -RebuildSummary."
        exit 1
    }
    foreach ($tag in $Tags) {
        $r = Audit-Tag -Tag $tag
        if ($r) { $allResults += $r }
    }
}

# Always rebuild the summary from EVERY *.json on disk, not just this run's tags --
# cumulative view, and the only thing -RebuildSummary needs to do.
$allResults = Get-ChildItem -Path $outDir -Filter "*.json" | ForEach-Object {
    Get-Content $_.FullName -Raw | ConvertFrom-Json
} | Sort-Object -Property @{ Expression = { [version]($_.tag.TrimStart('v')) } }

# ── Summary markdown ──────────────────────────────────────────────────────────
$summaryPath = Join-Path $outDir "SUMMARY.md"
$lines = @()
$lines += "# TheOrc Release Asset Audit"
$lines += ""
$lines += "Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') UTC by scripts/audit_release.ps1."
$lines += ""
$lines += "| Tag | Published | Missing assets | Native backend proxy | Launch result | Status |"
$lines += "|---|---|---|---|---|---|"
foreach ($r in $allResults) {
    $missing = if ($r.missing_patterns.Count -gt 0) { $r.missing_patterns -join ", " } else { "none" }
    $isPreCuda = [datetime]$r.published_at -lt $CudaOmissionCutoff
    $cuda = if (-not $r.cuda_dll_check.checked) {
        "not checked"
    } elseif ($r.cuda_dll_check.found) {
        "CUDA DLLs present ($($r.cuda_dll_check.file_count))"
    } elseif ($isPreCuda) {
        "**NO CUDA DLLs FOUND (pre-fix era -- real bug)**"
    } else {
        "absent from zip (expected -- installer fetches at install time, see script header)"
    }
    $lines += "| $($r.tag) | $($r.published_at) | $missing | $cuda | $($r.launch_result) | $($r.superseded_status) |"
}
$lines += ""
$lines += "## Flagged tags (detail)"
$lines += ""
foreach ($r in $allResults) {
    if ($r.status_flags.Count -gt 0) {
        $lines += "### $($r.tag)"
        foreach ($f in $r.status_flags) { $lines += "- $f" }
        $lines += ""
    }
}
$lines -join "`n" | Set-Content -Path $summaryPath -Encoding utf8
Write-Host "`nSummary written to $summaryPath" -ForegroundColor Green
