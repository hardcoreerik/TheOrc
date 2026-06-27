# run-local-testenv.ps1 — run TheOrc's headless test suites inside a repo-local
# environment profile so TEMP / APPDATA / HOME writes stay under the workspace.
#
# Why this exists:
# - Some tests touch temp/AppData-backed paths through git, Hive identity, or UI state.
# - Sandboxed or locked-down machines can deny those OS-owned paths even when the repo
#   itself is writable.
# - Running under a repo-local profile makes the test path reproducible and safer.
#
# Usage:
#   Tools\run-local-testenv.ps1
#   Tools\run-local-testenv.ps1 -Suite Unit
#   Tools\run-local-testenv.ps1 -Suite Headless
#   Tools\run-local-testenv.ps1 -UseScratchBuildPaths
#   Tools\run-local-testenv.ps1 -UseVsTestOnly
#   Tools\run-local-testenv.ps1 -NoRestore
#
# Exit code:
#   0 = requested suites passed
#   1 = one or more suites failed
param(
    [ValidateSet("Both", "Unit", "Headless")]
    [string]$Suite = "Both",
    [switch]$UseScratchBuildPaths,
    [switch]$UseVsTestOnly,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$profileRoot = Join-Path $root ".codex-testenv"
$tmpRoot = Join-Path $profileRoot "tmp"
$appDataRoot = Join-Path $profileRoot "appdata"
$localAppDataRoot = Join-Path $profileRoot "localappdata"
$homeRoot = Join-Path $profileRoot "home"

foreach ($dir in @($profileRoot, $tmpRoot, $appDataRoot, $localAppDataRoot, $homeRoot)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

$env:TEMP = $tmpRoot
$env:TMP = $tmpRoot
$env:APPDATA = $appDataRoot
$env:LOCALAPPDATA = $localAppDataRoot
$env:HOME = $homeRoot

$unitProject = Join-Path $root "OrchestratorIDE.UnitTests\OrchestratorIDE.UnitTests.csproj"
$headlessProject = Join-Path $root "OrchestratorIDE.Avalonia.HeadlessTests\OrchestratorIDE.Avalonia.HeadlessTests.csproj"

$unitDll = Join-Path $root "OrchestratorIDE.UnitTests\bin\Debug\net10.0-windows\OrchestratorIDE.UnitTests.dll"
$headlessDll = Join-Path $root "OrchestratorIDE.Avalonia.HeadlessTests\bin\Debug\net10.0\OrchestratorIDE.Avalonia.HeadlessTests.dll"

$scratchBuildRoot = Join-Path $root ".codex-scratch-build"

function New-ScratchBuildProps {
    param(
        [Parameter(Mandatory = $true)][string]$SuiteLabel
    )

    $suiteRoot = Join-Path $scratchBuildRoot $SuiteLabel
    New-Item -ItemType Directory -Force -Path $suiteRoot | Out-Null
    $propsPath = Join-Path $suiteRoot "scratch-build.props"

    @'
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$(RepoScratchRoot)obj\$(MSBuildProjectName)\</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
    <BaseOutputPath>$(RepoScratchRoot)bin\$(MSBuildProjectName)\</BaseOutputPath>
    <DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)\obj\**;$(MSBuildProjectDirectory)\bin\**</DefaultItemExcludes>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path $propsPath -Encoding utf8

    return @{
        PropsPath = $propsPath
        RootPath  = "$suiteRoot\"
    }
}

function Invoke-TestCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Command
    )

    Write-Host ""
    Write-Host "[$Label]" -ForegroundColor Cyan
    Write-Host $Command -ForegroundColor DarkGray
    Invoke-Expression $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (exit $LASTEXITCODE)."
    }
}

function Run-ProjectSuite {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$ExistingDll
    )

    if ($UseVsTestOnly) {
        if (-not (Test-Path $ExistingDll)) {
            throw "$Label requested vstest-only mode, but no built test assembly exists at $ExistingDll"
        }

        Invoke-TestCommand -Label $Label -Command "dotnet vstest `"$ExistingDll`""
        return
    }

    $args = @("dotnet", "test", "`"$ProjectPath`"")
    if ($NoRestore) { $args += "--no-restore" }
    if ($UseScratchBuildPaths) {
        $scratch = New-ScratchBuildProps -SuiteLabel $Label
        $args += "/p:DirectoryBuildPropsPath=`"$($scratch.PropsPath)`""
        $args += "/p:RepoScratchRoot=`"$($scratch.RootPath)`""
    }
    $command = $args -join " "

    try {
        Invoke-TestCommand -Label $Label -Command $command
    }
    catch {
        Write-Host "$Label dotnet test failed; trying vstest against the existing built assembly." -ForegroundColor Yellow
        if (-not (Test-Path $ExistingDll)) {
            throw
        }
        Invoke-TestCommand -Label "$Label fallback" -Command "dotnet vstest `"$ExistingDll`""
    }
}

$requested = switch ($Suite) {
    "Unit"     { @("Unit") }
    "Headless" { @("Headless") }
    default    { @("Unit", "Headless") }
}

try {
    foreach ($item in $requested) {
        switch ($item) {
            "Unit" {
                Run-ProjectSuite -Label "UnitTests" -ProjectPath $unitProject -ExistingDll $unitDll
            }
            "Headless" {
                Run-ProjectSuite -Label "HeadlessTests" -ProjectPath $headlessProject -ExistingDll $headlessDll
            }
        }
    }

    Write-Host ""
    Write-Host "All requested suites passed under .codex-testenv." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
