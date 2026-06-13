using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Swarm;
using OrchestratorIDE.Services.ToolCalls;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Agents;

/// <summary>
/// Orchestrates a 1-boss + up-to-4-worker Swarm run.
///
/// Phase 1:  Boss decomposes the user's goal into RESEARCHER / CODER / UIDEVELOPER / TESTER tasks (JSON).
/// Phase 2a: RESEARCHER tasks run first (parallel) — coders depend on their findings.
/// Phase 2b: CODER + UIDEVELOPER + planned TESTER tasks run concurrently with research context injected.
///           TESTER tasks are verification-only — they never write files and are exempt from file-write retries.
/// Phase 3:  Auto Tester goblin verifies files produced by CODER/UIDEVELOPER this run (skipped if none).
/// Phase 4:  Boss merges all worker results (including TESTER verdicts) into final_report.md.
///
/// Worker output files are written to a staging area at <runDir>/staging/ so they
/// can be reviewed before being applied to the workspace. Run metadata (plan.json,
/// trace, final_report.md) and the staged files both live under <runDir>/.
/// The OnStagingReady event fires when files are ready for the user to review.
/// </summary>
public class SwarmSession
{
    private readonly OllamaClient _ollama;
    private readonly string       _bossModel;
    private readonly string       _coderModel;
    private readonly string       _researcherModel;
    private readonly string?      _workspaceRoot;
    private CancellationTokenSource? _cts;
    private SwarmTraceLogger?     _trace;
    private string                _runId = "";
    private string                _targetLanguage = "";
    private ToolRegistry?         _toolRegistry;    // initialized once output dir is known
    private int                   _hardwareVramGb;  // detected at run start via nvidia-smi

    // ── Co-Work state ─────────────────────────────────────────────────────────
    // taskId → TCS that unblocks the ask_user handler when the user replies
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>
        _replyChannels = new();
    // taskId → queue of steer messages to inject at the start of the next LLM step
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.Queue<string>>
        _steerQueues = new();

    // ── HIVE MIND: per-task node routing (Phase B) ────────────────────────────
    private IReadOnlyList<Services.Hive.HiveHost>? _hiveHosts;
    private string _localOllamaUrl = "";
    // Cache OllamaClient per remote URL so we don't create a new one per task step.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OllamaClient>
        _nodeClients = new();

    // ── HIVE MIND: distributed swarm queue (Phase 3) ──────────────────────────
    private Services.Hive.HiveTaskQueue? _distributedQueue;
    private string _currentUserGoal = "";

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action?                         OnStarted;
    public event Action?                         OnStopped;
    public event Action<string>?                 OnBossToken;
    public event Action<string, string>?         OnWorkerToken;    // taskId, token
    public event Action<List<SwarmTask>>?        OnTasksPlanned;
    public event Action<SwarmTask>?              OnTaskChanged;
    public event Action<string>?                 OnSwarmComplete;  // merged result
    public event Action<string>?                 OnError;
    public event Action<string, string>?         OnActivity;       // agentKey, message

    // ── Public state ──────────────────────────────────────────────────────────
    public List<SwarmTask> Tasks   { get; private set; } = [];
    public bool            IsRunning => _cts is { IsCancellationRequested: false };

