using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Agents;

/// <summary>
/// Orchestrates a 1-boss + up-to-3-worker Swarm run.
///
/// Phase 1:  Boss decomposes the user's goal into RESEARCHER / CODER / UIDEVELOPER tasks (JSON).
/// Phase 2a: RESEARCHER tasks run first (parallel) — coders depend on their findings.
/// Phase 2b: CODER + UIDEVELOPER tasks run concurrently with research context injected.
/// Phase 3:  Boss merges all worker results into final_report.md.
///
/// Worker outputs are parsed for ### FILE: markers and written to
///   <workspaceRoot>/.orc/swarm/runs/<runId>/output/project/
/// after each phase completes.
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

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action?                         OnStarted;
    public event Action?                         OnStopped;
    public event Action<string>?                 OnBossToken;
    public event Action<string, string>?         OnWorkerToken;    // taskId, token
    public event Action<List<SwarmTask>>?        OnTasksPlanned;
    public event Action<SwarmTask>?              OnTaskChanged;
    public event Action<string>?                 OnSwarmComplete;  // merged result
    public event Action<string>?                 OnError;
    public event Action<string>?                 OnActivity;       // activity log entries

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

    private string RunsRoot        => Path.Combine(_workspaceRoot ?? Path.GetTempPath(), ".orc", "swarm", "runs");
    private string RunDir          => Path.Combine(RunsRoot, _runId);
    private string OutputProjectDir => Path.Combine(RunDir, "output", "project");
    private string AgentsTaskDir   => Path.Combine(RunDir, "agents");

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
        OnStopped?.Invoke();
    }

    public Task RunAsync(string userGoal) => RunInternalAsync(userGoal);

    // ── Orchestration ─────────────────────────────────────────────────────────

    private async Task RunInternalAsync(string userGoal)
    {
        _cts   = new CancellationTokenSource();
        var ct = _cts.Token;
        Tasks  = [];
        _runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        OnStarted?.Invoke();

        Directory.CreateDirectory(RunDir);
        Directory.CreateDirectory(OutputProjectDir);
        Directory.CreateDirectory(AgentsTaskDir);

        _trace = new SwarmTraceLogger(RunDir);
        _trace.WriteSessionMeta(_workspaceRoot ?? RunDir, _bossModel, _coderModel, _researcherModel, userGoal);

        try
        {
            // ── Phase 1: Boss decomposes ──────────────────────────────────────
            Activity("Boss decomposing goal into tasks…");
            _trace.WriteEvent("phase_start", "Boss planning");
            _trace.WriteUserMessage(userGoal, context: "user_goal");

            var bossRaw = await RunBossDecomposeAsync(userGoal, ct);
            Tasks = ParseBossPlan(bossRaw);

            if (Tasks.Count == 0)
            {
                OnError?.Invoke("Boss returned no tasks — try rephrasing your goal.");
                return;
            }

            _trace.WriteAssistantMessage(bossRaw, agent: "boss", model: _bossModel);
            _trace.WriteEvent("tasks_planned", $"{Tasks.Count} task(s): " +
                string.Join(", ", Tasks.Select(t => $"{t.Role}:{t.Title}")));

            Activity($"Planned {Tasks.Count} task(s): {string.Join(", ", Tasks.Select(t => t.Title))}");
            OnTasksPlanned?.Invoke(Tasks);
            await SavePlanJsonAsync(userGoal);

            // ── Phase 2a: Research tasks ──────────────────────────────────────
            var researchers = Tasks.Where(t => t.Role == SwarmWorkerRole.Researcher).ToList();
            var others      = Tasks.Where(t => t.Role != SwarmWorkerRole.Researcher).ToList();

            if (researchers.Count > 0)
            {
                Activity($"Research phase — {researchers.Count} researcher(s) running");
                _trace.WriteEvent("phase_start", $"Research phase: {researchers.Count} researcher(s)");
                await Task.WhenAll(researchers.Select(t => RunWorkerAsync(t, [], ct)));

                // Extract any files researchers wrote (documentation, sample data)
                foreach (var t in researchers.Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null))
                {
                    var n = ExtractAndWriteFiles(t.Result!, OutputProjectDir);
                    if (n > 0) Activity($"Researcher wrote {n} file(s)");
                }
            }

            // Evict researcher model when it differs from coder model
            if (researchers.Count > 0 && _researcherModel != _coderModel)
            {
                _trace.WriteEvent("model_evict", $"Evicting {_researcherModel}");
                await _ollama.EvictModelAsync(_researcherModel, ct);
            }

            var findings = researchers
                .Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null)
                .Select(t => (t.Title, t.Result!))
                .ToList();

            // ── Phase 2b: Coder + UIDev workers ──────────────────────────────
            if (others.Count > 0)
            {
                Activity($"Coder phase — {others.Count} worker(s) running concurrently");
                _trace.WriteEvent("phase_start", $"Coder/UIDev phase: {others.Count} worker(s)");
                await Task.WhenAll(others.Select(t => RunWorkerAsync(t, findings, ct)));

                var totalFiles = 0;
                foreach (var t in others.Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null))
                {
                    var n = ExtractAndWriteFiles(t.Result!, OutputProjectDir);
                    totalFiles += n;
                }
                if (totalFiles > 0)
                    Activity($"Workers wrote {totalFiles} file(s) to output/project/");
            }

            // ── Phase 3: Boss merge → final_report.md ────────────────────────
            Activity("Boss merging results → writing final_report.md");
            _trace.WriteEvent("phase_start", "Boss merge");
            var merged = await RunBossMergeAsync(userGoal, Tasks, ct);

            // Extract final_report.md and any extra files the boss added
            var mergeFiles = ExtractAndWriteFiles(merged, RunDir);       // final_report.md goes in RunDir
            ExtractAndWriteFiles(merged, OutputProjectDir);               // any project files too

            // Fallback: if boss didn't emit the FILE marker, write raw merge as final_report.md
            var finalReportPath = Path.Combine(RunDir, "final_report.md");
            if (!File.Exists(finalReportPath) && !string.IsNullOrWhiteSpace(merged))
                await File.WriteAllTextAsync(finalReportPath, merged, ct);

            await SaveSwarmRunJsonAsync(userGoal, Tasks);
            _trace.WriteAssistantMessage(merged, agent: "boss_merge", model: _bossModel);
            _trace.WriteEvent("swarm_complete", "All phases done");

            var projectFiles = Directory.Exists(OutputProjectDir)
                ? Directory.GetFiles(OutputProjectDir, "*", SearchOption.AllDirectories).Length
                : 0;
            Activity($"Swarm complete — {projectFiles} file(s) in output/project/");

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

    private void Activity(string msg) => OnActivity?.Invoke(msg);

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
            - Default language: C# unless the task specifies otherwise
            """,

        "uideveloper" => """
            # UI Developer Agent — Custom Instructions
            Follow TheOrc's visual design system:
            - Backgrounds: #161616 app / #1E1E1E panels
            - Accent green: #76B900
            - Text: #D4D4D4 / #888888 muted
            Output complete XAML + code-behind using the FILE format.
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

        Given a user's coding goal, break it into 1–4 concurrent subtasks.
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
        var bossFile  = await LoadAgentFileAsync("boss");
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
        // Build a compact summary of worker results (not full content — avoid context overflow)
        var ctx = new StringBuilder();
        ctx.AppendLine($"Original goal: {userGoal}");
        ctx.AppendLine();

        foreach (var t in tasks.Where(t => t.Status == SwarmTaskStatus.Done))
        {
            ctx.AppendLine($"## {t.RoleIcon} {t.Title} [{t.Role}]");
            // Include up to 1500 chars of each worker's output to avoid context overflow
            var workerOutput = t.Result ?? "(empty)";
            ctx.AppendLine(workerOutput.Length > 1500 ? workerOutput[..1500] + "\n…(truncated)" : workerOutput);
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
            5. Known risks or limitations

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

        CRITICAL — FILE OUTPUT FORMAT:
        When writing any file (code, README, test plan, documentation, sample data), use this exact format:

        ### FILE: relative/path/to/filename.ext
        ```language
        complete file content here — never truncated, never a snippet
        ```

        Rules:
        - Use relative paths from the project root (e.g., README.md, src/main.py, sample_data/data.csv)
        - Every required file must be output using this format — not described, actually written
        - Each file must be complete and production-ready — no placeholders, no TODOs
        - Multiple files: repeat the ### FILE: block for each one
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
            """,

        SwarmWorkerRole.Coder => $"""
            You are a CODER in a multi-agent AI coding system.
            Your job: write clean, complete, production-ready implementation code.
            Use the research findings provided to make informed technology choices.
            Output complete files — not snippets. Include error handling and comments.
            Default language: C# unless the task specifies otherwise.
            {FileOutputInstructions}
            """,

        SwarmWorkerRole.UIDeveloper => $"""
            You are a UIDEVELOPER in a multi-agent AI coding system.
            Your job: write complete UI code — WPF XAML + code-behind, or HTML/CSS as appropriate.
            Follow the project's existing visual style: dark theme, NVIDIA green accent (#76B900).
            Output complete, self-contained files.
            {FileOutputInstructions}
            """,

        _ => $"You are a worker agent. Complete the assigned task thoroughly.{FileOutputInstructions}"
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
        Activity($"{task.RoleIcon} {task.Title} — starting");

        // Save agent task file
        await SaveAgentTaskFileAsync(task);

        try
        {
            var userMsg   = BuildWorkerUserMessage(task, researchContext);
            _trace?.WriteUserMessage(userMsg, context: $"{task.Role}:{task.Title}");

            var roleKey   = task.Role.ToString();
            var agentFile = await LoadAgentFileAsync(roleKey);
            var basePrompt = WorkerSystemPrompt(task.Role);
            var sysPrompt  = string.IsNullOrWhiteSpace(agentFile)
                ? basePrompt
                : basePrompt + "\n\n## Custom agent instructions:\n" + agentFile;

            var history = new List<AgentMessage>
            {
                new() { Role = MessageRole.System, Content = sysPrompt },
                new() { Role = MessageRole.User,   Content = userMsg }
            };

            var modelForRole = task.Role == SwarmWorkerRole.Researcher
                ? _researcherModel
                : _coderModel;

            var sb = new StringBuilder();
            await foreach (var token in _ollama.StreamCompletionAsync(
                modelForRole, history, temperature: 0.3, maxTokens: 6144, ct: ct))
            {
                sb.Append(token);
                task.StreamBuffer = sb.ToString();
                OnWorkerToken?.Invoke(task.Id, token);
            }

            task.Result      = sb.ToString();
            task.Status      = SwarmTaskStatus.Done;
            task.CompletedAt = DateTime.UtcNow;

            _trace?.WriteAssistantMessage(task.Result, agent: task.Role.ToString().ToLower(), model: modelForRole);

            await SaveTaskResultAsync(task);
            Activity($"{task.RoleIcon} {task.Title} — complete");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            task.Status       = SwarmTaskStatus.Error;
            task.ErrorMessage = ex.Message;
            Activity($"{task.RoleIcon} {task.Title} — ERROR: {ex.Message}");
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
        sb.AppendLine("## Research findings from the Researcher:");
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
            var json = raw.Trim();
            if (json.StartsWith("```"))
            {
                var nl = json.IndexOf('\n');
                if (nl >= 0) json = json[(nl + 1)..];
            }
            var end = json.LastIndexOf("```");
            if (end >= 0) json = json[..end];
            json = json.Trim();

            var node = JsonNode.Parse(json);
            var arr  = node?["tasks"]?.AsArray();
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
