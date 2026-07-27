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
REM --timeout is a SELF-TERMINATE, not an idle timeout: swarmcli exits the moment it elapses,
REM mid-campaign, with a clean "Shutting down..." that looks nothing like a failure. A 7200s
REM value silently ended a session at exactly the two-hour mark and the resulting symptom --
REM workers no longer leasing -- was misread twice as worker/pairing trouble. Size this to the
REM whole working session, not to one phase.
REM `start` is load-bearing, not cosmetic: it gives swarmcli its OWN console. Without it the
REM Warchief inherits the launching shell's console and dies on that console's Ctrl+C -- which
REM is what an interrupted tool call or cancelled command sends. Observed repeatedly during
REM HV-3: warchief.log ends in a bare "^C" every time, and the downstream symptoms (workers
REM stop leasing, heartbeats 401, pairing reports hiveid_mismatch) all read as HIVE faults when
REM they were just a dead Warchief. Ctrl+C into one console does not reach the other.
REM Heartbeat receipt diagnostics: logs every arriving beat with its outcome (credited /
REM not-claimed / stale-token). Off by default and one cached bool read per beat when off, so it
REM is left ON here for the duration of the HV-3 heartbeat investigation -- the queue now also
REM states at startup which way this is set, because an empty receipt log otherwise cannot be
REM distinguished from a log that was never switched on.
set "THEORC_HIVE_HEARTBEAT_DIAGNOSTICS=1"

REM Two passes over one file rather than a nested `start ... cmd /c "swarmcli.exe ... > log"`.
REM That form does not survive its own quoting: the flags carry quoted fingerprints, so the inner
REM command needs doubled quotes, and cmd then strips the wrong pair -- the launched console came
REM up in the wrong directory and wrote `'swarmcli.exe' is not recognized` into warchief.log,
REM which reads exactly like a missing build. Re-entering this same script means the command line
REM is written once, quoted once, and the `cd /d` below is the only thing that decides where it
REM runs.
if not "%~1"=="run" (
    start "TheOrcWarchief" /min cmd /c ""%~f0" run"
    exit /b
)

cd /d F:\Ai\OrchestratorIDE-dev\Tools\SwarmCli\bin\Release\net10.0-windows
REM The redirect has to live on the swarmcli line itself, inside the new console, so a Warchief
REM that printed usage and exited is distinguishable from a healthy one.
REM Invoked by FULL PATH even though we just cd'd here: with NoDefaultCurrentDirectoryInExePath
REM set (it is, in some shells this gets launched from), cmd drops "." from the executable search
REM and a bare `swarmcli.exe` fails with `is not recognized` -- indistinguishable in the log from
REM a missing build, and it cost a launch cycle. The `cd /d` still matters: it is the working
REM directory swarmcli resolves its state against.
"F:\Ai\OrchestratorIDE-dev\Tools\SwarmCli\bin\Release\net10.0-windows\swarmcli.exe" --warchief --no-run --port 7079 --timeout 86400 ^
  --allow-fingerprint "dusk-bough-jade-fawn-stone-butte-jade-canopy" ^
  --allow-fingerprint "granite-castle-vent-flume-steppe-moss-atlas-peat" ^
  > F:\Ai\OrchestratorIDE-dev\warchief.log 2>&1
