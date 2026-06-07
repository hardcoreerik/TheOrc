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
    private readonly string       _model;
    private readonly string?      _workspaceRoot;
    private CancellationTokenSource? _cts;

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

    public SwarmSession(OllamaClient ollama, string model, string? workspaceRoot)
    {
        _ollama        = ollama;
        _model         = model;
        _workspaceRoot = workspaceRoot;
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

        try
        {
            // ── Phase 1: Boss decomposes ──────────────────────────────────────
            var bossRaw = await RunBossDecomposeAsync(userGoal, ct);
            Tasks = ParseBossPlan(bossRaw);

            if (Tasks.Count == 0)
            {
                OnError?.Invoke("Boss returned no tasks — try rephrasing your goal.");
                return;
            }
            OnTasksPlanned?.Invoke(Tasks);
            await SaveSwarmStateAsync(userGoal);

            // ── Phase 2a: Research tasks run first ────────────────────────────
            var researchers = Tasks.Where(t => t.Role == SwarmWorkerRole.Researcher).ToList();
            var others      = Tasks.Where(t => t.Role != SwarmWorkerRole.Researcher).ToList();

            if (researchers.Count > 0)
                await Task.WhenAll(researchers.Select(t => RunWorkerAsync(t, [], ct)));

            // Collect all research that succeeded
            var findings = researchers
                .Where(t => t.Status == SwarmTaskStatus.Done && t.Result is not null)
                .Select(t => (t.Title, t.Result!))
                .ToList();

            // ── Phase 2b: Remaining workers (with research context) ────────────
            if (others.Count > 0)
                await Task.WhenAll(others.Select(t => RunWorkerAsync(t, findings, ct)));

            // ── Phase 3: Boss merges ──────────────────────────────────────────
            var merged = await RunBossMergeAsync(userGoal, Tasks, ct);
            await SaveMergedResultAsync(merged);

            OnSwarmComplete?.Invoke(merged);
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("Swarm stopped.");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Swarm error: {ex.Message}");
        }
        finally
        {
            OnStopped?.Invoke();
        }
    }

    // ── Boss: Decompose ───────────────────────────────────────────────────────

    private const string BossDecomposeSystemPrompt = """
        You are the Orchestrator (Boss) of a multi-agent AI coding system.
        You coordinate three specialist worker types:
          • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code
          • CODER       — writes full implementation code using the researcher's findings
          • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling

        Given a user's coding goal, break it into 1–3 concurrent subtasks.
        Assign each subtask to the best-fit worker role.

        Rules:
        - RESEARCHER tasks always get priority 1 (they run first)
        - CODER and UIDEVELOPER tasks get priority 2 (run after research completes)
        - If no research is needed, assign a single CODER task with priority 1
        - Descriptions must be self-contained — workers cannot ask follow-up questions
        - Maximum 3 tasks total

        Respond with ONLY valid JSON — no markdown fences, no preamble:
        {
          "plan": "one-sentence overall approach",
          "tasks": [
            {
              "role": "RESEARCHER",
              "priority": 1,
              "title": "Short descriptive title",
              "description": "Detailed, self-contained instructions for this worker."
            }
          ]
        }
        """;

    private async Task<string> RunBossDecomposeAsync(string userGoal, CancellationToken ct)
    {
        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = BossDecomposeSystemPrompt },
            new() { Role = MessageRole.User,   Content = $"Goal: {userGoal}" }
        };

        OnBossToken?.Invoke("▶ Boss is planning tasks…\n\n");

        var sb = new StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            _model, history, temperature: 0.15, maxTokens: 2048, ct: ct))
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
        ctx.AppendLine("Output the final complete result. For code, output full working files.");

        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = "You are the Boss agent. Merge your workers' outputs into the final deliverable." },
            new() { Role = MessageRole.User,   Content = ctx.ToString() }
        };

        OnBossToken?.Invoke("\n\n──── BOSS MERGING RESULTS ────\n\n");

        var result = new StringBuilder();
        await foreach (var token in _ollama.StreamCompletionAsync(
            _model, history, temperature: 0.2, maxTokens: 6144, ct: ct))
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
            var userMsg = BuildWorkerUserMessage(task, researchContext);

            var history = new List<AgentMessage>
            {
                new() { Role = MessageRole.System, Content = WorkerSystemPrompt(task.Role) },
                new() { Role = MessageRole.User,   Content = userMsg }
            };

            var sb = new StringBuilder();
            await foreach (var token in _ollama.StreamCompletionAsync(
                _model, history, temperature: 0.3, maxTokens: 5120, ct: ct))
            {
                sb.Append(token);
                task.StreamBuffer = sb.ToString();
                OnWorkerToken?.Invoke(task.Id, token);
            }

            task.Result      = sb.ToString();
            task.Status      = SwarmTaskStatus.Done;
            task.CompletedAt = DateTime.UtcNow;

            await SaveTaskResultAsync(task);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            task.Status       = SwarmTaskStatus.Error;
            task.ErrorMessage = ex.Message;
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
                model       = _model,
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
