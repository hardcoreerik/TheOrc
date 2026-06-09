using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Agents;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Stages qualifying boss plans as plan-capture JSON files for dataset review.
///
/// Files are written to: {workspaceRoot}/.orc/swarm/dataset-staging/
/// Naming:  plan_capture_good_{runId}_{score:D3}.json  (score ≥ 70)
///          plan_capture_bad_{runId}_{score:D3}.json   (score ≤ 39)
///
/// These are plan captures (PLAN_CAPTURE_SCHEMA.md format), NOT chat-JSONL.
/// To use for training: run training_pit/scripts/convert_plan_captures.py to
/// convert qualifying captures to chat-JSONL, then validate + sanitize.
///
/// Capture is best-effort — errors are silently swallowed so the swarm run
/// is never disrupted by a capture failure.
///
/// See: training_pit/ARCHITECTURE.md — DatasetCapture hook
///      training_pit/PLAN_CAPTURE_SCHEMA.md — file format spec
/// </summary>
public static class DatasetCapture
{
    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    /// <summary>
    /// Set false to disable plan capture without recompiling.
    /// Default: true — all qualifying plans are staged automatically.
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Evaluate and stage a boss plan if it meets the positive or negative threshold.
    /// Marginal plans (40–69) are silently skipped.
    ///
    /// Called from SwarmSession.RunInternalAsync() after Tasks is populated.
    /// </summary>
    public static async Task StageAsync(
        string          runId,
        string          userGoal,
        string          bossRaw,
        List<SwarmTask> tasks,
        string          bossModel,
        string          stagingDir)
    {
        if (!IsEnabled) return;

        try
        {
            var score = EvalRubric.Score(tasks, userGoal);

            // Only stage positive (≥70) or negative (≤39) examples
            // Marginal (40–69) adds noise — skip
            bool isPositive = score.Composite >= EvalRubric.PositiveThreshold;
            bool isNegative = score.Composite <= EvalRubric.NegativeThreshold;
            if (!isPositive && !isNegative) return;

            Directory.CreateDirectory(stagingDir);

            // Parse the boss's raw JSON to store the structured plan.
            // Strip markdown fences first — the boss sometimes wraps output in ```json...```
            // even though the system prompt forbids it, and ParseBossPlan already handles this.
            var rawForParse = StripFences(bossRaw);
            JsonNode? planNode = null;
            try { planNode = JsonNode.Parse(rawForParse); }
            catch { /* store null if raw JSON was unparseable */ }

            var capture = BuildCapture(runId, userGoal, bossModel, tasks, score, planNode);
            var json    = JsonSerializer.Serialize(capture, _jsonOpts);

            var qualifier = isPositive ? "good" : "bad";
            var fileName  = $"plan_capture_{qualifier}_{runId}_{score.Composite:D3}.json";
            await File.WriteAllTextAsync(Path.Combine(stagingDir, fileName), json);
        }
        catch
        {
            // Best-effort — never propagate capture errors to the caller
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Strip markdown code fences (```json ... ``` or ``` ... ```) from a raw string.
    /// Mirrors the fence-stripping logic in SwarmSession.ParseBossPlan().
    /// </summary>
    private static string StripFences(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;

        // Remove opening fence line (e.g. "```json" or "```")
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        trimmed = trimmed[(firstNewline + 1)..];

        // Remove closing fence
        var closingFence = trimmed.LastIndexOf("```");
        if (closingFence >= 0)
            trimmed = trimmed[..closingFence];

        return trimmed.Trim();
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private static object BuildCapture(
        string          runId,
        string          userGoal,
        string          bossModel,
        List<SwarmTask> tasks,
        RubricResult    score,
        JsonNode?       planNode)
    {
        return new
        {
            schema_version = "1.0",
            example_id     = $"ex_{runId}",
            run_id         = runId,
            captured_at    = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),

            source         = "swarm_run",
            boss_model     = bossModel,
            benchmark_id   = (string?)null,

            goal           = userGoal,
            domain         = "general",   // Phase 1: not auto-detected; set manually during review
            difficulty     = 2,           // Phase 1: not auto-detected; set manually during review

            plan           = planNode,    // full boss JSON output (plan + tasks)

            quality_score  = score.Composite,
            rubric_scores  = new
            {
                task_count        = score.TaskCount,
                description_depth = score.DescriptionDepth,
                filename_presence = score.FilenamePresence,
                api_contract      = score.ApiContract,
                domain_accuracy   = score.DomainAccuracy,
                json_validity     = score.JsonValidity
            },
            example_class  = score.ExampleClass,

            failure_mode            = EvalRubric.DetectFailureMode(tasks, score),
            correct_plan_reference  = (string?)null,
            notes                   = "",
            annotator               = "auto",
            tags                    = Array.Empty<string>()
        };
    }
}
