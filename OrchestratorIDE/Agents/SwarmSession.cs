using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Agents;

/// <summary>
/// Orchestrates a 1-boss + up-to-3-worker Swarm run.
///
/// Phase 1:  Boss decomposes the user's goal into RESEARCHER / CODER / UIDEVELOPER tasks (JSON).
/// Phase 2a: RESEARCHER tasks run first (parallel) — coders depend on their findings.
/// Phase 2b: CODER + UIDEVELOPER tasks run concurrently with research context injected.
/// Phase 3:  Boss merges all worker results into a final deliverable.
///
/// All results are saved to &lt;workspaceRoot&gt;/.orc/swarm/.
/// Parallel inference requires OLLAMA_NUM_PARALLEL ≥ 3 (set by OllamaParallelHelper).
/// </summary>
public class SwarmSession
{
    private readonly OllamaClient _ollama;
    private readonly string       _bossModel;        // orchestrator — planning + merge
    private readonly string       _coderModel;       // Coder + UIDeveloper roles
    private readonly string       _researcherModel;  // Researcher role (may differ for VRAM savings)
    private readonly string?      _workspaceRoot;
    private CancellationTokenSource? _cts;
    private SwarmTraceLogger?     _trace;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action?                         OnStarted;
    public event Action?                         OnStopped;
    public event Action<string>?                 OnBossToken;      // streaming boss tokens
    public event Action<string, string>?         OnWorkerToken;    // taskId, token
    public event Action<List<SwarmTask>>?        OnTasksPlanned;   // after boss decompose
    public event Action<SwarmTask>?              OnTaskChanged;    // status change
    public event Action<string>?                 OnSwarmComplete;  // merged result
    public event Action<string>?                 OnError;

    // ── Public state ──────────────────────────────────────────────────────────
    public List<SwarmTask> Tasks   { get; private set; } = [];
    public bool            IsRunning => _cts is { IsCancellationRequested: false };

    /// <param name="bossModel">Orchestrator model — used for planning and merging.</param>
    /// <param name="workerModel">Coder + UIDeveloper model. Defaults to bossModel.</param>
    /// <param name="researcherModel">
    /// Researcher model. Can be a lower-quant or lighter model to save VRAM during
    /// the research phase. Ollama evicts it before the coder phase loads when it
    /// differs from <paramref name="workerModel"/>. Defaults to workerModel.
    /// </param>
    public SwarmSession(OllamaClient ollama, string bossModel, string? workspaceRoot,
        string? workerModel = null, string? researcherModel = null)
    {
        _ollama           = ollama;
        _bossModel        = bossModel;
        _coderModel       = workerModel      ?? bossModel;
        _researcherModel  = researcherModel  ?? _coderModel;
        _workspaceRoot    = workspaceRoot;
    }

    // ── Directive injection (mid-swarm steering from the user) ───────────────
    private readonly List<string> _directives = [];

