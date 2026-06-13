using System.Net.Http;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND Phase 3 — Worker-side polling agent.
///
/// When HiveWorkerMode is enabled this agent runs alongside the existing
/// HiveNodeServer. It polls the Warchief's HiveTaskQueue (port 7079) for
/// available tasks, claims them, executes them locally using OllamaClient,
/// and POSTs results back so the Warchief's SwarmSession can continue.
///
/// Execution model (Phase 3A): single-pass LLM call — the worker builds a
/// system + user message from the bundle and streams a full response.
/// Workers produce ### FILE: markers that the Warchief extracts as usual.
/// Multi-step tool calling on remote workers is Phase 3B.
///
/// Lanes: a worker advertises which task roles it accepts
/// (e.g. "researcher", "coder"). Empty lanes = accept all roles.
/// </summary>
public sealed class HiveWorkerAgent : IDisposable
{
    private CancellationTokenSource? _cts;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
    };

    // ── Configuration (set before Start) ─────────────────────────────────────

    /// <summary>This machine's display name reported in task results.</summary>
    public string WorkerId    { get; set; } = Environment.MachineName;

    /// <summary>This machine's Ollama base URL (e.g. "http://localhost:11434").</summary>
    public string WorkerUrl   { get; set; } = "http://localhost:11434";

    /// <summary>Warchief's HiveTaskQueue URL (e.g. "http://192.168.1.10:7079").</summary>
    public string WarchiefUrl { get; set; } = "";

    /// <summary>Task roles this worker accepts. Empty = all roles.</summary>
    public string[] Lanes     { get; set; } = [];

    // ── Inference configuration ───────────────────────────────────────────────

    public OllamaClient? Ollama         { get; set; }
    public string        CoderModel     { get; set; } = "";
    public string        ResearcherModel { get; set; } = "";

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>?       OnLog;
    public event Action<bool>?         OnStatusChanged;   // true = running, false = stopped
    public event Action<string, string>? OnTaskActivity;  // taskId, message

    // ── State ─────────────────────────────────────────────────────────────────

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // ── Main polling loop ─────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log($"🐝 Worker agent started (id={WorkerId}) — polling {WarchiefUrl}");
        OnStatusChanged?.Invoke(true);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bundle = await PollNextTaskAsync(ct);
                if (bundle is null)
                {
                    await Task.Delay(3_000, ct);
                    continue;
                }

                Log($"🐝 Received [{bundle.Role}] '{bundle.Title}' from Warchief");
                await ClaimAndExecuteAsync(bundle, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"⚠ Worker loop error: {ex.Message}");
                try { await Task.Delay(5_000, ct); } catch { break; }
            }
        }

        Log("🐝 Worker agent stopped.");
        OnStatusChanged?.Invoke(false);
    }

    // ── Poll ─────────────────────────────────────────────────────────────────

    private async Task<HiveTaskBundle?> PollNextTaskAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var lanes = Lanes.Length > 0
            ? string.Join(",", Lanes)
            : "researcher,coder,uideveloper,tester";

        var url  = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/next?lanes={lanes}&workerId={WorkerId}";
        var resp = await http.GetAsync(url, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<HiveTaskBundle>(json, _json);
    }

    // ── Claim → Execute → Complete ────────────────────────────────────────────

    private async Task ClaimAndExecuteAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var claimToken = await ClaimTaskAsync(bundle, ct);
        if (claimToken is null)
        {
            Log($"🐝 [{bundle.Role}] '{bundle.Title}' — already claimed by another worker");
            return;
        }

        var startedAt = DateTime.UtcNow;
        string? result   = null;
        string  status   = "completed";
        string? errorMsg = null;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask      = HeartbeatLoopAsync(bundle, claimToken ?? "", heartbeatCts.Token);

        try
        {
            result = await ExecuteTaskAsync(bundle, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            status   = "failed";
            errorMsg = ex.Message;
            Log($"⚠ [{bundle.Role}] '{bundle.Title}' — execution failed: {ex.Message}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { }
        }

        var taskResult = new HiveTaskResult
        {
            TaskId     = bundle.TaskId,
            WorkerId   = WorkerId,
            WorkerUrl  = WorkerUrl,
            Result     = result ?? "",
            Status     = status,
            ErrorMsg   = errorMsg,
            DurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            ClaimToken = claimToken,
        };

        var action = status == "completed" ? "complete" : "fail";
        await PostResultAsync(bundle.TaskId, action, taskResult, ct);
        TaskActivity(bundle.TaskId,
            status == "completed"
                ? $"✅ Sent to Warchief ({taskResult.DurationMs / 1000.0:F1}s, {result?.Length ?? 0} chars)"
                : $"⚠ Failure reported to Warchief: {errorMsg}");
    }

    private async Task<string?> ClaimTaskAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url  = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{bundle.TaskId}/claim";
        var body = JsonSerializer.Serialize(new HiveClaimRequest
        {
            WorkerId  = WorkerId,
            WorkerUrl = WorkerUrl,
            Lanes     = Lanes,
        }, _json);

        var resp = await http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode) return null;

        // Extract claimToken from response so we can prove we own this task on /complete.
        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claimToken", out var tok))
                return tok.GetString();
        }
        catch { }
        return "";   // claimed but no token in response — allow (backward-compat)
    }

    private async Task HeartbeatLoopAsync(HiveTaskBundle bundle, string claimToken, CancellationToken ct)
    {
        var hbBody = JsonSerializer.Serialize(new HiveHeartbeatRequest
        {
            WorkerId   = WorkerId,
            ClaimToken = claimToken,
        }, _json);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(10_000, ct); } catch { break; }
            if (ct.IsCancellationRequested) break;

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var url = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{bundle.TaskId}/heartbeat";
                await http.PostAsync(url, new StringContent(hbBody, Encoding.UTF8, "application/json"), ct);
            }
            catch { /* non-fatal — watchdog will re-queue if too many misses */ }
        }
    }

    private async Task PostResultAsync(string taskId, string action, HiveTaskResult result, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var url  = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{taskId}/{action}";
        var body = JsonSerializer.Serialize(result, _json);
        await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
    }

    // ── Task execution (single-pass LLM call) ─────────────────────────────────

    private async Task<string> ExecuteTaskAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        if (Ollama is null)
            throw new InvalidOperationException("Worker: OllamaClient not configured");

        // Choose model: use hint from Warchief, fall back to local config
        var model = bundle.Role.ToLower() == "researcher"
            ? (string.IsNullOrWhiteSpace(ResearcherModel) ? CoderModel : ResearcherModel)
            : CoderModel;

        if (string.IsNullOrWhiteSpace(model))
            model = bundle.ModelHint;

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Worker: no model configured — set CoderModel in HIVE settings");

        var sysPrompt = BuildSystemPrompt(bundle.Role);
        var userMsg   = BuildUserMessage(bundle);

        var messages = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = sysPrompt,  Status = MessageStatus.Complete },
            new() { Role = MessageRole.User,   Content = userMsg,    Status = MessageStatus.Complete },
        };

        Log($"🐝 Executing [{bundle.Role}] '{bundle.Title}' with {model}…");
        TaskActivity(bundle.TaskId, $"⚡ Running on {WorkerId} with {model}");

        var result = new StringBuilder();
        await foreach (var token in Ollama.StreamCompletionAsync(model, messages, ct: ct))
            result.Append(token);

        Log($"🐝 [{bundle.Role}] '{bundle.Title}' — done ({result.Length} chars)");
        return result.ToString();
    }

    private static string BuildSystemPrompt(string role) => role.ToLower() switch
    {
        "researcher" => """
            You are a RESEARCHER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Your job: investigate, discover, and document. Do NOT write final production code.
            Return: relevant library names and versions, key API methods with example usage,
            specific recommendations for the coder, known gotchas and constraints.
            Be thorough — the coder depends entirely on your findings.
            Format your output clearly with headers and bullet points.
            """,

        "coder" or "uideveloper" => """
            You are a CODER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Write production-ready, complete implementation code.
            Use the research findings provided to inform all technology choices.

            Output complete files using this EXACT format:
            ### FILE: path/to/file.ext
            ```language
            (complete file contents here)
            ```

            Include proper error handling. Write ONLY in the language specified in the task.
            Do NOT write snippets — write complete, runnable files.
            """,

        "tester" => """
            You are a TESTER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Review the provided code and deliverable against the task requirements.
            Write a structured test verdict:
              PASS: code is correct, complete, and meets requirements.
              FAIL: identify specific issues, what was expected vs. actual.
            Be precise and actionable — your verdict drives the next iteration.
            """,

        _ => $"You are a specialist agent in TheOrc HIVE MIND. Complete the assigned task thoroughly.",
    };

    private static string BuildUserMessage(HiveTaskBundle bundle)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(bundle.TargetLanguage))
            sb.AppendLine($"⚠ LANGUAGE LOCK: ALL code MUST be written in {bundle.TargetLanguage}. " +
                          $"Do NOT use any other language unless the task explicitly requires it.\n");

        if (bundle.UpstreamArtifacts.Count > 0)
        {
            sb.AppendLine("## Research findings from the Researcher:");
            foreach (var artifact in bundle.UpstreamArtifacts)
            {
                sb.AppendLine($"### {artifact.Source}");
                sb.AppendLine(artifact.Content);
                sb.AppendLine();
            }
            sb.AppendLine("---\n");
        }

        sb.AppendLine("## Your task:");
        sb.AppendLine(bundle.Spec);

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Log(string msg)          => OnLog?.Invoke(msg);
    private void TaskActivity(string id, string msg) => OnTaskActivity?.Invoke(id, msg);

    public void Dispose() => Stop();
}
