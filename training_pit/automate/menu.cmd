@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  ORC ACADEMY — HARDCOREPC Automate Menu
::  Drop this folder on your Desktop, run menu.cmd
:: ============================================================

:: Load config
for /f "usebackq tokens=1,2 delims==" %%A in ("%~dp0config.env") do (
    if not "%%A"=="" if not "%%A:~0,1%"=="#" set "%%A=%%B"
)

set SCRIPT_DIR=%~dp0
set PYTHON=python

:MENU
cls
echo.
echo  ===========================================
echo    ORC ACADEMY  ^|  HARDCOREPC Automate
echo    GPU: 6GB VRAM   Main: %MAIN_HOST%
echo  ===========================================
echo.
echo   [1]  SCOUT Goals         Generate fresh goal batch
echo   [2]  Farm Goals          Run goals through remote boss
echo   [3]  Night Harvest       Automated overnight gen+farm
echo   [4]  Synthetic SFT Gen   Generate boss plan examples
echo   [5]  QLoRA 7B Training   Fine-tune coder adapter (local)
echo   [6]  Quick Eval          Score adapter on 20 examples
echo   [7]  Dataset Stats       Health check on current dataset
echo   [8]  Review Batch        Interactive plan review helper
echo   [9]  Sync to Main        Push results to main machine
echo   [0]  Exit
echo.
choice /c 1234567890 /n /m "  Select: "
set OPT=%errorlevel%

if %OPT%==1  goto SCOUT_GOALS
if %OPT%==2  goto FARM_GOALS
if %OPT%==3  goto NIGHT_HARVEST
if %OPT%==4  goto SYNTH_GEN
if %OPT%==5  goto QLORA_TRAIN
if %OPT%==6  goto QUICK_EVAL
if %OPT%==7  goto DATASET_STATS
if %OPT%==8  goto REVIEW_BATCH
if %OPT%==9  goto SYNC_MAIN
if %OPT%==10 goto EXIT
goto MENU

:: ──────────────────────────────────────────────────────────
:SCOUT_GOALS
cls
echo.
echo  [SCOUT GOALS]
echo  Generating %GOALS_PER_BATCH% goals using %GEN_MODEL% on local GPU...
echo.
pwsh -ExecutionPolicy Bypass -File "%SCRIPT_DIR%01_scout_goals.ps1" ^
    -GoalsPerBatch %GOALS_PER_BATCH% ^
    -GenModel "%GEN_MODEL%" ^
    -LocalHost "%LOCAL_HOST%" ^
    -Workspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:FARM_GOALS
cls
echo.
echo  [FARM GOALS]
echo  Runs last goals batch through boss at %MAIN_HOST%
echo.
set /p GOALSFILE="  Goals PSV file path (or press Enter for latest): "
pwsh -ExecutionPolicy Bypass -File "%SCRIPT_DIR%02_farm_remote.ps1" ^
    -GoalsFile "%GOALSFILE%" ^
    -MainHost "%MAIN_HOST%" ^
    -BossModel "%BOSS_MODEL%" ^
    -Workspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:NIGHT_HARVEST
cls
echo.
echo  [NIGHT HARVEST]
set /p HOURS="  Run for how many hours? (default 8): "
if "%HOURS%"=="" set HOURS=8
set /p GOALS_PER_CYCLE="  Goals per cycle? (default 25): "
if "%GOALS_PER_CYCLE%"=="" set GOALS_PER_CYCLE=25
echo.
echo  Starting harvest for %HOURS% hours, %GOALS_PER_CYCLE% goals/cycle...
echo  Create HARVEST_STOP file in workspace to stop cleanly.
echo.
pwsh -ExecutionPolicy Bypass -File "%SCRIPT_DIR%03_night_harvest.ps1" ^
    -Hours %HOURS% ^
    -GoalsPerCycle %GOALS_PER_CYCLE% ^
    -GenModel "%GEN_MODEL%" ^
    -MainHost "%MAIN_HOST%" ^
    -BossModel "%BOSS_MODEL%" ^
    -Workspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:SYNTH_GEN
cls
echo.
echo  [SYNTHETIC SFT GENERATION]
set /p COUNT="  How many examples to generate? (default 50): "
if "%COUNT%"=="" set COUNT=50
echo.
echo  Generating %COUNT% synthetic boss plan examples using %SYNTH_MODEL%...
%PYTHON% "%SCRIPT_DIR%04_synthetic_gen.py" ^
    --count %COUNT% ^
    --model "%SYNTH_MODEL%" ^
    --host "%LOCAL_HOST%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:QLORA_TRAIN
cls
echo.
echo  [QLORA 7B TRAINING — LOCAL GPU]
echo  Target: %QLORA_BASE%
echo  Output: %QLORA_ADAPTER_OUT%
echo.
set /p EPOCHS="  Epochs? (default 3): "
if "%EPOCHS%"=="" set EPOCHS=3
set /p TRAIN_FILE="  Train JSONL path: "
echo.
%PYTHON% "%SCRIPT_DIR%05_qlora_7b.py" ^
    --base "%QLORA_BASE%" ^
    --train "%TRAIN_FILE%" ^
    --out "%QLORA_ADAPTER_OUT%" ^
    --epochs %EPOCHS%
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:QUICK_EVAL
cls
echo.
echo  [QUICK EVAL — 20 examples]
set /p ADAPTER="  Adapter path (local or remote): "
set /p EVAL_FILE="  Eval JSONL path: "
echo.
%PYTHON% "%SCRIPT_DIR%06_eval_subset.py" ^
    --adapter "%ADAPTER%" ^
    --eval "%EVAL_FILE%" ^
    --limit 20
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:DATASET_STATS
cls
echo.
echo  [DATASET STATS]
%PYTHON% "%SCRIPT_DIR%07_dataset_stats.py" ^
    --workspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:REVIEW_BATCH
cls
echo.
echo  [REVIEW BATCH]
%PYTHON% "%SCRIPT_DIR%08_review_batch.py" ^
    --workspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:SYNC_MAIN
cls
echo.
echo  [SYNC TO MAIN]
echo  Copies local work (goals, synthetic SFT) to main workspace...
pwsh -ExecutionPolicy Bypass -File "%SCRIPT_DIR%09_sync_results.ps1" ^
    -LocalWorkspace "%LOCAL_WORKSPACE%" ^
    -MainWorkspace "%WORKSPACE%"
echo.
pause
goto MENU

:: ──────────────────────────────────────────────────────────
:EXIT
echo.
echo  Goodbye.
exit /b 0
