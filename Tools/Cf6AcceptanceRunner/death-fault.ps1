<#
.SYNOPSIS
  Fault injector for cf6-acceptance-runner --death-test: suspends or resumes the
  OrchestratorIDE worker process on a remote HIVE machine over SSH.

.DESCRIPTION
  Suspension (NtSuspendProcess) rather than kill is deliberate: the Warchief cannot tell a
  suspended worker from a dead one (heartbeats just stop), but a suspended worker can be
  resumed afterwards to play the "presumed-dead node comes back and submits its stale
  result" half of the CF-6 exit gate, and the fleet needs no manual app relaunch.

  The worker-id to ssh-target mapping lives in a JSON map file supplied at run time
  (machine addresses are deliberately NOT committed with this script):
      { "HARDCOREPC": "user@100.x.y.z", "HARDCORELAPTOPM": "user@192.168.x.y" }
  A worker id with no mapping is REFUSED (exit 2). That refusal is the safety net that
  keeps the fault away from the Warchief host and any machine the operator didn't
  explicitly list; never add a fallback.

  NOTE: this file must stay pure ASCII. The acceptance runner invokes it with Windows
  PowerShell 5.1 (powershell.exe), which reads UTF-8-without-BOM scripts as ANSI; a
  UTF-8 em dash ends in byte 0x94 = a Windows-1252 closing quote, which terminates a
  double-quoted string mid-line and turns the whole file into parse errors (hit live,
  2026-07-01).

.EXAMPLE
  .\death-fault.ps1 -Action suspend -Worker HARDCOREPC -MapFile C:\evidence\worker-map.json
#>
param(
    [Parameter(Mandatory)][ValidateSet('suspend', 'resume')][string]$Action,
    [Parameter(Mandatory)][string]$Worker,
    [string]$MapFile = ''
)
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not populate $PSScriptRoot while evaluating param() defaults,
# so the script-adjacent default is resolved here instead.
if ([string]::IsNullOrEmpty($MapFile)) {
    $MapFile = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'worker-map.json'
}

if (-not (Test-Path $MapFile)) {
    [Console]::Error.WriteLine("Map file '$MapFile' not found.")
    exit 2
}
$map = Get-Content $MapFile -Raw | ConvertFrom-Json
$entry = $map.PSObject.Properties | Where-Object { $_.Name -ieq $Worker } | Select-Object -First 1
if (-not $entry) {
    [Console]::Error.WriteLine("Worker '$Worker' has no ssh mapping in '$MapFile' - refusing. (Only explicitly mapped machines may receive a fault; the Warchief host must never be listed.)")
    exit 2
}
$target = $entry.Value

$fn = if ($Action -eq 'suspend') { 'NtSuspendProcess' } else { 'NtResumeProcess' }
$remote = @"
`$sig = '[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h); [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);'
Add-Type -Namespace DeathTest -Name Native -MemberDefinition `$sig
`$procs = @(Get-Process OrchestratorIDE -ErrorAction Stop)
foreach (`$p in `$procs) {
    [DeathTest.Native]::$fn(`$p.Handle) | Out-Null
    Write-Output "$Action pid=`$(`$p.Id) on `$env:COMPUTERNAME"
}
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remote))

ssh -o BatchMode=yes -o ConnectTimeout=10 $target "powershell -NoProfile -EncodedCommand $encoded"
exit $LASTEXITCODE
