# Get-CudaRedistributables.ps1 - fetch NVIDIA's official CUDA runtime redistributables
# (cudart64_12.dll, cublas64_12.dll, cublasLt64_12.dll) without installing the full CUDA
# Toolkit or depending on a third-party NuGet repackaging.
#
# Source: NVIDIA's own redistributable manifest feed, the same official channel conda/pip
# use to build their nvidia-cuda-runtime-cu12/nvidia-cublas-cu12 packages:
#   https://developer.download.nvidia.com/compute/cuda/redist/redistrib_<version>.json
#
# Why this exists: LLamaSharp.Backend.Cuda12.Windows's ggml-cuda.dll dynamically imports
# cudart64_12.dll and cublas64_12.dll (which itself needs cublasLt64_12.dll) at runtime, but
# the NuGet package does not ship them - see OrchestratorIDE.NativeRuntime.csproj's
# TheOrcCudaRedistDir property. Every fleet dev machine sources these from a locally installed
# CUDA Toolkit; the release CI runner (a stock GitHub-hosted windows-latest box) has no toolkit
# at all, so the official release build has been silently missing these DLLs and CPU-falling-
# back for every real end user with an NVIDIA GPU. This script gives CI (or any machine) a way
# to fetch just the ~3 files actually needed, verified against NVIDIA's published SHA-256, in a
# fraction of the time/bandwidth a full toolkit install would cost.
#
# Usage:
#   Tools\Get-CudaRedistributables.ps1                          # default version, default output dir
#   Tools\Get-CudaRedistributables.ps1 -Version 12.4.0 -OutputDir F:\CudaRedist12
#
# Exit codes: 0 = success (or already present with matching hash), 1 = download/verify/extract
# failure. On success, prints the resolved output directory on its own final line so a CI step
# can capture it (e.g. into $env:TheOrcCudaRedistDir).
param(
    [string]$Version   = "12.4.0",
    [string]$OutputDir = "",
    [int]   $TimeoutSec = 300
)

$ErrorActionPreference = "Stop"
# Invoke-WebRequest renders a progress bar by default, which materially slows large downloads
# on some PowerShell hosts (noticeable here: multi-hundred-MB CUDA archives on every cache-miss
# CI run).
$ProgressPreference = "SilentlyContinue"

if (-not $OutputDir) {
    $OutputDir = Join-Path $env:TEMP "theorc-cuda-redist-$Version"
}

$manifestUrl = "https://developer.download.nvidia.com/compute/cuda/redist/redistrib_$Version.json"
$components  = @("cuda_cudart", "libcublas")
$neededDlls  = @("cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll")

function Test-Sha256 {
    param([string]$FilePath, [string]$ExpectedHash)
    $actual = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash
    return $actual -ieq $ExpectedHash
}

# Idempotent: skip everything if all three DLLs are already present. Re-run to force a refresh
# by pointing -OutputDir at a fresh/empty directory.
$allPresent = $true
foreach ($dll in $neededDlls) {
    if (-not (Test-Path (Join-Path $OutputDir $dll))) { $allPresent = $false; break }
}
if ($allPresent) {
    Write-Host "All CUDA redistributables already present in '$OutputDir' - skipping download." -ForegroundColor DarkGray
    Write-Output $OutputDir
    exit 0
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$workDir = Join-Path $OutputDir "_download"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

Write-Host "Fetching NVIDIA CUDA redistributable manifest for version $Version..." -ForegroundColor Cyan
try {
    $manifest = Invoke-RestMethod -Uri $manifestUrl -TimeoutSec $TimeoutSec
} catch {
    Write-Host "Failed to fetch manifest from $manifestUrl : $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

foreach ($component in $components) {
    if (-not $manifest.$component) {
        Write-Host "Manifest is missing expected component '$component' - NVIDIA may have restructured the feed." -ForegroundColor Red
        exit 1
    }
    $entry = $manifest.$component.'windows-x86_64'
    if (-not $entry) {
        Write-Host "Component '$component' has no windows-x86_64 entry in this manifest." -ForegroundColor Red
        exit 1
    }

    $relativePath = $entry.relative_path
    $expectedSha  = $entry.sha256
    $downloadUrl  = "https://developer.download.nvidia.com/compute/cuda/redist/$relativePath"
    $zipPath      = Join-Path $workDir ([System.IO.Path]::GetFileName($relativePath))

    Write-Host "Downloading $component ($($entry.size) bytes) from $downloadUrl..." -ForegroundColor Cyan
    try {
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -TimeoutSec $TimeoutSec
    } catch {
        Write-Host "Failed to download $component : $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }

    if (-not (Test-Sha256 -FilePath $zipPath -ExpectedHash $expectedSha)) {
        Write-Host "SHA-256 mismatch for $component - refusing to use a corrupted/tampered download." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Verified SHA-256 for $component." -ForegroundColor DarkGray

    $extractDir = Join-Path $workDir ([System.IO.Path]::GetFileNameWithoutExtension($relativePath))
    try {
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

        $binDir = Get-ChildItem -Path $extractDir -Directory -Recurse -Filter "bin" | Select-Object -First 1
        if (-not $binDir) {
            Write-Host "Could not find a 'bin' directory inside the extracted $component archive." -ForegroundColor Red
            exit 1
        }

        foreach ($dll in Get-ChildItem -Path $binDir.FullName -Filter "*.dll") {
            if ($neededDlls -contains $dll.Name) {
                Copy-Item -Path $dll.FullName -Destination (Join-Path $OutputDir $dll.Name) -Force
                Write-Host "  Extracted $($dll.Name)" -ForegroundColor DarkGray
            }
        }
    } catch {
        Write-Host "Failed to extract or copy $component : $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue

$missing = $neededDlls | Where-Object { -not (Test-Path (Join-Path $OutputDir $_)) }
if ($missing.Count -gt 0) {
    Write-Host "Missing expected DLLs after extraction: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "All CUDA redistributables ready in '$OutputDir'." -ForegroundColor Green
Write-Output $OutputDir
exit 0