    /// <summary>
    /// Injects a user directive mid-swarm. It is echoed to the Boss stream immediately
    /// and folded into the merge phase context so the Boss can reflect on it.
    /// Workers already running are not interrupted — directive takes effect at merge.
    /// </summary>
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
        OnStopped?.Invoke();
    }

    /// <summary>Launches the swarm asynchronously (fire-and-forget friendly).</summary>
    public Task RunAsync(string userGoal) => RunInternalAsync(userGoal);

    // ── Orchestration ─────────────────────────────────────────────────────────

    private async Task RunInternalAsync(string userGoal)
    {
        _cts   = new CancellationTokenSource();
        var ct = _cts.Token;
        Tasks  = [];
        OnStarted?.Invoke();

        // Open trace logger — one file per run session
        Directory.CreateDirectory(SwarmDir);
        _trace = new SwarmTraceLogger(SwarmDir);
        _trace.WriteSessionMeta(
            _workspaceRoot ?? SwarmDir,
            _bossModel, _coderModel, _researcherModel, userGoal);

        try
        {
            // ── Phase 1: Boss decomposes ──────────────────────────────────────
            _trace.WriteEvent("phase_start", "Boss planning: decomposing goal into tasks");
            _trace.WriteUserMessage(userGoal, context: "user_goal");
            var bossRaw = await RunBossDecomposeAsync(userGoal, ct);
            Tasks = ParseBossPlan(bossRaw);

            if (Tasks.Count == 0)
            {
                OnError?.Invoke("Boss returned no tasks — try rephrasing your goal.");
                return;
            }

            // Log boss plan output
            _trace.WriteAssistantMessage(bossRaw, agent: "boss", model: _bossModel);
            _trace.WriteEvent("tasks_planned", $"{Tasks.Count} task(s) planned: " +
                string.Join(", ", Tasks.Select(t => $"{t.Role}:{t.Title}")));

            OnTasksPlanned?.Invoke(Tasks);
            await SaveSwarmStateAsync(userGoal);

            // ── Phase 2a: Research tasks run first ────────────────────────────
            var researchers = Tasks.Where(t => t.Role == SwarmWorkerRole.Researcher).ToList();
            var others      = Tasks.Where(t => t.Role != SwarmWorkerRole.Researcher).ToList();

            if (researchers.Count > 0)
            {
                _trace.WriteEvent("phase_start", $"Research phase: {researchers.Count} researcher(s) running");
                await Task.WhenAll(researchers.Select(t => RunWorkerAsync(t, [], ct)));
            }

            // Evict researcher model from VRAM before coder phase when models differ —
            // this frees headroom for 3 concurrent coder/uidev workers on 16 GB GPUs.
            if (researchers.Count > 0 && _researcherModel != _coderModel)
            {
                _trace.WriteEvent("model_evict", $"Evicting {_researcherModel} to free VRAM before coder phase");
                await _ollama.EvictModelAsync(_researcherModel, ct);
            }

            // Collect all research that succeeded
            var findings = researchers
                .Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null)
                .Select(t => (t.Title, t.Result!))
                .ToList();

            // ── Phase 2b: Coder + UIDev workers run concurrently (up to 3) ────
            if (others.Count > 0)
            {
                _trace.WriteEvent("phase_start", $"Coder/UIDev phase: {others.Count} worker(s) running concurrently");
                await Task.WhenAll(others.Select(t => RunWorkerAsync(t, findings, ct)));
            }

            // ── Phase 3: Boss merges ──────────────────────────────────────────
            _trace.WriteEvent("phase_start", "Boss merge: synthesising all worker results");
            var merged = await RunBossMergeAsync(userGoal, Tasks, ct);
            await SaveMergedResultAsync(merged);

            _trace.WriteAssistantMessage(merged, agent: "boss_merge", model: _bossModel);
            _trace.WriteEvent("swarm_complete", "All phases done — final deliverable produced");

            OnSwarmComplete?.Invoke(merged);
        }
        catch (OperationCanceledException)
        {
            _trace?.WriteEvent("swarm_cancelled", "Run was stopped by user");
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

    // ── Per-role agent files (.orc/agents/{role}.md) ─────────────────────────

    /// <summary>
    /// Directory that holds one Markdown file per agent role.
    /// Users edit these to customise each worker's behaviour.
    /// Files are auto-created with opinionated defaults on first run.
    /// </summary>
    private string AgentsDir =>
        Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "agents");

    private string AgentFilePath(string role) =>
        Path.Combine(AgentsDir, $"{role.ToLower()}.md");

    /// <summary>
    /// Reads the agent file for <paramref name="role"/>.
    /// Creates it with defaults when it does not yet exist.
    /// Returns an empty string on any read error so the swarm can continue.
    /// </summary>
    private async Task<string> LoadAgentFileAsync(string role)
    {
        var path = AgentFilePath(role);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(AgentsDir);
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
            When merging, produce one cohesive deliverable — never just concatenate.
            """,

        "researcher" => """
            # Researcher Agent — Custom Instructions
            Focus on practical, actionable findings. Always include:
            - Specific library names and versions to use
            - Key API endpoints or methods with example payloads
            - Known gotchas, rate limits, or auth requirements
            - A concrete recommendation for the coder on what to use and why
            Do NOT write final production code — document and recommend only.
            """,

        "coder" => """
            # Coder Agent — Custom Instructions
            Write production-ready code. Always:
            - Include proper error handling (try/catch, null checks)
            - Add comments on non-obvious logic
            - Use the exact libraries and approaches the researcher recommended
            - Output complete, runnable files — not snippets or pseudocode
            - Default language: C# unless the task specifies otherwise
            """,

        "uideveloper" => """
            # UI Developer Agent — Custom Instructions
            Follow TheOrc's visual design system:
            - Backgrounds: #161616 app / #1E1E1E panels / #0E120E sidebar
            - Accent green: #76B900 (hover: #8FD120)
            - Text: #D4D4D4 normal / #888888 muted / #FFFFFF heading
            - Borders: #2E3A2E / #333333
            - CornerRadius: 6px cards, 4px buttons, 3px chips
            For WPF: output complete XAML + code-behind. No code-behind logic in XAML.
            For web: semantic HTML5 + scoped CSS. No frameworks unless specified.
            """,

        _ => $"# {role} Agent — Custom Instructions\nComplete the assigned task thoroughly."
    };

    // ── Boss: Decompose ───────────────────────────────────────────────────────

    private const string BossDecomposeSystemPrompt = """
        You are TheOrc — the Orchestrator of a multi-agent AI coding swarm.
        You direct three specialist minions:
          • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code
          • CODER       — writes full implementation code using the researcher's findings
          • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling

        Given a user's coding goal, break it into 1–3 concurrent subtasks.
        Assign each subtask to the best-fit minion role.

        Rules:
        - RESEARCHER tasks always get priority 1 (they run first, alone)
        - CODER and UIDEVELOPER tasks get priority 2 (run concurrently after research)
        - If no research is needed, skip RESEARCHER and assign CODER/UIDEVELOPER tasks directly
        - Descriptions must be self-contained — minions cannot ask follow-up questions
        - Maximum 4 tasks total: up to 1 RESEARCHER + up to 3 CODER/UIDEVELOPER
        - Prefer 3 priority-2 tasks when the goal has distinct implementation concerns

        Respond with ONLY valid JSON — no markdown fences, no preamble:
        {
          "plan": "one-sentence overall approach",
          "tasks": [
            {
              "role": "RESEARCHER",
              "priority": 1,
              "title": "Short descriptive title",
              "description": "Detailed, self-contained instructions for this minion."
            }
          ]
        }
        """;

    private async Task<string> RunBossDecomposeAsync(string userGoal, CancellationToken ct)
    {
        // Load any user customisations from .orc/agents/boss.md
        var bossFile = await LoadAgentFileAsync("boss");
        var sysPrompt = string.IsNullOrWhiteSpace(bossFile)
            ? BossDecomposeSystemPrompt
            : BossDecomposeSystemPrompt + "\n\n## Custom boss instructions:\n" + bossFile;

        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = sysPrompt },
            new() { Role = MessageRole.User,   Content = $"Goal: {userGoal}" }
        };

        OnBossToken?.Invoke("⬡ TheOrc is planning the swarm…\n\n");

        var sb = new StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            _bossModel, history, temperature: 0.15, maxTokens: 2048, ct: ct))
        {
            sb.Append(token);
            OnBossToken?.Invoke(token);
        }
        return sb.ToString();
    }

    // ── Boss: Merge ───────────────────────────────────────────────────────────

    private async Task<string> RunBossMergeAsync(
        string userGoal, List<SwarmTask> tasks, CancellationToken ct)
    {
        var ctx = new StringBuilder();
        ctx.AppendLine("All workers have completed. Synthesize their results into a final deliverable.");
        ctx.AppendLine();
        foreach (var t in tasks.Where(t => t.Status == SwarmTaskStatus.Done))
        {
            ctx.AppendLine($"## {t.RoleIcon} {t.Title}");
            ctx.AppendLine(t.Result ?? "(empty)");
            ctx.AppendLine();
        }
        ctx.AppendLine($"Original goal: {userGoal}");

        // Include any mid-swarm directives from the user
        if (_directives.Count > 0)
        {
            ctx.AppendLine();
            ctx.AppendLine("## User directives received during this swarm run:");
            foreach (var d in _directives)
                ctx.AppendLine($"- {d}");
            ctx.AppendLine("Incorporate these directives into your final deliverable.");
        }

        ctx.AppendLine("Output the final complete result. For code, output full working files.");

        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = "You are TheOrc, the Orchestrator. Merge your minions' outputs into a single final deliverable." },
            new() { Role = MessageRole.User,   Content = ctx.ToString() }
        };

        OnBossToken?.Invoke("\n\n──── ⬡ TheOrc is merging results ────\n\n");

        var result = new StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            _bossModel, history, temperature: 0.2, maxTokens: 6144, ct: ct))
        {
            result.Append(token);
            OnBossToken?.Invoke(token);
        }
        return result.ToString();
    }

    // ── Worker system prompts ─────────────────────────────────────────────────

    private static string WorkerSystemPrompt(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher => """
            You are a RESEARCHER in a multi-agent AI coding system.
            Your job: investigate, discover, and document — do NOT write final production code.
            Return: relevant API endpoints, library names and versions, code snippets from docs,
            key constraints, gotchas, and specific recommendations for the coder.
            Be thorough. The coder depends entirely on your findings.
            """,

        SwarmWorkerRole.Coder => """
            You are a CODER in a multi-agent AI coding system.
            Your job: write clean, complete, production-ready implementation code.
            Use the research findings provided to make informed technology choices.
            Output complete files, not snippets. Include error handling and comments.
            Default language: C# unless the task specifies otherwise.
            """,

        SwarmWorkerRole.UIDeveloper => """
            You are a UIDEVELOPER in a multi-agent AI coding system.
            Your job: write complete UI code — WPF XAML + code-behind, or HTML/CSS as appropriate.
            Follow the project's existing visual style: dark theme, NVIDIA green accent (#76B900),
            background #161616, text #D4D4D4, borders #2E3A2E.
            Output complete, self-contained files.
            """,

        _ => "You are a worker agent. Complete the assigned task thoroughly."
    };

    // ── Worker execution ──────────────────────────────────────────────────────

    private async Task RunWorkerAsync(
        SwarmTask task,
        List<(string title, string result)> researchContext,
        CancellationToken ct)
    {
        task.Status    = SwarmTaskStatus.InProgress;
        task.StartedAt = DateTime.UtcNow;
        OnTaskChanged?.Invoke(task);

        try
        {
            var userMsg     = BuildWorkerUserMessage(task, researchContext);

            // Trace: log the task assignment as a user-side message
            _trace?.WriteUserMessage(userMsg, context: $"{task.Role}:{task.Title}");
            var roleKey     = task.Role.ToString();  // "Researcher" / "Coder" / "UIDeveloper"
            var agentFile   = await LoadAgentFileAsync(roleKey);
            var basePrompt  = WorkerSystemPrompt(task.Role);
            var sysPrompt   = string.IsNullOrWhiteSpace(agentFile)
                ? basePrompt
                : basePrompt + "\n\n## Custom agent instructions:\n" + agentFile;

            var history = new List<AgentMessage>
            {
                new() { Role = MessageRole.System, Content = sysPrompt },
                new() { Role = MessageRole.User,   Content = userMsg }
            };

            // Route to the model registered for this role
            var modelForRole = task.Role == SwarmWorkerRole.Researcher
                ? _researcherModel
                : _coderModel;

            var sb = new StringBuilder();
            await foreach (var token in _ollama.StreamCompletionAsync(
                modelForRole, history, temperature: 0.3, maxTokens: 5120, ct: ct))
            {
                sb.Append(token);
                task.StreamBuffer = sb.ToString();
                OnWorkerToken?.Invoke(task.Id, token);
            }

            task.Result      = sb.ToString();
            task.Status      = SwarmTaskStatus.Done;
            task.CompletedAt = DateTime.UtcNow;

            // Trace: log the worker's completed output
            _trace?.WriteAssistantMessage(task.Result, agent: task.Role.ToString().ToLower(), model: modelForRole);

            await SaveTaskResultAsync(task);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            task.Status       = SwarmTaskStatus.Error;
            task.ErrorMessage = ex.Message;
            _trace?.WriteEvent("worker_error", $"{task.Role}:{task.Title} — {ex.Message}");
        }

        OnTaskChanged?.Invoke(task);
    }

    private static string BuildWorkerUserMessage(
        SwarmTask task,
        List<(string title, string result)> researchContext)
    {
        if (researchContext.Count == 0)
            return task.Description;

        var sb = new StringBuilder();
        sb.AppendLine("## Research findings from the Researcher worker:");
        foreach (var (title, result) in researchContext)
        {
            sb.AppendLine($"### {title}");
            sb.AppendLine(result);
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Your task:");
        sb.AppendLine(task.Description);
        return sb.ToString();
    }

    // ── Boss plan parsing ─────────────────────────────────────────────────────

    private List<SwarmTask> ParseBossPlan(string raw)
    {
        try
        {
            // Strip markdown code fences if the model wrapped them
            var json = raw.Trim();
            if (json.StartsWith("```"))
            {
                var nl = json.IndexOf('\n');
                if (nl >= 0) json = json[(nl + 1)..];
            }
            var end = json.LastIndexOf("```");
            if (end >= 0) json = json[..end];
            json = json.Trim();

            var node  = JsonNode.Parse(json);
            var arr   = node?["tasks"]?.AsArray();
            if (arr is null) return FallbackTask(raw);

            var result = new List<SwarmTask>();
            foreach (var t in arr)
            {
                if (t is null) continue;
                var roleStr = t["role"]?.GetValue<string>()?.ToUpperInvariant() ?? "CODER";
                var role = roleStr switch
                {
                    "RESEARCHER"  => SwarmWorkerRole.Researcher,
                    "UIDEVELOPER" => SwarmWorkerRole.UIDeveloper,
                    _             => SwarmWorkerRole.Coder
                };
                result.Add(new SwarmTask
                {
                    Role        = role,
                    Priority    = t["priority"]?.GetValue<int>() ?? 2,
                    Title       = t["title"]?.GetValue<string>() ?? "Task",
                    Description = t["description"]?.GetValue<string>() ?? "",
                });
            }
            return result.Count > 0 ? result : FallbackTask(raw);
        }
        catch
        {
            return FallbackTask(raw);
        }
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

    private string SwarmDir => Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "swarm");

    private async Task SaveSwarmStateAsync(string goal)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(SwarmDir, "results"));
            var state = new
            {
                goal,
                boss_model        = _bossModel,
                worker_model      = _coderModel,
                researcher_model  = _researcherModel,
                started_at  = DateTime.UtcNow,
                tasks = Tasks.Select(t => new
                {
                    id       = t.Id,
                    role     = t.Role.ToString(),
                    priority = t.Priority,
                    title    = t.Title,
                    status   = t.Status.ToString()
                })
            };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(SwarmDir, "state.json"), json);
        }
        catch { /* non-fatal — swarm continues without persistence */ }
    }

    private async Task SaveTaskResultAsync(SwarmTask task)
    {
        try
        {
            var path    = Path.Combine(SwarmDir, "results", $"result_{task.Id}.md");
            var content = $"# {task.RoleIcon} {task.Title}\n\n**Status:** {task.Status}\n\n{task.Result}";
            await File.WriteAllTextAsync(path, content);
        }
        catch { /* non-fatal */ }
    }

    private async Task SaveMergedResultAsync(string merged)
    {
        try
        {
            var path = Path.Combine(SwarmDir, "results", "merged_result.md");
            await File.WriteAllTextAsync(path, merged);
        }
        catch { /* non-fatal */ }
    }
}
