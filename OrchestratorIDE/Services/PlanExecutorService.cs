// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services;

/// <summary>
/// Drives the two-phase Pit Boss execution pipeline:
///
///   Phase 1 — Dataset generation
///     Spawns generate_cerebras_gold.py (or generate_ollama_gold.py) with
///     --plan-file and --out-file derived from the TrainingPlan. Monitors the
///     progress JSON for line counts; fires ProgressUpdated periodically.
///     When the process exits cleanly, renames the .work file to the
///     proper naming convention and fires DatasetReady.
///
///   Phase 2 — Forge handoff
///     Fires ForgeReady(plan, datasetPath, hfRepo) — the caller (MainWindow /
///     PitBossPanel) navigates to the Training Pit and calls
///     TrainingPitPanel.LaunchFromPlan(plan) which pre-fills and starts Forge.
///
/// The executor survives PitBossPanel destruction — it holds its own
/// CancellationTokenSource so Back-navigation doesn't kill a running gen.
/// </summary>
public sealed class PlanExecutorService : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Fires every ~5 s during dataset gen with (linesWritten, totalTarget, phase).</summary>
    public event Action<int, int, string>? ProgressUpdated;
    /// <summary>Dataset gen complete — path is the renamed, final .jsonl file.</summary>
    public event Action<string>?           DatasetReady;
    /// <summary>Ready to start Forge training with this plan and dataset path.</summary>
    public event Action<TrainingPlan, string>? ForgeReady;
    /// <summary>Something went wrong.</summary>
    public event Action<string>?           Failed;
    /// <summary>One-line log output from the gen process.</summary>
    public event Action<string>?           LogLine;

    // ── State ─────────────────────────────────────────────────────────────────
    public bool IsRunning  { get; private set; }
    public ExecutorPhase Phase { get; private set; } = ExecutorPhase.Idle;

    /// <summary>
    /// Optional SQL run-history target (Phase 2). Set once at startup.
    /// When non-null, every execution writes a run row. Best-effort — a DB failure
    /// never affects dataset gen or the swarm run.
    /// </summary>
    public Data.RunRepository? RunRepo { get; set; }

    private TrainingPlan?          _plan;
    private string                 _pitRoot      = "";
    private string                 _workFile     = "";
    private string                 _progressFile = "";
    private Process?               _genProcess;
    private CancellationTokenSource _cts = new();
    private string?                _currentRunId;

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task StartAsync(TrainingPlan plan, string pitRoot)
    {
        if (IsRunning) return;

        _plan    = plan;
        _pitRoot = pitRoot;
        IsRunning = true;
        Phase    = ExecutorPhase.GeneratingDataset;

        // Save plan JSON so the Python script can read it
        PitBossService.SavePlan(plan, pitRoot);
        var planFile = Path.Combine(pitRoot, "training_pit", "plans", plan.PlanFileName);

        // Work output file — must be assigned before TryInsertRun so LogPath is correct.
        var dsDir    = Path.Combine(pitRoot, "training_pit", "datasets");
        Directory.CreateDirectory(dsDir);
        _workFile     = Path.Combine(dsDir, $"{plan.PlanId}.work.jsonl");
        _progressFile = Path.Combine(dsDir, $"{plan.PlanId}.progress.json");

        // Clean stale work file from previous aborted run
        if (File.Exists(_workFile))     File.Delete(_workFile);
        if (File.Exists(_progressFile)) File.Delete(_progressFile);

        // Phase 2: open a run row after _workFile is set so log_path is correct.
        _currentRunId = $"run_{plan.PlanId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        TryInsertRun(plan);

        // Short-circuit for existing-dataset plans — skip generation entirely.
        if (plan.Mode == "existing" || plan.DatasetSource == "existing")
        {
            var existingPath = ResolveExistingDataset(plan, pitRoot);
            if (existingPath is null)
            {
                TryUpdateRun("failed");
                Failed?.Invoke($"Existing dataset '{plan.DatasetFile}' not found in training_pit/datasets/. " +
                               "Expected train_{key}.jsonl — check the dataset file name.");
                IsRunning = false;
                Phase = ExecutorPhase.Failed;
                return;
            }
            TryUpdateRun("dataset_ready");
            DatasetReady?.Invoke(existingPath);
            ForgeReady?.Invoke(plan, existingPath);
            IsRunning = false;
            Phase = ExecutorPhase.Idle;
            return;
        }

        try
        {
            await RunDatasetGenAsync(plan, planFile, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryUpdateRun("cancelled");
            Phase = ExecutorPhase.Idle;
            IsRunning = false;
        }
        catch (Exception ex)
        {
            TryUpdateRun("failed");
            Phase = ExecutorPhase.Failed;
            IsRunning = false;
            Failed?.Invoke($"Dataset gen failed: {ex.Message}");
        }
    }

    public void Cancel()
    {
        TryUpdateRun("cancelled");
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        try { _genProcess?.Kill(entireProcessTree: true); } catch { }
        _genProcess = null;
        Phase = ExecutorPhase.Idle;
        IsRunning = false;
    }

    // ── Phase 1: Dataset generation ───────────────────────────────────────────

    private async Task RunDatasetGenAsync(TrainingPlan plan, string planFile, CancellationToken ct)
    {
        // Locate the generator script
        var genScript = ResolveGenScript(plan, _pitRoot);
        if (genScript is null)
        {
            TryUpdateRun("failed");
            Failed?.Invoke($"Cannot find generation script for source '{plan.DatasetSource}'. " +
                           "Expected tools/generate_cerebras_gold.py or tools/generate_ollama_gold.py.");
            IsRunning = false;
            Phase = ExecutorPhase.Failed;
            return;
        }

        // Build arg string
        var scriptArgs = $"-u \"{genScript}\"" +
                         $" --plan-file \"{planFile}\"" +
                         $" --out-file \"{_workFile}\"" +
                         $" --model \"{plan.DatasetGenModel}\"";

        // Log file beside the work file
        var logFile = _workFile.Replace(".work.jsonl", ".gen.log");
        File.WriteAllText(logFile, $"=== Pit Boss gen run {DateTime.Now:yyyy-MM-dd HH:mm} ===\n" +
                                   $"Goal: {plan.Goal}\n" +
                                   $"Script: {genScript}\n\n");

        var psi = new ProcessStartInfo
        {
            FileName         = "cmd.exe",
            Arguments        = $"/c python {scriptArgs} >> \"{logFile}\" 2>&1",
            WorkingDirectory = _pitRoot,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };

        _genProcess = Process.Start(psi);
        if (_genProcess is null)
        {
            TryUpdateRun("failed");
            Failed?.Invoke("Failed to launch Python generator process.");
            IsRunning = false;
            Phase = ExecutorPhase.Failed;
            return;
        }

        LogLine?.Invoke($"[gen] PID {_genProcess.Id} — {Path.GetFileName(genScript)}");

        // Monitor progress until process exits
        await MonitorGenProgressAsync(plan.DatasetTarget, ct);
    }

    private async Task MonitorGenProgressAsync(int target, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5_000, ct);

            // Count lines in the work file as the live written count
            int written = CountLines(_workFile);
            ProgressUpdated?.Invoke(written, target, "Generating dataset…");
            LogLine?.Invoke($"[gen] {written}/{target} examples written");

            // Check if process has exited
            if (_genProcess is null || _genProcess.HasExited)
                break;
        }

        ct.ThrowIfCancellationRequested();

        int exitCode = _genProcess?.ExitCode ?? 0;
        _genProcess  = null;

        if (exitCode != 0)
        {
            TryUpdateRun("failed");
            Failed?.Invoke($"Generator exited with code {exitCode}. Check the .gen.log file next to the dataset.");
            IsRunning = false;
            Phase = ExecutorPhase.Failed;
            return;
        }

        // Rename work file to proper convention
        var finalPath = RenameWorkFile(_workFile, _plan!);
        // Update mutable state fields on the plan
        _plan!.DatasetFile = finalPath;
        _plan.Phase        = PlanPhase.Training;
        PitBossService.SavePlan(_plan, _pitRoot);
        TryUpdateRun("complete", finalPath);

        int finalCount = CountLines(finalPath);
        ProgressUpdated?.Invoke(finalCount, finalCount, "Dataset complete");
        DatasetReady?.Invoke(finalPath);
        LogLine?.Invoke($"[gen] Complete: {finalCount} examples → {Path.GetFileName(finalPath)}");

        Phase = ExecutorPhase.WaitingForForge;
        IsRunning = false;

        // Signal Forge handoff
        ForgeReady?.Invoke(_plan, finalPath);
    }

    // ── Run-history helpers ───────────────────────────────────────────────────

    private void TryInsertRun(TrainingPlan plan)
    {
        var repo = RunRepo;
        if (repo is null || _currentRunId is null) return;
        try
        {
            repo.Upsert(new Data.RunRecord(
                RunId:        _currentRunId,
                PlanId:       plan.PlanId,
                Kind:         "dataset_gen",
                Status:       "running",
                StartedAt:    DateTime.UtcNow.ToString("o"),
                EndedAt:      null,
                Host:         System.Environment.MachineName,
                ArtifactPath: null,
                MetricsJson:  null,
                LogPath:      _workFile.Replace(".work.jsonl", ".gen.log")));
        }
        catch { }
    }

    private void TryUpdateRun(string status, string? artifactPath = null)
    {
        var repo = RunRepo;
        if (repo is null || _currentRunId is null) return;
        try { repo.UpdateStatus(_currentRunId, status, artifactPath); }
        catch { }
        finally { _currentRunId = null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ResolveExistingDataset(TrainingPlan plan, string pitRoot)
    {
        var dsDir = Path.Combine(pitRoot, "training_pit", "datasets");
        var key   = plan.DatasetFile?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return null;

        // Old-convention: dataset_file is the stem key, e.g. "v2gold" → train_v2gold.jsonl
        var byKey = Path.Combine(dsDir, $"train_{key}.jsonl");
        if (File.Exists(byKey)) return byKey;

        // User may have typed the full filename
        var direct = Path.Combine(dsDir, key);
        if (File.Exists(direct)) return direct;

        return null;
    }

    private static string? ResolveGenScript(TrainingPlan plan, string pitRoot)
    {
        var toolsDir = Path.Combine(pitRoot, "tools");
        if (!Directory.Exists(toolsDir))
            toolsDir = Path.Combine(pitRoot, "Tools");

        var candidates = plan.DatasetSource switch
        {
            "cerebras" => new[] { "generate_cerebras_gold.py" },
            "ollama"   => new[] { "generate_ollama_gold.py" },
            _          => new[] { "generate_cerebras_gold.py", "generate_ollama_gold.py" },
        };

        foreach (var name in candidates)
        {
            var p = Path.Combine(toolsDir, name);
            if (File.Exists(p)) return p;
            // Try root
            p = Path.Combine(pitRoot, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string RenameWorkFile(string workFile, TrainingPlan plan)
    {
        // Count lines to get the actual n
        int n = CountLines(workFile);

        // Build canonical name: {source}[{ctx}].synthetic.{role}.{n}.jsonl
        var source  = plan.DatasetSource switch { "cerebras" => "cerebras", "ollama" => "ollama", _ => "pit" };
        var ctx     = plan.DatasetSource == "cerebras" ? "api"
                    : plan.DatasetSource == "ollama"   ? "local"
                    : "gen";
        var finalName = $"{source}[{ctx}].synthetic.boss.{n}.jsonl";
        var dir       = Path.GetDirectoryName(workFile) ?? "";
        var finalPath = Path.Combine(dir, finalName);

        // If target already exists (from a prior run), append the plan id to avoid collision
        if (File.Exists(finalPath))
            finalPath = Path.Combine(dir, $"{source}[{ctx}].synthetic.boss.{n}.{plan.PlanId}.jsonl");

        File.Move(workFile, finalPath, overwrite: false);
        return finalPath;
    }

    private static int CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        try { return File.ReadLines(path).Count(l => l.Trim().Length > 0); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        try { _genProcess?.Kill(entireProcessTree: true); } catch { }
    }
}

public enum ExecutorPhase
{
    Idle,
    GeneratingDataset,
    WaitingForForge,
    Training,
    Complete,
    Failed,
}