    public SwarmSession(OllamaClient ollama, string bossModel, string? workspaceRoot,
        string? workerModel = null, string? researcherModel = null)
    {
        _ollama          = ollama;
        _bossModel       = bossModel;
        _coderModel      = workerModel     ?? bossModel;
        _researcherModel = researcherModel ?? _coderModel;
        _workspaceRoot   = workspaceRoot;
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    private string RunsRoot         => Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "swarm", "runs");
    private string RunDir           => Path.Combine(RunsRoot, _runId);
    /// <summary>
    /// Workers write files into a staging area under the run directory.
    /// Files stay here until the user reviews and applies them to the workspace,
    /// preventing the workspace root from being silently overwritten mid-run.
    /// </summary>
    private string OutputProjectDir  => Path.Combine(RunDir, "staging");
    private string AgentsTaskDir     => Path.Combine(RunDir, "agents");
    /// <summary>
    /// Boss plan captures are written here for dataset review.
    /// Files are plan-capture JSON (PLAN_CAPTURE_SCHEMA.md format).
    /// Convert to chat-JSONL via training_pit/scripts/convert_plan_captures.py.
    /// </summary>
    private string DatasetStagingDir => Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "swarm", "dataset-staging");

    /// <summary>Returns the staging dir for the last (or current) run. Empty if no run yet.</summary>
    public string GetOutputProjectDir() => string.IsNullOrEmpty(_runId) ? "" : OutputProjectDir;

    // ── Staging ready event ───────────────────────────────────────────────────

    /// <summary>
    /// Raised when the swarm run completes and files are ready for review in the staging dir.
    /// Args: (runId, stagingDir, files[]).
    /// </summary>
    public event Action<string, string, IReadOnlyList<string>>? OnStagingReady;

    // ── HIVE MIND API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Attach a running HiveTaskQueue so SwarmSession dispatches tasks to remote
    /// workers instead of executing them locally. Call before RunAsync.
    /// When set, all worker phases use distributed dispatch.
    /// </summary>
    public void SetDistributedQueue(Services.Hive.HiveTaskQueue queue)
        => _distributedQueue = queue;

    /// <summary>
    /// Provide the full list of probed hive hosts so tasks can be routed to remote nodes.
    /// Call before RunAsync. If not called, all tasks run on the local node (_ollama).
    /// </summary>
    public void SetHiveHosts(IReadOnlyList<Services.Hive.HiveHost> hosts, string localUrl)
    {
        _hiveHosts     = hosts;
        _localOllamaUrl = localUrl;
    }

    /// <summary>
    /// Returns the OllamaClient for the node assigned to this task.
    /// Caches one client per unique remote URL to avoid reconnect overhead.
    /// </summary>
    private OllamaClient GetOllamaForTask(SwarmTask task)
    {
        var url = task.TargetNodeUrl;
        if (string.IsNullOrEmpty(url) || url == _localOllamaUrl || string.IsNullOrEmpty(_localOllamaUrl))
            return _ollama;
        return _nodeClients.GetOrAdd(url, u => new OllamaClient(u));
    }

    // ── Directive injection ───────────────────────────────────────────────────
    private readonly List<string> _directives = [];

    public void InjectDirective(string directive)
    {
        _directives.Add(directive);
        OnBossToken?.Invoke($"\n\n📌 **USER DIRECTIVE:** {directive}\n\n");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _distributedQueue?.CancelAll();
        OnStopped?.Invoke();
    }

    public Task RunAsync(string userGoal) => RunInternalAsync(userGoal);

    // ── Co-Work API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Provide the user's reply to a worker that called ask_user and is WaitingForUser.
    /// Unblocks the worker so it can continue.
    /// </summary>
    public void ProvideWorkerReply(string taskId, string reply)
    {
        if (_replyChannels.TryRemove(taskId, out var tcs))
            tcs.TrySetResult(reply);
    }

    /// <summary>
    /// Inject a guidance message into a running worker's next LLM step.
    /// The message is queued and consumed at the start of the next tool-calling iteration.
    /// </summary>
    public void SteerWorker(string taskId, string message)
    {
        var q = _steerQueues.GetOrAdd(taskId, _ => new System.Collections.Generic.Queue<string>());
        lock (q) q.Enqueue(message);
        Activity($"💬 User guidance queued for worker {taskId[..Math.Min(8, taskId.Length)]}", "boss");
    }

    /// <summary>
    /// Continue chatting with a worker after its task is complete.
    /// Resumes the worker's conversation history with a new user message.
    /// </summary>
    public async Task ContinueWorkerAsync(string taskId, string userMessage)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null || task.ConversationHistory.Count == 0) return;

        var ct       = _cts?.Token ?? CancellationToken.None;
        var agentKey = AgentKey(task.Role);
        var model    = GetCapableModel(task.Role);   // GOBLIN MIND: capability-aware
        var noThink  = ShouldDisableThinking(model);
        var tools    = GetWorkerTools(task.Role);
        var history  = task.ConversationHistory; // continue from saved history

        // Append the follow-up
        history.Add(new AgentMessage { Role = MessageRole.User, Content = userMessage, Status = MessageStatus.Complete });
        task.Status = SwarmTaskStatus.InProgress;
        OnTaskChanged?.Invoke(task);
        Activity($"💬 Follow-up from user: {userMessage[..Math.Min(80, userMessage.Length)]}", agentKey);

        try
        {
            var nodeOllama = GetOllamaForTask(task);
            await RunWorkerLoopAsync(task, history, tools, model, noThink, agentKey, ct, nodeOllama);
        }
        finally
        {
            task.Status      = SwarmTaskStatus.Done;
            task.CompletedAt = DateTime.UtcNow;
            OnTaskChanged?.Invoke(task);
        }
    }

    // ── Orchestration ─────────────────────────────────────────────────────────

    private async Task RunInternalAsync(string userGoal)
    {
        _cts             = new CancellationTokenSource();
        var ct           = _cts.Token;
        Tasks            = [];
        var runStartedAt = DateTime.UtcNow;
        _runId           = runStartedAt.ToString("yyyyMMdd_HHmmss");
        _targetLanguage  = DetectTargetLanguage(userGoal);
        _currentUserGoal = userGoal;

        // Detect hardware VRAM so run metrics are accurate
        var hw = await OrchestratorIDE.Services.Swarm.SwarmConfigAdvisor.DetectHardwareAsync();
        _hardwareVramGb = hw.TotalVramGb;
        OnStarted?.Invoke();

        Directory.CreateDirectory(RunDir);
        // OutputProjectDir is now the workspace root — already exists, no need to create
        if (OutputProjectDir != _workspaceRoot) Directory.CreateDirectory(OutputProjectDir);
        Directory.CreateDirectory(AgentsTaskDir);

        // Initialise the worker tool registry now that OutputProjectDir is known
        InitWorkerTools();

        _trace = new SwarmTraceLogger(RunDir);
        _trace.WriteSessionMeta(_workspaceRoot ?? RunDir, _bossModel, _coderModel, _researcherModel, userGoal);

        try
        {
            // ── Phase 1: Boss decomposes ──────────────────────────────────────
            Activity("Boss decomposing goal into tasks…", "boss");
            _trace.WriteEvent("phase_start", "Boss planning");
            _trace.WriteUserMessage(userGoal, context: "user_goal");

            var bossRaw = await RunBossDecomposeAsync(userGoal, ct);
            Tasks = ParseBossPlan(bossRaw);

            if (Tasks.Count == 0)
            {
                OnError?.Invoke("Boss returned no tasks — try rephrasing your goal.");
                return;
            }

            // ── Phase 2: Dataset capture ──────────────────────────────────────
            // Stage qualifying boss plans for the Training Pit dataset.
            // Best-effort: StageAsync swallows all exceptions internally.
            await DatasetCapture.StageAsync(_runId, userGoal, bossRaw, Tasks, _bossModel, DatasetStagingDir);

            _trace.WriteAssistantMessage(bossRaw, agent: "boss", model: _bossModel);
            _trace.WriteEvent("tasks_planned", $"{Tasks.Count} task(s): " +
                string.Join(", ", Tasks.Select(t => $"{t.Role}:{t.Title}")));

            Activity($"Planned {Tasks.Count} task(s): {string.Join(", ", Tasks.Select(t => t.Title))}", "boss");
            OnTasksPlanned?.Invoke(Tasks);

            // HIVE MIND Phase 3: update session context now that we know models + language.
            if (_distributedQueue is not null)
            {
                _distributedQueue.UpdateSessionContext(new Services.Hive.HiveSessionContext
                {
                    SessionId       = _runId,
                    ProjectGoal     = userGoal,
                    TargetLanguage  = _targetLanguage,
                    CoderModel      = _coderModel,
                    ResearcherModel = _researcherModel,
                });
                var activeWorkers = _distributedQueue.IsListening ? "queue open" : "queue not listening";
                Activity($"🐝 HIVE MIND distributed mode — {activeWorkers}. Tasks will be dispatched to worker nodes.", "boss");
            }

            // HIVE MIND Phase B: assign tasks to remote nodes when multiple nodes are alive.
            if (_hiveHosts is { Count: > 1 } && !string.IsNullOrEmpty(_localOllamaUrl))
            {
                Services.Hive.HiveScheduler.AssignNodes(Tasks, _hiveHosts, _localOllamaUrl);
                var routed = Tasks.Count(t => !string.IsNullOrEmpty(t.TargetNodeUrl)
                                           && t.TargetNodeUrl != _localOllamaUrl);
                if (routed > 0)
                    Activity($"🐝 HIVE MIND: {routed} task(s) routed to remote node(s) — " +
                             string.Join(", ", Tasks
                                 .Where(t => !string.IsNullOrEmpty(t.TargetNodeName))
                                 .Select(t => $"{t.Title} → {t.TargetNodeName}")
                                 .Take(4)), "boss");
            }

            await SavePlanJsonAsync(userGoal);

            // Honor Stop() requested during OnTasksPlanned (e.g. swarmcli --plan-only)
            // before any worker phase starts
            ct.ThrowIfCancellationRequested();

            // ── Phase 2a: Research tasks ──────────────────────────────────────
            var researchers = Tasks.Where(t => t.Role == SwarmWorkerRole.Researcher).ToList();
            var others      = Tasks.Where(t => t.Role != SwarmWorkerRole.Researcher).ToList();

            if (researchers.Count > 0)
            {
                Activity($"Research phase — {researchers.Count} researcher(s) running", "boss");
                _trace.WriteEvent("phase_start", $"Research phase: {researchers.Count} researcher(s)");
                if (_distributedQueue is not null)
                    await Task.WhenAll(researchers.Select(t => DispatchToQueueAsync(t, [], ct)));
                else
                    await Task.WhenAll(researchers.Select(t => RunWorkerAsync(t, [], ct)));

                // Extract any files researchers wrote (documentation, sample data)
                foreach (var t in researchers.Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null))
                {
                    var n = ExtractAndWriteFiles(t.Result!, OutputProjectDir);
                    if (n > 0)
                    {
                        var names = ExtractFileNames(t.Result!);
                        Activity($"→ {string.Join(", ", names)}", "researcher");
                    }
                }
            }

            // Evict researcher model when it differs from coder model, then verify
            if (researchers.Count > 0 && _researcherModel != _coderModel)
            {
                Activity($"♻ Evicting {_researcherModel} from VRAM before coder phase…", "boss");
                _trace?.WriteEvent("model_evict", $"Evicting {_researcherModel}");
                var evicted = await _ollama.EvictAndVerifyAsync(_researcherModel, ct);
                if (evicted)
                {
                    Activity($"✓ {_researcherModel} confirmed evicted — VRAM freed for coder", "boss");
                    _trace?.WriteEvent("model_evict_confirmed", _researcherModel);
                }
                else
                {
                    Activity($"⚠ {_researcherModel} still in VRAM after eviction — may constrain coder", "boss");
                    _trace?.WriteEvent("model_evict_unconfirmed", _researcherModel);
                }
            }

            // ── Researcher quality gate (zero tokens — pure code check) ─────────
            // Prevents silent empty-researcher output from flowing into coder prompts.
            // Catches: ghost responses (""), trivially short outputs, LLM refusals.
            const int MinResearchChars = 150;
            const int MinResearchLines = 3;

            var allFindings = researchers
                .Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null)
                .Select(t => (Title: t.Title, Result: t.Result!))
                .ToList();

            var findings = allFindings.Where(f =>
            {
                var text       = f.Result.Trim();
                var longEnough = text.Length >= MinResearchChars;
                var denseEnough = text.Split('\n').Count(l => l.Trim().Length > 0) >= MinResearchLines;
                var noRefusal  = !System.Text.RegularExpressions.Regex.IsMatch(
                    text, @"\b(cannot|unable to|i don't know|as an ai)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return longEnough && denseEnough && noRefusal;
            }).ToList();

            var ghostCount = allFindings.Count - findings.Count;
            if (ghostCount > 0)
            {
                _trace?.WriteEvent("researcher_quality_fail",
                    $"{ghostCount} researcher(s) returned insufficient output (<{MinResearchChars} chars or <{MinResearchLines} lines) — excluded");
                Activity($"⚠ {ghostCount} researcher(s) returned ghost output — coders proceeding without research context", "boss");
            }

            // ── Phase 2b: Coder + UIDev + planned Tester workers ─────────────────
            // TESTER is verification-only: no write_file, no ### FILE: output expected.
            // Never retry TESTER for zero file output; never run ExtractAndWriteFiles on its output.
            var coderRetryCount = 0;     // hoisted for metrics capture
            if (others.Count > 0)
            {
                var implCount = others.Count(t => t.Role != SwarmWorkerRole.Tester);
                var testCount = others.Count(t => t.Role == SwarmWorkerRole.Tester);
                var phaseDesc = (implCount, testCount) switch
                {
                    ( > 0,  > 0) => $"{implCount} implementation + {testCount} planned tester worker(s)",
                    ( > 0,   0) => $"{implCount} implementation worker(s)",
                    (  0,  > 0) => $"{testCount} planned tester worker(s)",
                    _           => $"{others.Count} worker(s)"
                };
                Activity($"Coder phase — {phaseDesc} running concurrently", "boss");
                _trace?.WriteEvent("phase_start", $"Coder/UIDev/Tester phase: {others.Count} worker(s)");
                if (_distributedQueue is not null)
                    await Task.WhenAll(others.Select(t => DispatchToQueueAsync(t, findings, ct)));
                else
                    await Task.WhenAll(others.Select(t => RunWorkerAsync(t, findings, ct)));

                var totalFiles = 0;
                foreach (var t in others.Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null))
                {
                    // TESTER is verification-only — no file output expected or required.
                    // A completed TESTER with zero files is correct by design; never retry it.
                    if (t.Role == SwarmWorkerRole.Tester) continue;

                    // Count files written via tools during execution
                    totalFiles += t.ToolFilesWritten;

                    var n = ExtractAndWriteFiles(t.Result!, OutputProjectDir);
                    if (n > 0 || t.ToolFilesWritten > 0)
                    {
                        // Log ### FILE: marker files that weren't already logged via write_file
                        if (n > 0)
                        {
                            t.MarkerFilesWritten = n;   // track for workerFiles + metrics
                            var names = ExtractFileNames(t.Result!);
                            Activity($"→ {string.Join(", ", names)}", AgentKey(t.Role));
                        }
                        totalFiles += n;
                        continue;
                    }

                    // ── Boss retry: worker wrote no files via tools OR markers ─
                    for (int attempt = 1; attempt <= MaxRetries; attempt++, coderRetryCount++)
                    {
                        Activity($"⚠ {t.Title} wrote no files — retrying ({attempt}/{MaxRetries})…", "boss");
                        _trace?.WriteEvent("worker_retry", $"{t.Role}:{t.Title} attempt {attempt}");

                        // Reset task state for re-run
                        t.Status          = SwarmTaskStatus.Pending;
                        t.Result          = null;
                        t.ErrorMessage    = null;
                        t.ToolFilesWritten = 0;

                        var retryMsg = BuildRetryUserMessage(t, findings, _targetLanguage, attempt, Tasks);
                        await RunWorkerAsync(t, findings, ct, retryMsg);

                        if (t.Result is null) break;   // error / cancel — stop retrying
                        var retryFiles = ExtractAndWriteFiles(t.Result, OutputProjectDir) + t.ToolFilesWritten;
                        if (retryFiles > 0)
                        {
                            var names = ExtractFileNames(t.Result);
                            if (names.Count > 0)
                                Activity($"→ retry wrote {string.Join(", ", names)}", AgentKey(t.Role));
                            Activity($"→ retry: {retryFiles} file(s) total", AgentKey(t.Role));
                            totalFiles += retryFiles;
                            break;
                        }
                    }
                }

                Activity($"Workers wrote {totalFiles} file(s) to workspace root", "boss");
            }

            // ── Collect planned TESTER verdicts (boss-assigned, ran in Phase 2b) ──
            // These are distinct from the Phase 3 auto tester goblin (which verifies
            // files written this run). Both contribute to testerVerdict for the merge.
            string? plannedTesterVerdict = null;
            {
                var verdicts = others
                    .Where(t => t.Role == SwarmWorkerRole.Tester
                                && t.Status == SwarmTaskStatus.Done
                                && !string.IsNullOrWhiteSpace(t.Result))
                    .Select(t => $"[Planned: {t.Title}] {ExtractTesterSummary(t.Result!)}")
                    .ToList();
                if (verdicts.Count > 0)
                {
                    plannedTesterVerdict = string.Join("\n", verdicts);
                    _trace?.WriteEvent("planned_tester_complete", plannedTesterVerdict);
                    Activity($"🧪 Planned Tester: {plannedTesterVerdict}", "boss");
                }
            }

            // ── Phase 3: Auto Tester goblin — verify files written this run ──────
            // Only runs when CODER/UIDEVELOPER produced files this run (tool calls or markers).
            // Planned TESTER tasks (Phase 2b) verify pre-existing workspace files instead.
            string? testerVerdict   = null;
            bool    fixTaskSpawned  = false;
            bool    fixTaskSucceeded = false;
            var workerFiles = Tasks
                .Where(t => t.Role is SwarmWorkerRole.Coder or SwarmWorkerRole.UIDeveloper
                            && t.Status == SwarmTaskStatus.Done)
                .Sum(t => t.ToolFilesWritten + t.MarkerFilesWritten);

            if (workerFiles > 0 && !ct.IsCancellationRequested)
            {
                Activity("🧪 Tester goblin — verifying output files…", "boss");
                _trace?.WriteEvent("phase_start", "Verification phase");

                var testerTask = BuildTesterTask(userGoal, Tasks, OutputProjectDir, _targetLanguage);
                Tasks.Add(testerTask);
                OnTasksPlanned?.Invoke(Tasks);   // refresh board to show tester card
                await RunWorkerAsync(testerTask, [], ct);

                if (testerTask.Status == SwarmTaskStatus.Done && !string.IsNullOrWhiteSpace(testerTask.Result))
                {
                    testerVerdict = ExtractTesterSummary(testerTask.Result);
                    _trace?.WriteEvent("verification_complete", testerVerdict);
                    Activity($"🧪 Tester: {testerVerdict}", "boss");

                    // ── Boss spawns targeted fix task on FAIL ─────────────────
                    if (TesterReportedFailure(testerTask.Result) && !ct.IsCancellationRequested)
                    {
                        fixTaskSpawned = true;
                        Activity("⚠ Tester found failures — Boss spawning targeted fix task", "boss");
                        _trace?.WriteEvent("fix_task_spawned", "Tester failure triggered fix pass");

                        var fixTask = BuildFixTask(testerTask.Result, Tasks, _targetLanguage);
                        Tasks.Add(fixTask);
                        OnTasksPlanned?.Invoke(Tasks);
                        await RunWorkerAsync(fixTask, findings, ct);

                        if (fixTask.Status == SwarmTaskStatus.Done)
                        {
                            fixTaskSucceeded = true;
                            Activity("✓ Fix task complete", "boss");
                            testerVerdict += " → fix applied";
                        }
                    }
                }
            }
            else if (workerFiles == 0 && plannedTesterVerdict is null)
            {
                Activity("⚠ Auto Tester skipped — no new files were written by implementation workers", "boss");
            }

            // Merge planned TESTER verdict with auto tester verdict (planned first, auto after)
            if (plannedTesterVerdict is not null)
                testerVerdict = testerVerdict is null
                    ? plannedTesterVerdict
                    : plannedTesterVerdict + "\n" + testerVerdict;

            // ── Phase 4: Boss merge → final_report.md ────────────────────────
            Activity("Boss merging results → writing final_report.md", "boss");
            _trace?.WriteEvent("phase_start", "Boss merge");
            var merged = await RunBossMergeAsync(userGoal, Tasks, ct, testerVerdict);

            // Extract final_report.md — only write to RunDir (not workspace root)
            ExtractAndWriteFiles(merged, RunDir);

            // Fallback: if boss didn't emit the FILE marker, write raw merge as final_report.md
            var finalReportPath = Path.Combine(RunDir, "final_report.md");
            if (!File.Exists(finalReportPath) && !string.IsNullOrWhiteSpace(merged))
                await File.WriteAllTextAsync(finalReportPath, merged, ct);

            await SaveSwarmRunJsonAsync(userGoal, Tasks);
            _trace?.WriteAssistantMessage(merged, agent: "boss_merge", model: _bossModel);
            _trace?.WriteEvent("swarm_complete", "All phases done");

            // Count only files actually written by workers (tool calls + ### FILE: markers)
            var projectFiles = Tasks.Sum(t => t.ToolFilesWritten + t.MarkerFilesWritten);

            // Collect staged files and notify the UI so the user can review before applying
            var stagedFiles = Directory.Exists(OutputProjectDir)
                ? Directory.GetFiles(OutputProjectDir, "*", SearchOption.AllDirectories)
                           .Select(f => f.Replace(OutputProjectDir, "").TrimStart(Path.DirectorySeparatorChar))
                           .ToArray()
                : [];
            if (stagedFiles.Length > 0)
                OnStagingReady?.Invoke(_runId, OutputProjectDir, stagedFiles);

            Activity($"Swarm complete — {stagedFiles.Length} file(s) staged for review in .orc/swarm/runs/{_runId}/staging", "boss");

            // ── Emit run metrics (non-blocking, non-fatal) ────────────────────
            var tvEnum = testerVerdict switch
            {
                null                                => TesterVerdict.Skipped,
                var s when s.Contains("PASS")       => TesterVerdict.Pass,
                var s when s.Contains("fix applied")=> TesterVerdict.Partial,
                _                                   => TesterVerdict.Fail
            };
            var bossProfile   = ModelProfiles.Get(_bossModel);
            var coderProfile  = ModelProfiles.Get(_coderModel);
            _ = SwarmMetricsStore.AppendAsync(new SwarmRunRecord(
                RunId:                _runId,
                StartedAt:            runStartedAt,
                DurationSeconds:      (int)(DateTime.UtcNow - runStartedAt).TotalSeconds,
                BossModel:            _bossModel,
                CoderModel:           _coderModel,
                ResearcherModel:      _researcherModel,
                TotalVramGb:          _hardwareVramGb,
                Goal:                 userGoal.Length > 120 ? userGoal[..120] : userGoal,
                SwarmSucceeded:       projectFiles > 0,
                FilesWritten:         projectFiles,
                GhostResearcherCount: ghostCount,
                CoderRetryCount:      coderRetryCount,
                Verdict:              tvEnum,
                FixTaskSpawned:       fixTaskSpawned,
                FixTaskSucceeded:     fixTaskSucceeded,
                BossScoreAtRunTime:   bossProfile.BossScore,
                CoderScoreAtRunTime:  coderProfile.CoderScore
            ));

            OnSwarmComplete?.Invoke(merged);
        }
        catch (OperationCanceledException)
        {
            _trace?.WriteEvent("swarm_cancelled", "Stopped by user");
            OnError?.Invoke("Swarm stopped.");
        }
        catch (Exception ex)
        {
            _trace?.WriteEvent("swarm_error", ex.Message);
            OnError?.Invoke($"Swarm error: {ex.Message}");
        }
        finally
        {
            _trace?.Dispose();
            _trace = null;
            OnStopped?.Invoke();
        }
    }

    private void Activity(string msg, string agentKey = "boss") => OnActivity?.Invoke(agentKey, msg);

    private static string AgentKey(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => "researcher",
        SwarmWorkerRole.Coder       => "coder",
        SwarmWorkerRole.UIDeveloper => "uidev",
        SwarmWorkerRole.Tester      => "tester",
        _                           => "boss"
    };

    private const int MaxRetries = 2;

    // ── GOBLIN MIND: Capability-aware routing ─────────────────────────────────

    /// <summary>
    /// Returns the best available model for the given role, validated against
    /// the model's CategoryBoundaryMap.  Falls back gracefully if no map exists
    /// (assumes the model is capable — avoids breaking setups that haven't been probed).
    ///
    /// For each role the required categories are:
    ///   Boss       → StructuredOutput + TaskPlanning
    ///   Researcher → Network + DataTransform
    ///   Coder      → FileOps + CodeExec
    ///   UIDev      → FileOps + CodeExec  (same as Coder)
    ///   Tester     → CodeExec + SystemInspect
    /// </summary>
    private string GetCapableModel(SwarmWorkerRole role)
    {
        var (primary, fallback) = role == SwarmWorkerRole.Researcher
            ? (_researcherModel, _coderModel)
            : (_coderModel,      _bossModel);

        // Decision logic lives in SwarmSteering (unit-tested, T11); this method
        // only wires the live profile store and surfaces the warnings.
        var d = SwarmSteering.SelectModel(role, primary, fallback,
                                          ToolCallProfileStore.GetCategoryMap);

        if (d.UsedFallback)
        {
            Activity(SwarmSteering.PrimaryFallbackWarning(
                ShortModelName(primary), ShortModelName(fallback), role, d.PrimaryMissing), "boss");

            if (d.FallbackMissing.Length > 0)
                Activity(SwarmSteering.FallbackDeficientWarning(
                    ShortModelName(fallback), d.FallbackMissing), "boss");
        }

        return d.Model;
    }

    /// <summary>
    /// Builds a compact capability summary string for all swarm models.
    /// Injected into the boss decompose prompt so TheOrc knows which goblins
    /// can handle which task categories without knowing model internals.
    ///
    /// Example output:
    ///   Boss (qwen2.5:32b): StructuredOutput ✅ TaskPlanning ✅
    ///   Coder (qwen2.5-coder:14b): FileOps ✅ CodeExec ✅ Network ⚠
    ///   Researcher (mistral:7b): Network ✅ DataTransform ✅ FileOps ✗
    /// </summary>
    private string BuildCapabilitySummary()
        => SwarmSteering.BuildCapabilitySummary(
               _bossModel, _coderModel, _researcherModel,
               ToolCallProfileStore.GetCategoryMap, ShortModelName);

    private static string ShortModelName(string modelId)
    {
        // "qwen2.5-coder:14b-instruct-q4_K_M" → "qwen2.5-coder:14b"
        var parts = modelId.Split(':');
        if (parts.Length < 2) return modelId;
        var tag = parts[1].Split('-')[0];
        return $"{parts[0]}:{tag}";
    }

    // ── File extraction ───────────────────────────────────────────────────────

    private static readonly Regex FileMarker = new(
        @"###\s+FILE:\s*(.+?)\r?\n```[^\n]*\r?\n(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses worker output for ### FILE: path markers and writes each to outputDir.
    /// Returns the number of files written.
    /// </summary>
    private static int ExtractAndWriteFiles(string content, string outputDir)
    {
        int count = 0;
        foreach (Match m in FileMarker.Matches(content))
        {
            var relPath = m.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar)
                                                   .TrimStart(Path.DirectorySeparatorChar, '.');
            var fileContent = m.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(relPath)) continue;

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(outputDir, relPath));
                // Guard against path traversal
                if (!fullPath.StartsWith(Path.GetFullPath(outputDir), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, fileContent);
                count++;
            }
            catch { /* non-fatal */ }
        }
        return count;
    }

    /// <summary>Returns just the file names (leaf name, no path) extracted by the FILE marker regex.</summary>
    private static IReadOnlyList<string> ExtractFileNames(string content)
    {
        var names = new List<string>();
        foreach (Match m in FileMarker.Matches(content))
        {
            var relPath = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(relPath))
                names.Add(Path.GetFileName(relPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        return names;
    }

    /// <summary>
    /// Builds a user message for a retry attempt, prepending a strong reminder about the FILE format.
    /// </summary>
    private static string BuildRetryUserMessage(
        SwarmTask task,
        List<(string title, string result)> researchContext,
        string targetLanguage,
        int attempt,
        IReadOnlyList<SwarmTask>? allTasks = null)
    {
        var baseMsg = BuildWorkerUserMessage(task, researchContext, targetLanguage, allTasks);
        var header = attempt == 1
            ? "⚠ RETRY — YOUR PREVIOUS RESPONSE HAD NO ### FILE: BLOCKS.\n" +
              "You MUST output every file using this exact format:\n\n" +
              "### FILE: filename.py\n```python\n[COMPLETE file contents here]\n```\n\n" +
              "Do NOT describe the code. Do NOT use prose. Output ONLY ### FILE: blocks.\n\n"
            : "⚠ FINAL RETRY — YOU MUST OUTPUT ### FILE: BLOCKS.\n" +
              "Stop all prose. Your ENTIRE response must be ### FILE: blocks only.\n" +
              "Format:\n\n" +
              "### FILE: [filename]\n```[language]\n[complete code, no truncation]\n```\n\n";
        return header + baseMsg;
    }

    // ── Text-format tool call parser ─────────────────────────────────────────
    // Mirrors AgentLoop.TryParseTextToolCalls — models that don't use structured
    // tool_calls sometimes emit raw JSON like {"name":"write_file","arguments":{…}}.

    private static List<ToolCall> TryParseTextToolCalls(string content)
    {
        var result   = new List<ToolCall>();
        var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();

        int i = 0;
        while (i < stripped.Length)
        {
            var start = stripped.IndexOf('{', i);
            if (start < 0) break;

            int depth = 0, end = -1;
            bool inStr = false;
            for (int j = start; j < stripped.Length; j++)
            {
                var ch = stripped[j];
                if (ch == '"' && (j == 0 || stripped[j - 1] != '\\')) inStr = !inStr;
                if (inStr) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }
            if (end < 0) break;

            var json = stripped[start..(end + 1)];
            try
            {
                var node = JsonNode.Parse(json);
                var name = node?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    var args = new Dictionary<string, object?>();
                    if (node?["arguments"] is JsonObject argsObj)
                        foreach (var kvp in argsObj)
                            args[kvp.Key] = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                                ? s : kvp.Value?.ToString();

                    result.Add(new ToolCall
                    {
                        Id           = Guid.NewGuid().ToString("N")[..8],
                        Name         = name,
                        Arguments    = args,
                        IsTextFormat = true,
                    });
                }
            }
            catch { /* malformed JSON — skip */ }

            i = end + 1;
        }
        return result;
    }

    // ── Agent files (.orc/agents/{role}.md) ──────────────────────────────────

    private string AgentFilesDir =>
        Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "agents");

    private string AgentFilePath(string role) =>
        Path.Combine(AgentFilesDir, $"{role.ToLower()}.md");

    private async Task<string> LoadAgentFileAsync(string role)
    {
        var path = AgentFilePath(role);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(AgentFilesDir);
                await File.WriteAllTextAsync(path, DefaultAgentContent(role));
            }
            return await File.ReadAllTextAsync(path);
        }
        catch { return ""; }
    }

    private static string DefaultAgentContent(string role) => role.ToLower() switch
    {
        "boss" => """
            # TheOrc — Custom Instructions
            You are TheOrc, the Orchestrator. You direct the swarm.
            Be decisive — keep plans tight, minions focused.
            Prefer 2–3 well-scoped tasks over many small ones.
            """,

        "researcher" => """
            # Researcher Agent — Custom Instructions
            Focus on practical, actionable findings. Always include:
            - Specific library names and versions to use
            - Key API methods with example usage
            - Known gotchas, constraints, or requirements
            - A concrete recommendation for the coder
            Do NOT write final production code — document and recommend only.
            """,

        "coder" => """
            # Coder Agent — Custom Instructions
            Write production-ready code. Always:
            - Include proper error handling
            - Use the exact libraries the researcher recommended
            - Output complete, runnable files using the FILE format
            - Write in the language specified by the task — do NOT default to C# or any other language
            """,

        "uideveloper" => """
            # UI Developer Agent — Custom Instructions
            Write complete UI code using whatever framework the task specifies:
            - Python: tkinter, PyQt, or wxPython
            - Web: HTML + CSS + JavaScript
            - C#/.NET: WPF XAML + code-behind (only if task explicitly requires it)
            Output complete files using the FILE format. Never assume a framework.
            """,

        _ => $"# {role} Agent — Custom Instructions\nComplete the assigned task thoroughly."
    };

    // ── Language detection ────────────────────────────────────────────────────

    /// <summary>
    /// Inspects the user's goal for explicit language/stack keywords.
    /// Returns the canonical language name, or empty string if undetected.
    /// Workers will be locked to this language if non-empty.
    ///
    /// Detection runs in two passes:
    ///   1. Output-artifact pass — filenames the goal asks to create/write/add.
    ///      "Write T09_Tests.cs that invokes review_captures.py" locks to C#,
    ///      because .cs is the requested artifact and .py is just tooling.
    ///   2. Keyword pass — the original keyword scan, unchanged, as fallback.
    /// </summary>
    internal static string DetectTargetLanguage(string goal)
    {
        var lower = goal.ToLowerInvariant();

        var artifactLang = DetectArtifactLanguage(lower);
        if (artifactLang.Length > 0) return artifactLang;

        // Python — most common swarm target; check early
        // (filename mentions like ".py" are handled by the artifact pass above)
        if (lower.Contains("python") || lower.Contains("tkinter") || lower.Contains("pyqt") ||
            lower.Contains("flask") || lower.Contains("django") || lower.Contains("fastapi"))
            return "Python";

        // Web
        if (lower.Contains("typescript") || lower.Contains(" ts ")) return "TypeScript";
        if (lower.Contains("javascript") || lower.Contains("node.js") || lower.Contains("nodejs") ||
            lower.Contains("react") || lower.Contains("vue") || lower.Contains("angular"))
            return "JavaScript";
        if (lower.Contains("html") && lower.Contains("css")) return "HTML/CSS/JavaScript";

        // Systems
        if (lower.Contains(" rust ") || lower.Contains("cargo.toml")) return "Rust";
        if (lower.Contains("golang") || lower.Contains("go module") ||
            Regex.IsMatch(lower, @"\bgo\s+(program|app|tool|server|client)\b"))
            return "Go";
        if (lower.Contains("c++") || lower.Contains("cpp") || lower.Contains("cmake")) return "C++";

        // JVM
        if (lower.Contains("kotlin")) return "Kotlin";
        if (lower.Contains("java") && !lower.Contains("javascript")) return "Java";

        // .NET  — only if explicitly named; do NOT default here
        if (lower.Contains(" c# ") || lower.Contains("c#\n") || lower.Contains("csharp") ||
            lower.Contains("wpf") || lower.Contains("winforms") || lower.Contains(".net") ||
            lower.Contains("dotnet") || lower.Contains("asp.net"))
            return "C#";

        // Mobile
        if (lower.Contains("swift") || lower.Contains("swiftui") || lower.Contains("ios")) return "Swift";

        // Scripting
        if (lower.Contains("ruby") || lower.Contains("rails")) return "Ruby";
        if (lower.Contains("php") || lower.Contains("laravel")) return "PHP";
        if (lower.Contains("powershell")) return "PowerShell";
        if (lower.Contains("bash") || lower.Contains("shell script")) return "Bash";

        return "";  // unknown — boss/task description will guide workers
    }

    // Filenames with a recognized code extension, e.g. health_check.ps1,
    // ModelWikiWindow.xaml.cs, review_captures.py
    private static readonly Regex _artifactFileRe =
        new(@"[\w\-]+(?:\.[\w\-]+)*\.(?<ext>[a-z0-9]{1,6})\b", RegexOptions.Compiled);

    // A creation verb followed by the filename within the same sentence fragment
    // (no sentence terminator between verb and filename) marks the file as the
    // goal's requested OUTPUT artifact rather than referenced tooling.
    private static readonly Regex _creationVerbRe =
        new(@"\b(write|create|add|implement|build|generate|make|update|modify|edit|extend|refactor)\b[^.!?\n]{0,80}$",
            RegexOptions.Compiled);

    /// <summary>
    /// Detects the lock language from filenames mentioned in the goal.
    /// Requested artifacts (preceded by a creation verb) outrank incidental
    /// mentions; mixed-language mentions without a clear requested artifact
    /// yield no lock so the keyword pass can decide.
    /// </summary>
    internal static string DetectArtifactLanguage(string lowerGoal)
    {
        var requested = new HashSet<string>();
        var mentioned = new HashSet<string>();

        foreach (Match m in _artifactFileRe.Matches(lowerGoal))
        {
            var lang = ExtensionLanguage(m.Groups["ext"].Value);
            if (lang is null) continue;

            mentioned.Add(lang);
            if (_creationVerbRe.IsMatch(lowerGoal[..m.Index]))
                requested.Add(lang);
        }

        if (requested.Count == 1) return requested.First();
        if (requested.Count == 0 && mentioned.Count == 1) return mentioned.First();
        return "";  // none, or ambiguous mix — defer to keyword pass
    }

    /// <summary>Maps a filename extension to its canonical lock language.</summary>
    private static string? ExtensionLanguage(string ext) => ext switch
    {
        "py"                                    => "Python",
        "cs" or "xaml" or "csproj" or "sln"
             or "slnx"                          => "C#",
        "ts" or "tsx"                           => "TypeScript",
        "js" or "jsx" or "mjs"                  => "JavaScript",
        "rs"                                    => "Rust",
        "go"                                    => "Go",
        "cpp" or "cc" or "cxx" or "hpp"         => "C++",
        "kt" or "kts"                           => "Kotlin",
        "java"                                  => "Java",
        "swift"                                 => "Swift",
        "rb"                                    => "Ruby",
        "php"                                   => "PHP",
        "ps1" or "psm1" or "psd1"               => "PowerShell",
        "sh" or "bash"                          => "Bash",
        _                                       => null   // docs/data/unknown — no lock signal
    };

    // ── Boss: Decompose ───────────────────────────────────────────────────────

    private const string BossDecomposeSystemPrompt = """
        You are TheOrc — the Orchestrator of a multi-agent AI coding swarm.
        You direct four specialist minions:
          • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code
          • CODER       — writes full implementation code using the researcher's findings
          • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling
          • TESTER      — runs existing code, executes tests, checks syntax, reports results; does NOT write or modify files

        Given a user's coding goal, break it into 2–4 concurrent subtasks.
        Assign each subtask to the best-fit minion role.

        Rules:
        - RESEARCHER tasks always get priority 1 (they run first, alone)
        - CODER, UIDEVELOPER, and TESTER tasks get priority 2 (run concurrently after research)
        - If no research is needed, skip RESEARCHER and assign CODER/UIDEVELOPER/TESTER tasks directly
        - TESTER tasks verify code that already exists in the workspace — they do NOT receive output from CODER tasks in the same run
        - Descriptions must be self-contained — minions cannot ask follow-up questions
        - Maximum 4 tasks total: up to 1 RESEARCHER + up to 3 CODER/UIDEVELOPER/TESTER
        - Prefer 3 priority-2 tasks when the goal has distinct implementation concerns

        FILENAME RULE — task titles MUST name the output file(s):
        - Good title: "Write scraper.py and ollama_client.py"
        - Good title: "Build main.py Tkinter UI"
        - Bad title:  "Implement article fetcher" (no filename — workers won't know what to name the file)

        API CONTRACT RULE — when worker A produces a module that worker B imports:
        - Decide the EXACT function/class names ONCE and use the same names in BOTH task descriptions.
        - Example: if CODER writes scraper.py with function fetch_article_text(url), then the UIDEVELOPER task MUST say "from scraper import fetch_article_text" — not a different name.
        - This is non-negotiable: mismatched names cause import errors at runtime.

        Respond with ONLY valid JSON — no markdown fences, no preamble, no trailing text.
        String values MUST NOT contain literal newlines — use \\n inside strings if needed.
        {
          "plan": "one-sentence overall approach",
          "tasks": [
            {
              "role": "RESEARCHER",
              "priority": 1,
              "title": "Short descriptive title",
              "description": "Detailed, self-contained instructions for this minion. Use \\n for line breaks inside this string."
            }
          ]
        }
        """;

    private async Task<string> RunBossDecomposeAsync(string userGoal, CancellationToken ct)
    {
        var bossProfile = ModelProfiles.Get(_bossModel);
        var bossFile    = await LoadAgentFileAsync("boss");

        // Build system prompt: base + optional custom boss file + capability map
        var sysPrompt = string.IsNullOrWhiteSpace(bossFile)
            ? BossDecomposeSystemPrompt
            : BossDecomposeSystemPrompt + "\n\n## Custom boss instructions:\n" + bossFile;

        // Inject model-specific planning supplement (e.g. few-shot examples for weak planners)
        if (!string.IsNullOrWhiteSpace(bossProfile.BossPromptSupplement))
            sysPrompt += "\n\n" + bossProfile.BossPromptSupplement.Trim();

        // GOBLIN MIND: inject capability map so TheOrc routes tasks correctly
        sysPrompt += "\n\n" + BuildCapabilitySummary();

        // Append language lock to the goal if we detected one
        var goalWithLang = string.IsNullOrWhiteSpace(_targetLanguage)
            ? userGoal
            : $"{userGoal}\n\n⚠ LANGUAGE LOCK: This project MUST be implemented in {_targetLanguage}. " +
              $"All task descriptions MUST specify {_targetLanguage} explicitly. " +
              $"Minions must NOT use C#, XAML, WPF, or any other language.";

        // Use the model's configured temperature for boss calls; fall back to 0.15
        var bossTemp = bossProfile.Temperature > 0 ? bossProfile.Temperature : 0.15;

        OnBossToken?.Invoke("⬡ TheOrc is planning the swarm…\n\n");

        // ── Attempt 1 ────────────────────────────────────────────────────────
        var raw = await StreamBossAsync(sysPrompt, goalWithLang, bossTemp, ct);
        var plan = ParseBossPlan(raw);

        // ── Boss plan quality gate ────────────────────────────────────────────
        // Detect models that collapse planning to a single empty task.
        // If underplanned, retry ONCE with an escalated prompt that:
        //   (a) shows the bad output the model just produced, and
        //   (b) explicitly demands a complete multi-task plan.
        if (IsBossUnderPlanned(plan))
        {
            _trace?.WriteEvent("boss_underplanned",
                $"Boss produced {plan.Count} task(s) with empty/trivial descriptions — retrying with escalated prompt");
            Activity("⚠ Boss plan is underspecified — retrying with escalated decomposition prompt…", "boss");
            OnBossToken?.Invoke("\n\n⚠ Plan was underspecified — retrying…\n\n");

            var escalationSuffix = $"""

## ⚠ YOUR PREVIOUS OUTPUT WAS REJECTED

You returned this plan, which is INVALID because the description is empty:
{raw.Trim()[..Math.Min(400, raw.Trim().Length)]}

REQUIREMENTS YOU MUST FOLLOW NOW:
1. You MUST output between 3 and 4 tasks. Never 1.
2. Every "description" field MUST be at least 3 sentences describing exactly what to build.
3. Task titles MUST name the output file (e.g. "Write csv_cleaner.py").
4. Do NOT output title:"Execute goal" or description:"" — these are rejected.

Output ONLY the JSON object. No explanation, no apology, no markdown fences.
""";
            var escalatedPrompt = sysPrompt + escalationSuffix;
            raw = await StreamBossAsync(escalatedPrompt, goalWithLang, Math.Min(bossTemp + 0.2, 0.9), ct);
            _trace?.WriteEvent("boss_retry_complete", $"Escalated plan raw length: {raw.Length}");
        }

        return raw;
    }

    /// <summary>
    /// Returns true when the boss plan is underspecified:
    /// a single task, or all tasks have empty/trivial descriptions.
    /// </summary>
    private static bool IsBossUnderPlanned(List<SwarmTask> tasks)
    {
        if (tasks.Count == 0) return true;
        // 1 task with empty or very short description = classic gemma4:12b collapse pattern
        if (tasks.Count == 1)
        {
            var desc = tasks[0].Description?.Trim() ?? "";
            return desc.Length < 30;
        }
        // Multiple tasks but all descriptions are empty
        var nonEmpty = tasks.Count(t => (t.Description?.Trim().Length ?? 0) > 20);
        return nonEmpty == 0;
    }

    private async Task<string> StreamBossAsync(
        string sysPrompt, string goalWithLang, double temperature, CancellationToken ct)
    {
        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = sysPrompt },
            new() { Role = MessageRole.User,   Content = $"Goal: {goalWithLang}" }
        };
        var sb = new StringBuilder();
        // 8192: Gemma4 spends a variable (often large) share of the budget on
        // reasoning tokens before the JSON plan. At 2048 the plan was routinely
        // truncated mid-string (or never started), presenting as json_invalid
        // collapse. max_tokens overrides the Modelfile's num_predict 2048.
        await foreach (var token in _ollama.StreamCompletionAsync(
            _bossModel, history, temperature: temperature, maxTokens: 8192, ct: ct))
        {
            sb.Append(token);
            OnBossToken?.Invoke(token);
        }
        // Strip Gemma4 special tokens that leak through Ollama's OpenAI-compat
        // /v1/chat/completions endpoint (Ollama issue #15798). Safe on all models —
        // short-circuits immediately if no Gemma4 artifact markers are present.
        return StripGemma4Artifacts(sb.ToString());
    }

    /// <summary>
    /// Removes Gemma4 special tokens that Ollama leaks through the OpenAI-compat
    /// /v1/chat/completions endpoint (issue #15798). Artifacts include:
    ///   <|token|>  — Gemma4 control tokens (e.g. <|tool_call|>, <|turn|>, <|think|>)
    ///   <token|>   — Gemma4 closing delimiters (e.g. <turn|>, <channel|>)
    ///   <|channel|>thought\n...<channel|>  — embedded thinking blocks
    /// This is a no-op when none of these artifacts are present.
    /// </summary>
    private static string StripGemma4Artifacts(string raw)
    {
        // Fast path — avoid regex overhead on models that don't emit these tokens
        if (!raw.Contains("<|") && !raw.Contains("<channel|>")) return raw;

        // Remove <|token|> control tokens (Gemma4 format, e.g. <|tool_call|>, <|turn|>)
        var result = Regex.Replace(raw, @"<\|[^|>]{0,40}\|>", string.Empty);
        // Remove <token|> closing delimiters (e.g. <turn|>, <channel|>)
        result     = Regex.Replace(result, @"<[a-z_]{1,20}\|>", string.Empty);
        // Remove full thinking blocks: <|channel|>thought\n...<channel|>
        result     = Regex.Replace(result, @"<\|channel\|>.*?<channel\|>",
                         string.Empty, RegexOptions.Singleline);

        return result.Trim();
    }

    // ── Boss: Merge ───────────────────────────────────────────────────────────

    private async Task<string> RunBossMergeAsync(
        string userGoal, List<SwarmTask> tasks, CancellationToken ct,
        string? testerVerdict = null)
    {
        // Build a compact summary of worker results (not full content — avoid context overflow)
        var ctx = new StringBuilder();
        ctx.AppendLine($"Original goal: {userGoal}");
        ctx.AppendLine();

        foreach (var t in tasks.Where(t => t.Status == SwarmTaskStatus.Done
                                          && t.Role != SwarmWorkerRole.Tester))
        {
            ctx.AppendLine($"## {t.RoleIcon} {t.Title} [{t.Role}]");
            // Include up to 1500 chars of each worker's output to avoid context overflow
            var workerOutput = t.Result ?? "(empty)";
            ctx.AppendLine(workerOutput.Length > 1500 ? workerOutput[..1500] + "\n…(truncated)" : workerOutput);
            ctx.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(testerVerdict))
        {
            ctx.AppendLine("## 🧪 Verification Result");
            ctx.AppendLine(testerVerdict);
            ctx.AppendLine();
        }

        if (_directives.Count > 0)
        {
            ctx.AppendLine("## User directives:");
            foreach (var d in _directives) ctx.AppendLine($"- {d}");
            ctx.AppendLine();
        }

        var mergePrompt = $"""
            {ctx}

            Write a final_report.md that covers exactly:
            1. How many agents were used and why those roles were chosen
            2. What each agent produced (list the files)
            3. How to run the project
            4. How to test the project
            5. Verification result (from the Tester goblin, if present)
            6. Known risks or limitations

            Use this exact output format:

            ### FILE: final_report.md
            ```markdown
            # Final Report — [project name]

            ## Agents Used
            [N agents: describe each role and what it did]

            ## Files Created
            [bullet list of files and what each does]

            ## How to Run
            [numbered steps]

            ## How to Test
            [test checklist]

            ## Verification
            [Tester goblin result — PASS/FAIL/PARTIAL and any errors found.
             If no tester was run, write "Not verified — no runnable files produced."]

            ## Known Risks / Limitations
            [any important caveats]
            ```
            """;

        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = "You are TheOrc, the Orchestrator. Produce a concise final_report.md using the FILE format shown." },
            new() { Role = MessageRole.User,   Content = mergePrompt }
        };

        OnBossToken?.Invoke("\n\n──── ⬡ TheOrc is merging results ────\n\n");

        var result = new StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            _bossModel, history, temperature: 0.2, maxTokens: 3072, ct: ct))
        {
            result.Append(token);
            OnBossToken?.Invoke(token);
        }
        return result.ToString();
    }

    // ── Worker system prompts ─────────────────────────────────────────────────

    private const string FileOutputInstructions = """

        CRITICAL — FILE OUTPUT:
        You have access to tools. ALWAYS use write_file to write each file directly to disk.
        Do NOT describe the code in prose — call write_file with the full file content.

        If write_file is not available, fall back to this text format:
        ### FILE: relative/path/to/filename.ext
        ```language
        complete file content here — never truncated, never a snippet
        ```

        Rules:
        - Prefer write_file tool over text markers when available
        - Every required file must actually be written — not described
        - Each file must be complete and production-ready — no placeholders, no TODOs
        - Multiple files: one write_file call per file
        - Use fetch_url to look up documentation or source material when needed
        - Use run_shell to install dependencies or verify the project builds

        ⚠ FILENAME DISCIPLINE: Use the EXACT filename(s) specified in your task title or description.
        NEVER substitute generic names like README.md, output.py, FileMap.txt, or main.txt.
        If the task says write "ARCHITECTURE.md" → the file MUST be named "ARCHITECTURE.md".
        If the task says write "scraper.py" → the file MUST be named "scraper.py".
        The filename in the task description IS the filename. Do not invent alternatives.
        Do NOT write files named ".agent.md", "_agentlog.txt", "output.txt", or "debug.txt".
        """;

    private static string WorkerSystemPrompt(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher => """
            You are a RESEARCHER in a multi-agent AI coding system.
            Your job: investigate, discover, and document. Do NOT write final production code.
            Return: relevant library names and versions, key API methods with example usage,
            specific recommendations for the coder, known gotchas and constraints.
            Be thorough — the coder depends entirely on your findings.
            Format your output clearly with headers. You may include short example snippets.

            You have access to `ask_user` — use it if you need a critical piece of information
            you cannot find yourself (e.g., a private API key, a specific internal URL).
            Use sparingly: ask at most once per task, only for genuinely blocking information.
            """,

        SwarmWorkerRole.Coder => $"""
            You are a CODER in a multi-agent AI coding system.
            Your job: write clean, complete, production-ready implementation code.
            Use the research findings provided to make informed technology choices.
            Output complete files — not snippets. Include error handling and comments.

            ⚠ CRITICAL LANGUAGE RULE: Write ONLY in the language explicitly stated in your task.
            If the task says Python → write Python. If it says JavaScript → write JavaScript.
            Do NOT default to C#, XAML, or WPF unless the task explicitly requires it.

            ⚠ CRITICAL IMPORT RULE — THIS EXACT PATTERN IS FORBIDDEN:
            ```python
            # ❌ WRONG — NEVER DO THIS:
            try:
                scraper = None  # placeholder
            except Exception as e:
                raise RuntimeError(...) from e
            ```
            ALWAYS use real import statements:
            ```python
            # ✅ CORRECT:
            import scraper
            import ollama_client
            import file_manager
            ```
            If a sibling module is needed, import it by name directly — never assign None.

            ⚠ NO PLACEHOLDERS: Every file must be complete and self-contained.
            Never write `# TODO`, `pass`, `raise NotImplementedError`, or stub bodies.
            Every function must have a real, working implementation.

            You have `ask_user` available. Use it only for genuinely ambiguous requirements
            where the wrong choice would break the implementation (e.g., "should authentication
            use JWT or sessions?"). Keep it rare — ask at most once per task.
            {FileOutputInstructions}
            """,

        SwarmWorkerRole.UIDeveloper => $"""
            You are a UIDEVELOPER in a multi-agent AI coding system.
            Your job: write complete UI code using the framework specified in your task.

            Framework rules:
            - If the task says Python/tkinter → write Python with tkinter
            - If the task says HTML/web → write HTML + CSS + JavaScript
            - If the task says WPF/C# → write XAML + C# code-behind
            - If unspecified, match whatever language the Coder is using

            ⚠ Do NOT default to WPF or XAML unless explicitly required by the task.

            ⚠ CRITICAL IMPORT RULE — THIS EXACT PATTERN IS FORBIDDEN:
            ```python
            # ❌ WRONG — NEVER DO THIS:
            try:
                scraper = None  # placeholder
            except Exception as e:
                raise RuntimeError(...) from e
            ```
            ALWAYS use real import statements instead:
            ```python
            # ✅ CORRECT:
            import scraper
            import ollama_client
            import file_manager
            ```
            If a sibling module may not exist yet, use a conditional import — never assign None.

            ⚠ NO PLACEHOLDERS: Every file must be complete and runnable.
            Every widget must be properly placed/packed/gridded. Every button must have
            a working command. Every import must be real.
            Output complete, self-contained files.

            You have `ask_user` available — use it if the layout or UX has a critical
            ambiguity you cannot resolve from the task description. Ask at most once.
            {FileOutputInstructions}
            """,

        SwarmWorkerRole.Tester => """
            You are a TESTER in a multi-agent AI coding swarm.
            Your ONLY job: run the code that already exists in the workspace and report what happened.
            The files you are testing already exist — you are not waiting for any other worker in this run.

            RULES — follow every rule exactly:
            - You have run_shell, read_file, and list_files. Use them. Do NOT use write_file.
            - Do NOT rewrite code. Do NOT give suggestions. Only run and report.
            - Report the EXACT error text if something fails. Do not paraphrase.
            - If a file does not exist, say so exactly.

            STEPS (execute in this order):
            1. list_files — confirm which files exist in the workspace.
            2. Syntax check — run_shell with the language-appropriate command:
               Python:     python -m py_compile main.py  (or the entry file from your task)
               JavaScript: node --check index.js
            3. Smoke run — run_shell to attempt a brief import/load with a short timeout.
               Python:     python -c "import ast; [compile statements check]"
               or simply:  python main.py  with a 5-second timeout if safe.
            4. Report results using EXACTLY this format — no other text:

            STATUS: PASS
            FILES_FOUND: file1.py, file2.py
            ERRORS: none
            NOTES: All files present and syntax checks pass.

            --- OR ---

            STATUS: FAIL
            FILES_FOUND: file1.py, file2.py
            ERRORS: [EXACT error text including line numbers]
            NOTES: [one sentence on root cause]

            Only STATUS: PASS, STATUS: FAIL, or STATUS: PARTIAL are valid verdicts.
            """,

        _ => $"You are a worker agent. Complete the assigned task thoroughly.{FileOutputInstructions}"
    };

    // ── Worker tool suite ─────────────────────────────────────────────────────

    /// <summary>
    /// Build the auto-approving tool registry for swarm workers, rooted at the
    /// workspace root (OutputProjectDir). Call once output dir is known.
    /// </summary>
    private void InitWorkerTools()
    {
        var approvals = new ApprovalQueue { AutoApprove = true };
        _toolRegistry = new ToolRegistry(approvals);

        // ── Sandbox bypass delegate ───────────────────────────────────────
        // Shows the SandboxBypassDialog on the UI thread and awaits the user's choice.
        Func<string, string, string, CancellationToken, Task<bool>> sandboxBypass =
            async (toolName, escapedPath, sandboxRoot, ct) =>
            {
                // Headless host (no WPF Application) — deny sandbox escapes outright
                if (System.Windows.Application.Current is null) return false;

                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                              System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetResult(false));

                // Fire on the UI thread; TCS carries the result back to this async context
                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new OrchestratorIDE.UI.Dialogs.SandboxBypassDialog(
                                  toolName, escapedPath, sandboxRoot,
                                  "TheOrc Swarm Worker")
                    {
                        Owner = System.Windows.Application.Current.MainWindow
                    };
                    tcs.TrySetResult(dlg.ShowDialog() == true);
                });

                return await tcs.Task;
            };

        // File tools — workers write directly into the swarm output project dir
        FileTools.Register(_toolRegistry, OutputProjectDir,
            onDiffPreview: null, onSandboxBypass: sandboxBypass);
        // Code search tools — search within the output dir or workspace
        SearchTools.Register(_toolRegistry, OutputProjectDir);
        // Web tools — fetch documentation, APIs, source URLs
        WebTools.Register(_toolRegistry);
        // Shell tools — run python, pip, node within the output dir
        ShellTools.Register(_toolRegistry, OutputProjectDir, onSandboxBypass: sandboxBypass);

        Activity("🛠 Worker tools initialised: write_file, read_file, list_files, run_shell, grep_code, fetch_url", "boss");
    }

    /// <summary>
    /// Virtual tool that pauses the worker and waits for the user to reply.
    /// Handled directly in RunWorkerAsync — never dispatched to _toolRegistry.
    /// </summary>
    private static readonly ToolDefinition AskUserTool = new()
    {
        Name        = "ask_user",
        Description = "Pause your task and ask the user a question. Use when you genuinely need user input to proceed — e.g. ambiguous requirements, a critical design choice, or needing credentials/paths you can't infer. Keep it rare: ask at most once per task.",
        Parameters  = new Dictionary<string, ToolParameter>
        {
            ["question"] = new("string",  "Clear, specific question to ask the user."),
            ["options"]  = new("string",  "Optional JSON array of suggested answer strings, e.g. [\"Option A\",\"Option B\"]. Omit if open-ended."),
        },
        Required = ["question"],
    };

    /// <summary>
    /// Returns the allowed tool set for a given worker role.
    /// Researcher gets read + fetch; Coder/UIDev get the full write + run suite.
    /// ask_user is available to all roles (handled in-process, not via registry).
    /// </summary>
    private IReadOnlyList<ToolDefinition> GetWorkerTools(SwarmWorkerRole role)
    {
        if (_toolRegistry == null) return [AskUserTool];

        var names = role switch
        {
            SwarmWorkerRole.Researcher  => new[] { "fetch_url", "grep_code", "get_outline", "read_file", "list_files" },
            SwarmWorkerRole.Coder       => new[] { "write_file", "read_file", "run_shell",
                                                    "list_files", "grep_code", "fetch_url" },
            SwarmWorkerRole.UIDeveloper => new[] { "write_file", "read_file", "run_shell",
                                                    "list_files", "fetch_url" },
            // Tester: read + execute only — NO write_file by design (prevents self-patching)
            SwarmWorkerRole.Tester      => new[] { "run_shell", "read_file", "list_files" },
            _                           => new[] { "read_file", "list_files" },
        };

        return names
            .Select(n => { _toolRegistry.TryGet(n, out var t); return t; })
            .Where(t => t is not null)
            .Cast<ToolDefinition>()
            .Append(AskUserTool)        // ask_user available to every role
            .ToList();
    }

    /// <summary>
    /// Returns true if the given model name benefits from disabling thinking tokens
    /// (Nemotron models must have thinking disabled for reliable tool calling).
    /// </summary>
    private static bool ShouldDisableThinking(string model)
    {
        var m = model.ToLowerInvariant();
        return m.Contains("nemotron") || m.Contains("qwen3") || m.Contains("deepseek-r");
    }

    // ── HIVE MIND Phase 3: distributed dispatch ───────────────────────────────

    /// <summary>
    /// Dispatches a SwarmTask to the distributed HiveTaskQueue.
    /// Builds a self-contained HiveTaskBundle (system prompt + user message +
    /// upstream artifacts), enqueues it, and awaits a remote worker's result.
    /// Integrates the result back into session state exactly as local execution does.
    /// Falls back to local execution if dispatch fails.
    /// </summary>
    private async Task DispatchToQueueAsync(
        SwarmTask task,
        List<(string title, string result)> findings,
        CancellationToken ct)
    {
        task.Status    = SwarmTaskStatus.InProgress;
        task.StartedAt = DateTime.UtcNow;
        OnTaskChanged?.Invoke(task);
        Activity($"{task.RoleIcon} {task.Title} — dispatching to HIVE queue", AgentKey(task.Role));

        // Emit a waiting indicator to the stream tab so the UI isn't blank
        OnWorkerToken?.Invoke(task.Id,
            $"⏳ Task dispatched to HIVE MIND distributed queue.\n" +
            $"Waiting for a worker node to claim [{task.Role}] '{task.Title}'…\n");

        try
        {
            var bundle = BuildTaskBundle(task, findings);
            var result = await _distributedQueue!.EnqueueAndWaitAsync(task.Id, bundle, ct);

            if (result is null)
            {
                // Session cancelled — propagate as OperationCanceledException
                ct.ThrowIfCancellationRequested();
                // If not cancelled, no worker claimed in time — fall back to local
                Activity($"⚠ No worker claimed '{task.Title}' — falling back to local execution", "boss");
                await RunWorkerAsync(task, findings, ct);
                return;
            }

            if (result.Status == "failed")
            {
                task.Status       = SwarmTaskStatus.Error;
                task.ErrorMessage = result.ErrorMsg ?? "Worker reported failure";
                task.ExecutedByNodeId = result.WorkerId;
                Activity($"⚠ {task.RoleIcon} {task.Title} — worker {result.WorkerId} reported failure", AgentKey(task.Role));
                OnTaskChanged?.Invoke(task);
                return;
            }

            task.Result           = result.Result;
            task.ExecutedByNodeId = result.WorkerId;
            task.Status           = SwarmTaskStatus.Done;
            task.CompletedAt      = DateTime.UtcNow;

            // Show full result in the stream tab + completion banner
            OnWorkerToken?.Invoke(task.Id,
                $"\n✅ Completed by {result.WorkerId} in {result.DurationMs / 1000.0:F1}s\n\n" +
                result.Result);

            _trace?.WriteAssistantMessage(result.Result, agent: task.Role.ToString().ToLower(), model: "hive-worker");
            await SaveTaskResultAsync(task);
            Activity($"{task.RoleIcon} {task.Title} — ✅ completed by {result.WorkerId}", AgentKey(task.Role));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            task.Status       = SwarmTaskStatus.Error;
            task.ErrorMessage = ex.Message;
            Activity($"⚠ HIVE dispatch error for '{task.Title}': {ex.Message} — falling back to local", "boss");
            // Non-fatal — fall back to local execution
            await RunWorkerAsync(task, findings, ct);
            return;
        }

        OnTaskChanged?.Invoke(task);
    }

    private Services.Hive.HiveTaskBundle BuildTaskBundle(
        SwarmTask task,
        List<(string title, string result)> findings)
    {
        return new Services.Hive.HiveTaskBundle
        {
            TaskId       = task.Id,
            SessionId    = _runId,
            Role         = task.Role.ToString(),
            Title        = task.Title,
            Spec         = BuildWorkerUserMessage(task, findings, _targetLanguage, Tasks),
            ProjectGoal  = _currentUserGoal,
            TargetLanguage = _targetLanguage,
            ModelHint    = task.Role == SwarmWorkerRole.Researcher ? _researcherModel : _coderModel,
            WarchiefUrl  = _distributedQueue!.BaseUrl,
            TimeoutMs    = 300_000,
            UpstreamArtifacts = findings.Select(f => new Services.Hive.HiveArtifact
            {
                Source  = f.title,
                Role    = "Researcher",
                Content = f.result,
            }).ToList(),
        };
    }

    // ── Worker execution ──────────────────────────────────────────────────────

    private async Task RunWorkerAsync(
        SwarmTask task,
        List<(string title, string result)> researchContext,
        CancellationToken ct,
        string? overrideUserMessage = null)
    {
        task.Status    = SwarmTaskStatus.InProgress;
        task.StartedAt = DateTime.UtcNow;
        OnTaskChanged?.Invoke(task);
        Activity($"{task.RoleIcon} {task.Title} — starting", AgentKey(task.Role));

        await SaveAgentTaskFileAsync(task);

        try
        {
            var userMsg    = overrideUserMessage
                          ?? BuildWorkerUserMessage(task, researchContext, _targetLanguage, Tasks);
            _trace?.WriteUserMessage(userMsg, context: $"{task.Role}:{task.Title}");

            var roleKey    = task.Role.ToString();
            var agentFile  = await LoadAgentFileAsync(roleKey);
            var basePrompt = WorkerSystemPrompt(task.Role);
            var sysPrompt  = string.IsNullOrWhiteSpace(agentFile)
                ? basePrompt
                : basePrompt + "\n\n## Custom agent instructions:\n" + agentFile;

            var history = new List<AgentMessage>
            {
                new() { Role = MessageRole.System, Content = sysPrompt },
                new() { Role = MessageRole.User,   Content = userMsg }
            };

            // GOBLIN MIND: capability-aware model selection
            var modelForRole = GetCapableModel(task.Role);
            var agentKey     = AgentKey(task.Role);
            var tools        = GetWorkerTools(task.Role);
            var noThink      = ShouldDisableThinking(modelForRole);

            // HIVE MIND: use the node assigned to this task (or local if unassigned)
            var nodeOllama = GetOllamaForTask(task);
            if (!string.IsNullOrEmpty(task.TargetNodeName))
                Activity($"🐝 → {task.TargetNodeName}", agentKey);

            await RunWorkerLoopAsync(task, history, tools, modelForRole, noThink, agentKey, ct, nodeOllama);

            // Preserve history so ContinueWorkerAsync can resume the conversation
            task.ConversationHistory = history;
            task.Status      = SwarmTaskStatus.Done;
            task.CompletedAt = DateTime.UtcNow;

            _trace?.WriteAssistantMessage(task.Result ?? "", agent: task.Role.ToString().ToLower(), model: modelForRole);
            await SaveTaskResultAsync(task);
            Activity($"{task.RoleIcon} {task.Title} — complete", AgentKey(task.Role));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            task.Status       = SwarmTaskStatus.Error;
            task.ErrorMessage = ex.Message;
            Activity($"{task.RoleIcon} {task.Title} — ERROR: {ex.Message}", AgentKey(task.Role));
            _trace?.WriteEvent("worker_error", $"{task.Role}:{task.Title} — {ex.Message}");
        }

        OnTaskChanged?.Invoke(task);
    }

    /// <summary>
    /// Core agentic loop. Drives a worker through tool-calling steps until the model
    /// stops emitting tool calls or the step limit is hit.
    /// Handles: ask_user pause/resume, mid-run steer injection, file-write accounting.
    /// </summary>
    private async Task RunWorkerLoopAsync(
        SwarmTask                    task,
        List<AgentMessage>           history,
        IReadOnlyList<ToolDefinition> tools,
        string                       model,
        bool                         noThink,
        string                       agentKey,
        CancellationToken            ct,
        OllamaClient?                nodeOllama = null)
    {
        const int MaxWorkerSteps = 16;
        var finalSb = new StringBuilder();

        // Convert ToolDefinition objects → serializable JsonObject schemas INLINE.
        // We build JsonObject nodes directly instead of going through SchemaGenerator
        // or any method that calls JsonSerializer.Serialize(toolDefinition) (even
        // indirectly via ToOllamaSchema() which returns 'object' and can confuse
        // the .NET 10 serializer's runtime-type discovery into touching Handler).
        // JsonObject is natively handled by System.Text.Json — zero reflection on Func<>.
        IReadOnlyList<object>? toolsPayload = null;
        if (tools.Count > 0)
        {
            var schemas = new List<object>(tools.Count);
            foreach (var td in tools)
            {
                var props = new JsonObject();
                foreach (var (key, param) in td.Parameters)
                    props[key] = new JsonObject
                    {
                        ["type"]        = JsonValue.Create(param.Type),
                        ["description"] = JsonValue.Create(param.Description)
                    };

                var req = new JsonArray();
                foreach (var r in td.Required)
                    req.Add(JsonValue.Create(r));

                schemas.Add((object)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]        = JsonValue.Create(td.Name),
                        ["description"] = JsonValue.Create(td.Description),
                        ["parameters"]  = new JsonObject
                        {
                            ["type"]       = "object",
                            ["properties"] = props,
                            ["required"]   = req
                        }
                    }
                });
            }
            toolsPayload = schemas;
        }

        for (int step = 0; step < MaxWorkerSteps && !ct.IsCancellationRequested; step++)
        {
            // ── Inject any pending steer messages as user guidance ────────
            if (_steerQueues.TryGetValue(task.Id, out var steerQ))
            {
                string? steerMsg = null;
                lock (steerQ) { if (steerQ.Count > 0) steerMsg = steerQ.Dequeue(); }
                if (steerMsg != null)
                {
                    history.Add(new AgentMessage
                    {
                        Role    = MessageRole.User,
                        Content = $"[User guidance]: {steerMsg}\nIncorporate this into your next action.",
                        Status  = MessageStatus.Complete,
                    });
                    Activity($"💬 Steer injected: {steerMsg[..Math.Min(60, steerMsg.Length)]}", agentKey);
                }
            }

            // ── Call the LLM ──────────────────────────────────────────────
            var pendingTcs = new List<ToolCall>();
            var contentSb  = new StringBuilder();

            await foreach (var token in (nodeOllama ?? _ollama).StreamCompletionAsync(
                model, history,
                tools:       toolsPayload,
                temperature: 0.25,
                maxTokens:   8192,
                onToolCall:  tc => pendingTcs.Add(tc),
                ct:          ct))
            {
                contentSb.Append(token);
                task.StreamBuffer = contentSb.ToString();
                OnWorkerToken?.Invoke(task.Id, token);
            }

            var content = contentSb.ToString();

            // Strip <think>…</think> blocks (Nemotron, Qwen3, DeepSeek-R)
            if (noThink && content.Contains("<think>"))
                content = Regex.Replace(content, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();

            finalSb.Append(content);

            // Fallback text-format tool call parsing
            if (pendingTcs.Count == 0 && !string.IsNullOrWhiteSpace(content))
            {
                var textCalls = TryParseTextToolCalls(content);
                if (textCalls.Count > 0)
                {
                    Activity($"🔍 Detected {textCalls.Count} text-format tool call(s)", agentKey);
                    pendingTcs.AddRange(textCalls);
                }
            }

            history.Add(new AgentMessage
            {
                Role      = MessageRole.Assistant,
                Content   = content,
                ToolCalls = pendingTcs,
                Status    = MessageStatus.Complete,
            });

            if (pendingTcs.Count == 0)
                break;

            // ── Execute each tool call ────────────────────────────────────
            foreach (var tc in pendingTcs)
            {
                var argSummary = string.Join(", ", tc.Arguments
                    .Take(2).Select(kv =>
                    {
                        var v = kv.Value?.ToString() ?? "";
                        return $"{kv.Key}={v[..Math.Min(30, v.Length)]}";
                    }));
                Activity($"🔧 {tc.Name}({argSummary})", agentKey);

                string result;

                // ── ask_user: pause worker and wait for user reply ────────
                if (tc.Name == "ask_user")
                {
                    result = await HandleAskUserAsync(task, tc, agentKey, ct);
                }
                else
                {
                    result = _toolRegistry is not null
                        ? await _toolRegistry.ExecuteAsync(tc, ct)
                        : "[ERROR] No tool registry available";
                }

                // Log file writes specially
                if (tc.Name == "write_file" && tc.Arguments.TryGetValue("path", out var pathObj))
                {
                    var fname = Path.GetFileName(pathObj?.ToString() ?? "");
                    if (!string.IsNullOrWhiteSpace(fname))
                        Activity($"→ {fname}", agentKey);
                    task.ToolFilesWritten++;
                }
                else if (tc.Name != "ask_user")
                {
                    var snip = result.Length > 80 ? result[..80].Replace('\n', ' ') + "…" : result.Replace('\n', ' ');
                    Activity($"  ↳ {snip}", agentKey);
                }

                var toolMsg = tc.IsTextFormat
                    ? new AgentMessage
                      {
                          Role    = MessageRole.User,
                          Content = $"[Tool result: {tc.Name}]\n{(string.IsNullOrWhiteSpace(result) ? "(no output)" : result)}",
                          Status  = MessageStatus.Complete,
                      }
                    : new AgentMessage
                      {
                          Role       = MessageRole.Tool,
                          Content    = result,
                          ToolCallId = tc.Id,
                          Status     = MessageStatus.Complete,
                      };
                history.Add(toolMsg);
            }
        }

        task.Result = finalSb.ToString();
    }

    /// <summary>
    /// Handles an ask_user tool call: sets task to WaitingForUser, fires OnTaskChanged so the UI
    /// can render the question, then awaits the user reply via a TaskCompletionSource.
    /// </summary>
    private async Task<string> HandleAskUserAsync(SwarmTask task, ToolCall tc, string agentKey, CancellationToken ct)
    {
        var q       = tc.Arguments.GetValueOrDefault("question")?.ToString() ?? "The worker has a question.";
        var rawOpts = tc.Arguments.GetValueOrDefault("options")?.ToString();
        var opts    = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawOpts))
        {
            try { opts = JsonSerializer.Deserialize<List<string>>(rawOpts) ?? []; }
            catch { /* ignore parse errors */ }
        }

        task.PendingQuestion = q;
        task.PendingOptions  = opts;
        task.Status          = SwarmTaskStatus.WaitingForUser;
        OnTaskChanged?.Invoke(task);
        Activity($"⏸ {task.RoleLabel} asks: {q[..Math.Min(80, q.Length)]}", agentKey);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replyChannels[task.Id] = tcs;

        string reply;
        try
        {
            reply = await tcs.Task.WaitAsync(ct);
        }
        finally
        {
            _replyChannels.TryRemove(task.Id, out _);
            task.PendingQuestion = null;
            task.PendingOptions  = [];
            task.Status          = SwarmTaskStatus.InProgress;
            OnTaskChanged?.Invoke(task);
        }

        Activity($"▶ User replied: {reply[..Math.Min(60, reply.Length)]}", agentKey);
        return $"User replied: \"{reply}\"";
    }

    // Matches filenames like scraper.py, main.ts, index.html, .agent.md, ARCHITECTURE.md
    private static readonly Regex FilenameInTitle = new(
        @"(?:^|[\s""'`(])(\.[a-zA-Z0-9_\-]+\.[a-z]{1,6}|[a-zA-Z0-9_\-]+\.[a-z]{1,6})(?=$|[\s""'`),])",
        RegexOptions.Compiled);

    private static string BuildWorkerUserMessage(
        SwarmTask task,
        List<(string title, string result)> researchContext,
        string targetLanguage = "",
        IReadOnlyList<SwarmTask>? allTasks = null)
    {
        var sb = new StringBuilder();

        // ── Language lock ─────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(targetLanguage))
            sb.AppendLine($"⚠ LANGUAGE LOCK: ALL code in this task MUST be written in {targetLanguage}. " +
                          $"Do NOT use C#, XAML, WPF, or any other language unless this task explicitly requires it.\n");

        // ── Filename enforcement (injected into user message for maximum model attention) ──
        // Extract filenames from the task title so the model can't substitute generic names.
        var titleFiles = FilenameInTitle.Matches(task.Title)
            .Select(m => m.Groups[1].Value)
            .Where(f => f.Length > 2)
            .Distinct()
            .ToList();

        // TESTER is verification-only — it has no write_file tool and must not be told to produce files.
        if (titleFiles.Count > 0 && task.Role is not SwarmWorkerRole.Researcher and not SwarmWorkerRole.Tester)
        {
            sb.AppendLine("⚡ REQUIRED OUTPUT FILE(S) — NON-NEGOTIABLE:");
            foreach (var f in titleFiles)
                sb.AppendLine($"  • You MUST call write_file(path=\"{f}\", content=...) — EXACTLY this filename.");
            sb.AppendLine("  • Do NOT invent alternative names (utils.py, helpers.py, output.py, etc.).");
            sb.AppendLine("  • Every required file above must exist on disk when you finish.\n");
        }

        // ── Sibling module awareness ───────────────────────────────────────────
        // Collect filenames being produced by ALL other coder/uidev tasks in this run.
        // This tells main.py's author that scraper.py, ollama_client.py etc. WILL exist
        // at runtime — so it must use real `import X` statements, never `X = None`.
        // TESTER is excluded: it needs to know what files exist, not import them as modules.
        if (allTasks is { Count: > 0 } && task.Role is not SwarmWorkerRole.Researcher and not SwarmWorkerRole.Tester)
        {
            var siblingModules = allTasks
                .Where(t => t.Id != task.Id && t.Role != SwarmWorkerRole.Researcher)
                .SelectMany(t => FilenameInTitle.Matches(t.Title)
                    .Select(m => m.Groups[1].Value))
                .Where(f => Regex.IsMatch(f, @"\.(py|js|ts|cs|rb|go|rs)$"))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Distinct()
                .ToList();

            if (siblingModules.Count > 0)
            {
                sb.AppendLine("⚡ SIBLING MODULES — produced by other workers in this swarm run:");
                sb.AppendLine("  These files WILL exist at runtime. Import them directly — NEVER assign None.\n");
                sb.AppendLine("  CORRECT imports:");
                foreach (var mod in siblingModules)
                    sb.AppendLine($"    import {mod}");
                sb.AppendLine();
                sb.AppendLine("  FORBIDDEN pattern (this will be rejected):");
                sb.AppendLine("    try:");
                foreach (var mod in siblingModules)
                    sb.AppendLine($"        {mod} = None  # ← FORBIDDEN");
                sb.AppendLine();
            }
        }

        // ── Research context ──────────────────────────────────────────────────
        if (researchContext.Count > 0)
        {
            sb.AppendLine("## Research findings from the Researcher:");
            foreach (var (title, result) in researchContext)
            {
                sb.AppendLine($"### {title}");
                sb.AppendLine(result);
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        sb.AppendLine("## Your task:");
        sb.AppendLine(task.Description);
        return sb.ToString();
    }

    // ── Tester helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a self-contained Tester task describing the files to verify
    /// and the expected entry point for the project's language.
    /// </summary>
    private SwarmTask BuildTesterTask(
        string goal, List<SwarmTask> completedTasks,
        string outputDir, string targetLanguage)
    {
        // Collect files actually on disk (workers may have written more than FILE: markers show)
        var onDisk = Directory.Exists(outputDir)
            ? string.Join(", ",
                Directory.GetFiles(outputDir, "*.*", SearchOption.TopDirectoryOnly)
                         .Select(Path.GetFileName)
                         .Where(f => !string.IsNullOrWhiteSpace(f)))
            : "(none yet)";

        var entryPoint = targetLanguage switch
        {
            "Python"              => "main.py",
            "JavaScript"          => "index.js",
            "TypeScript"          => "index.ts",
            "HTML/CSS/JavaScript" => "index.html",
            "Ruby"                => "main.rb",
            "Go"                  => "main.go",
            "Rust"                => "main.rs",
            _                     => "main.py",
        };

        var syntaxCmd = targetLanguage switch
        {
            "Python"     => $"python -m py_compile {entryPoint}",
            "JavaScript" => $"node --check {entryPoint}",
            "TypeScript" => $"npx tsc --noEmit",
            _            => $"python -m py_compile {entryPoint}",
        };

        return new SwarmTask
        {
            Role        = SwarmWorkerRole.Tester,
            Priority    = 3,
            Title       = $"Verify {entryPoint}",
            Description = $"""
                Project goal: {goal}

                Files on disk: {onDisk}
                Expected entry point: {entryPoint}
                Language: {(string.IsNullOrWhiteSpace(targetLanguage) ? "Python" : targetLanguage)}
                Syntax check command: {syntaxCmd}

                Step 1: list_files — confirm the files above exist.
                Step 2: run_shell("{syntaxCmd}") — check for syntax errors.
                Step 3: If Python, also run: run_shell("python -c \\"import importlib.util; spec=importlib.util.spec_from_file_location('m','{entryPoint}'); m=importlib.util.module_from_spec(spec)\\"")
                Step 4: Report STATUS: PASS or STATUS: FAIL with the exact error text.
                """,
        };
    }

    /// <summary>
    /// Builds a targeted fix task from the Tester's exact error report.
    /// Uses verbatim error text — never a paraphrase — so weak models can patch precisely.
    /// </summary>
    private static SwarmTask BuildFixTask(
        string testerResult, List<SwarmTask> tasks, string targetLanguage)
    {
        // Extract just the structured lines from the tester report
        var reportLines = testerResult
            .Split('\n')
            .Where(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith("STATUS:",  StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("ERRORS:",  StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("NOTES:",   StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("FILES_FOUND:", StringComparison.OrdinalIgnoreCase);
            })
            .Take(12);

        var errorBlock = string.Join("\n", reportLines);

        var filesToFix = tasks
            .Where(t => t.Role is SwarmWorkerRole.Coder or SwarmWorkerRole.UIDeveloper
                        && t.Status == SwarmTaskStatus.Done)
            .SelectMany(t => ExtractFileNames(t.Result ?? ""))
            .Distinct()
            .ToList();

        return new SwarmTask
        {
            Role        = SwarmWorkerRole.Coder,
            Priority    = 4,
            Title       = "Fix tester-reported errors",
            Description = $"""
                The Tester goblin ran the code and found errors. Fix ONLY the reported errors.

                TESTER REPORT:
                {errorBlock}

                INSTRUCTIONS:
                - Use read_file to read the current content of the broken file(s).
                - Use write_file to patch only the lines causing the error.
                - Do NOT rewrite files from scratch — minimal targeted fix only.
                - After writing, use run_shell to verify the fix (syntax check).

                FILES TO INSPECT: {string.Join(", ", filesToFix)}
                LANGUAGE: {(string.IsNullOrWhiteSpace(targetLanguage) ? "Python" : targetLanguage)}
                """,
        };
    }

    /// <summary>Returns true if the tester's output indicates a failure that should trigger a fix task.</summary>
    private static bool TesterReportedFailure(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        return result.Contains("STATUS: FAIL",    StringComparison.OrdinalIgnoreCase)
            || result.Contains("STATUS: PARTIAL", StringComparison.OrdinalIgnoreCase)
            || result.Contains("ModuleNotFoundError", StringComparison.Ordinal)
            || result.Contains("SyntaxError",         StringComparison.Ordinal)
            || result.Contains("ImportError",         StringComparison.Ordinal)
            || result.Contains("NameError",            StringComparison.Ordinal)
            || result.Contains("Traceback (most recent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the short STATUS/ERRORS/NOTES summary from the Tester's full output
    /// for injection into the Boss merge context (keeps it brief to save tokens).
    /// </summary>
    private static string ExtractTesterSummary(string result)
    {
        var lines = result.Split('\n');
        var summary = string.Join(" | ", lines
            .Where(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith("STATUS:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("ERRORS:", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("NOTES:",  StringComparison.OrdinalIgnoreCase);
            })
            .Take(3)
            .Select(l => l.Trim()));

        return string.IsNullOrWhiteSpace(summary)
            ? (result.Length > 200 ? result[..200] + "…" : result)
            : summary;
    }

    // ── Boss plan parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Escapes literal newlines/carriage-returns that appear inside JSON string values
    /// (LLMs sometimes emit them, which makes JsonNode.Parse throw).
    /// </summary>
    private static string SanitizeJson(string json)
    {
        var result  = new System.Text.StringBuilder(json.Length);
        bool inStr  = false, escaped = false;
        foreach (char c in json)
        {
            if (escaped)  { result.Append(c); escaped = false; continue; }
            if (c == '\\') { escaped = true;  result.Append(c); continue; }
            if (c == '"')  { inStr = !inStr;  result.Append(c); continue; }
            if (inStr && c == '\n') { result.Append("\\n"); continue; }
            if (inStr && c == '\r') { result.Append("\\r"); continue; }
            result.Append(c);
        }
        return result.ToString();
    }

    private List<SwarmTask> ParseBossPlan(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```"))
            {
                var nl = json.IndexOf('\n');
                if (nl >= 0) json = json[(nl + 1)..];
            }
            var end = json.LastIndexOf("```");
            if (end >= 0) json = json[..end];
            json = SanitizeJson(json.Trim());

            var node = JsonNode.Parse(json);
            var arr  = node?["tasks"]?.AsArray();
            if (arr is null) return FallbackTask(raw);

            var result = new List<SwarmTask>();
            foreach (var t in arr)
            {
                if (t is null) continue;
                var roleStr = t["role"]?.GetValue<string>()?.ToUpperInvariant() ?? "CODER";

                // ── Role alias map ──────────────────────────────────────────────────
                // Maps the logical role the boss emits to the execution lane the
                // runtime supports.  The boss system prompt only advertises
                // RESEARCHER / CODER / UIDEVELOPER to avoid model confusion, but the
                // parser can safely absorb a wider vocabulary from future fine-tuned
                // variants or user-authored plan JSON.
                //
                // Execution lanes and their capabilities:
                //   Researcher  — fetch/read/investigate; NO write_file
                //   Coder       — write_file, run_shell, read, grep, fetch
                //   UIDeveloper — write_file, run_shell, read, fetch (UI-focused prompt)
                //   Tester      — run_shell, read, list; NO write_file (read+execute only)
                //
                // Unknown logical roles fall through to Coder (safe default).
                var role = roleStr switch
                {
                    // ── Native execution lane names ────────────────────────────────
                    "RESEARCHER"                                          => SwarmWorkerRole.Researcher,
                    "UIDEVELOPER"                                         => SwarmWorkerRole.UIDeveloper,
                    "TESTER"                                              => SwarmWorkerRole.Tester,

                    // ── Planning / analysis → RESEARCHER ──────────────────────────
                    // These roles investigate and document; they do not produce code.
                    "ARCHITECT" or "PLANNER" or "REVIEWER" or "ANALYST"  => SwarmWorkerRole.Researcher,

                    // ── UI / frontend → UIDEVELOPER ────────────────────────────────
                    "FRONTEND_DEVELOPER" or "FRONTEND" or "UI"           => SwarmWorkerRole.UIDeveloper,

                    // ── Test / QA → TESTER ────────────────────────────────────────
                    "QA" or "QUALITY_ASSURANCE"                          => SwarmWorkerRole.Tester,

                    // ── Everything else → CODER ───────────────────────────────────
                    // Covers: CODER, BACKEND_DEVELOPER, BACKEND, DOCS, DOCUMENTATION,
                    // DEVOPS, RELEASE_MANAGER, SECURITY, PERFORMANCE, ML_ENGINEER,
                    // DATA_ENGINEER, and any future or unknown role strings.
                    _                                                     => SwarmWorkerRole.Coder
                };

                // Preserve the logical role only when it differs from the execution lane name
                // (so LogicalRole is null for RESEARCHER/CODER/UIDEVELOPER — no aliasing occurred)
                var executionName = role switch
                {
                    SwarmWorkerRole.Researcher  => "RESEARCHER",
                    SwarmWorkerRole.UIDeveloper => "UIDEVELOPER",
                    SwarmWorkerRole.Tester      => "TESTER",
                    _                           => "CODER"
                };
                var logicalRole = roleStr != executionName ? roleStr : null;

                result.Add(new SwarmTask
                {
                    Role        = role,
                    LogicalRole = logicalRole,
                    Priority    = t["priority"]?.GetValue<int>() ?? 2,
                    Title       = t["title"]?.GetValue<string>()       ?? "Task",
                    Description = t["description"]?.GetValue<string>() ?? "",
                });
            }
            return result.Count > 0 ? result : FallbackTask(raw);
        }
        catch { return FallbackTask(raw); }
    }

    private static List<SwarmTask> FallbackTask(string context) =>
    [
        new SwarmTask
        {
            Role        = SwarmWorkerRole.Coder,
            Priority    = 1,
            Title       = "Execute goal",
            Description = context.Length > 2000 ? context[..2000] : context,
        }
    ];

    // ── File I/O ──────────────────────────────────────────────────────────────

    private async Task SavePlanJsonAsync(string goal)
    {
        try
        {
            var plan = new
            {
                run_id          = _runId,
                goal,
                boss_model      = _bossModel,
                coder_model     = _coderModel,
                researcher_model = _researcherModel,
                started_at      = DateTime.UtcNow,
                tasks = Tasks.Select(t => new
                {
                    id       = t.Id,
                    role     = t.Role.ToString(),
                    priority = t.Priority,
                    title    = t.Title,
                    description = t.Description
                })
            };
            var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(RunDir, "plan.json"), json);
        }
        catch { }
    }

    private async Task SaveSwarmRunJsonAsync(string goal, List<SwarmTask> tasks)
    {
        try
        {
            var run = new
            {
                run_id           = _runId,
                goal,
                boss_model       = _bossModel,
                coder_model      = _coderModel,
                researcher_model = _researcherModel,
                completed_at     = DateTime.UtcNow,
                tasks = tasks.Select(t => new
                {
                    id          = t.Id,
                    role        = t.Role.ToString(),
                    title       = t.Title,
                    status      = t.Status.ToString(),
                    started_at  = t.StartedAt,
                    completed_at = t.CompletedAt
                }),
                output_files = Directory.Exists(OutputProjectDir)
                    ? Directory.GetFiles(OutputProjectDir, "*", SearchOption.AllDirectories)
                              .Select(f => f.Replace(OutputProjectDir, "").TrimStart(Path.DirectorySeparatorChar))
                              .ToArray()
                    : Array.Empty<string>()
            };
            var json = JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(RunDir, "swarm_run.json"), json);
        }
        catch { }
    }

    private async Task SaveAgentTaskFileAsync(SwarmTask task)
    {
        try
        {
            var path    = Path.Combine(AgentsTaskDir, $"agent_{task.Role.ToString().ToLower()}_task.md");
            var content = $"# {task.RoleIcon} {task.Role} — {task.Title}\n\n{task.Description}";
            await File.WriteAllTextAsync(path, content);
        }
        catch { }
    }

    private async Task SaveTaskResultAsync(SwarmTask task)
    {
        try
        {
            var path    = Path.Combine(AgentsTaskDir, $"agent_{task.Role.ToString().ToLower()}_result.md");
            var content = $"# {task.RoleIcon} {task.Title}\n\n**Status:** {task.Status}\n\n{task.Result}";
            await File.WriteAllTextAsync(path, content);
        }
        catch { }
    }
}
