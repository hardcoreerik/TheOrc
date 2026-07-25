@echo off
REM Warchief (pairing + task queue) for HIVE validation campaigns on NewcorePC.
REM
REM Mirrors start-worker.bat's shape deliberately: launching swarmcli directly from an
REM interactive shell does NOT survive -- the process is reaped when the launching shell's
REM process tree ends, which killed the Warchief mid-run twice during the HV-3 campaign and
REM produced a false "heartbeat timeout" failure (the worker simply had nothing to heartbeat
REM to). Launch this file detached instead.
REM
REM --allow-fingerprint entries are the fleet workers' HIVE fingerprints, so a worker whose
REM identity churned can re-pair headlessly. Re-read them with `theorc-warband --show-identity`
REM ON THE WORKER if they ever change; never run that against a GUI machine.
cd /d F:\Ai\OrchestratorIDE-dev\Tools\SwarmCli\bin\Release\net10.0-windows
swarmcli.exe --warchief --no-run --port 7079 --timeout 7200 ^
  --allow-fingerprint "dusk-bough-jade-fawn-stone-butte-jade-canopy" ^
  --allow-fingerprint "granite-castle-vent-flume-steppe-moss-atlas-peat" ^
  > F:\Ai\OrchestratorIDE-dev\warchief.log 2>&1
