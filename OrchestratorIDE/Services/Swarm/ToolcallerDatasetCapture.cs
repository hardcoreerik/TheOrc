// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Stages real, organic toolcaller-v0 dataset examples captured from live swarm tool-call
/// decisions — TheOrc generating its own Foundry F-1 training data as a byproduct of normal
/// use, rather than synthetic-only authoring.
///
/// Two organic signals are captured, per training_pit/TOOLCALLER_CAPTURE_SCHEMA.md:
///   - "call": every real tool dispatch through RunWorkerLoopAsync's tool-execution loop,
///     including ask_user. A correct ask_user call IS a "call" decision under the frozen
///     schema, not a separate "clarify" type — ask_user is one of the six frozen v0 tools.
///   - "no_tool": a worker turn that produces substantive content but proposes no tool call.
///
/// "clarify" (beyond ask_user) and "unsupported" have no organic signal in the current
/// worker loop and are intentionally not captured here — see the Foundry F-1
/// coverage-strategy decision in docs/TOOLCALLER_V0_FROZEN_INVENTORY.md.
///
/// Captures are staged pending/unreviewed, mirroring DatasetCapture.cs's precedent:
/// mechanical admission gates (Tools/ToolcallerBench), the existing sanitizer
/// (training_pit/scripts/sanitize_dataset.py), and human review remain required before any
/// example reaches a train/eval split — this hook never assigns a split itself. Capture is
/// best-effort: errors are silently swallowed so a capture failure never disrupts the
/// swarm run.
/// </summary>
public static class ToolcallerDatasetCapture
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// SHA-256 of training_pit/schemas/toolcaller_v0_frozen_tools.json's raw bytes
    /// (see docs/TOOLCALLER_V0_FROZEN_INVENTORY.md). Update both if that file's tool
    /// set ever changes — stale-hash captures are rejected by Tools/ToolcallerBench.
    /// </summary>
    private const string FrozenToolSchemaHash =
        "c456ca416882788664b14ea332aa968de76735171a2e53a76eac7c4c6e2bfefd";

    /// <summary>
    /// Opt-in, off by default. Driven by AppSettings.ToolcallerDatasetCaptureEnabled (see
    /// SettingsPanel's "Foundry F-1 dataset capture" toggle), which also drives the status
    /// bar's "Dataset Gathering Active" indicator so capture is never silent. Set directly only
    /// in tests.
    /// </summary>
    public static bool IsEnabled { get; set; } = false;

    private static int _sequence;

    /// <summary>
    /// Stage a "call" example: the worker proposed exactly this tool with these arguments.
    /// Called once per dispatched tool call from RunWorkerLoopAsync's tool-execution loop.
    /// </summary>
    public static async Task StageCallAsync(
        string                        runId,
        SwarmTask                     task,
        string                        model,
        ToolCall                      call,
        IReadOnlyList<ToolDefinition> availableTools,
        string?                       workspaceRoot,
        string                        stagingDir)
    {
        if (!IsEnabled) return;

        try
        {
            var exampleId = NextExampleId(runId);
            var policy = workspaceRoot is not null
                ? ToolPolicyEngine.Evaluate(call.Name, call.Arguments, workspaceRoot)
                : null;

            var capture = new
            {
                schema_version = "toolcaller-v0",
                tool_schema_hash = FrozenToolSchemaHash,
                example_id = exampleId,
                lineage_group_id = exampleId, // organic capture, no paraphrase/repair siblings yet
                captured_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                provenance = new
                {
                    source_type = "swarm_capture",
                    producing_model = model,
                    teacher_model = (string?)null,
                    prompt_or_recipe_id = (string?)null,
                    derived_from_example_id = (string?)null,
                },
                role = RoleToken(task.Role),
                request = RequestText(task),
                available_tools = availableTools.Select(t => t.Name).ToArray(),
                approval_state = "approved", // swarm workers always run in auto-approve mode today
                expected = new
                {
                    decision = "call",
                    tool = call.Name,
                    arguments = call.Arguments,
                    reason_code = (string?)null,
                },
                policy_outcome = policy is null ? null : new
                {
                    evaluated = true,
                    risk_level = JsonNamingPolicy.SnakeCaseLower.ConvertName(policy.Risk.ToString()),
                    is_destructive = policy.IsDestructive,
                    touches_outside_workspace = policy.TouchesOutsideWorkspace,
                    network_access = policy.NetworkAccess,
                    block_reason = policy.BlockReason,
                    policy_gap_tool = call.Name is "grep_code" or "ask_user",
                },
                review_status = "pending",
                reviewer = (string?)null,
                split = (string?)null, // assigned during review, never at capture time
                notes = "",
                tags = Array.Empty<string>(),
            };

            await WriteAsync(capture, exampleId, stagingDir);
        }
        catch
        {
            // Best-effort — never propagate capture errors to the caller.
        }
    }

    /// <summary>
    /// Stage a "no_tool" example: the worker produced a substantive response without
    /// proposing any tool call. Skips trivial/near-empty completions.
    /// </summary>
    public static async Task StageNoToolAsync(
        string                        runId,
        SwarmTask                     task,
        string                        model,
        string                        content,
        IReadOnlyList<ToolDefinition> availableTools,
        string                        stagingDir)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(content) || content.Length < 20) return;

        try
        {
            var exampleId = NextExampleId(runId);

            var capture = new
            {
                schema_version = "toolcaller-v0",
                tool_schema_hash = FrozenToolSchemaHash,
                example_id = exampleId,
                lineage_group_id = exampleId,
                captured_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                provenance = new
                {
                    source_type = "swarm_capture",
                    producing_model = model,
                    teacher_model = (string?)null,
                    prompt_or_recipe_id = (string?)null,
                    derived_from_example_id = (string?)null,
                },
                role = RoleToken(task.Role),
                request = RequestText(task),
                available_tools = availableTools.Select(t => t.Name).ToArray(),
                approval_state = "approved",
                expected = new
                {
                    decision = "no_tool",
                    tool = (string?)null,
                    arguments = (object?)null,
                    reason_code = (string?)null,
                },
                policy_outcome = (object?)null, // no call proposed, nothing to evaluate
                review_status = "pending",
                reviewer = (string?)null,
                split = (string?)null,
                notes = "",
                tags = Array.Empty<string>(),
            };

            await WriteAsync(capture, exampleId, stagingDir);
        }
        catch
        {
            // Best-effort — never propagate capture errors to the caller.
        }
    }

    private static async Task WriteAsync(object capture, string exampleId, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var filePath = Path.Combine(stagingDir, $"toolcaller_capture_{exampleId}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(capture, _jsonOpts));
    }

    private static string NextExampleId(string runId) =>
        $"tc_{runId}_{Interlocked.Increment(ref _sequence):D4}";

    private static string RequestText(SwarmTask task) =>
        string.IsNullOrWhiteSpace(task.Description) ? task.Title : task.Description;

    private static string RoleToken(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => "researcher",
        SwarmWorkerRole.Coder       => "coder",
        SwarmWorkerRole.UIDeveloper => "ui_developer",
        SwarmWorkerRole.Tester      => "tester",
        _                           => "unknown",
    };
}
