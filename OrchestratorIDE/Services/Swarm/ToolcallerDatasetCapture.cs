// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Stages real, organic toolcaller dataset examples captured from live tool-call
/// decisions — TheOrc generating its own Foundry training data as a byproduct of normal
/// use, rather than synthetic-only authoring. Two independent streams, never mixed:
///
/// v0 (StageCallAsync/StageNoToolAsync) — swarm worker decisions from
/// SwarmSession.RunWorkerLoopAsync's tool-execution loop, roles researcher/coder/
/// ui_developer/tester, the 6-tool frozen set (docs/TOOLCALLER_V0_FROZEN_INVENTORY.md).
///
/// v1 (StageChatDecisionAsync) — OrcChat single-agent decisions from
/// ChatEngine.OnToolcallerDecision, role "chat", the wider 16-tool frozen set
/// (docs/TOOLCALLER_V1_FROZEN_INVENTORY.md). Separate schema_version, hash, and staging
/// directory from v0 by construction.
///
/// Both streams capture the same two organic signals per
/// training_pit/TOOLCALLER_CAPTURE_SCHEMA.md:
///   - "call": every real tool proposal, including ask_user (v0 only — ask_user isn't a
///     v1 chat tool). A correct ask_user call IS a "call" decision under the frozen
///     schema, not a separate "clarify" type.
///   - "no_tool": a turn that produces substantive content but proposes no tool call.
///
/// "clarify" and "unsupported" have no organic signal in either loop and are intentionally
/// not captured here — see the Foundry F-1 coverage-strategy decision in
/// docs/TOOLCALLER_V0_FROZEN_INVENTORY.md.
///
/// Captures are staged pending/unreviewed, mirroring DatasetCapture.cs's precedent:
/// mechanical admission gates (Tools/ToolcallerBench), the existing sanitizer
/// (training_pit/scripts/sanitize_dataset.py), and human review remain required before any
/// example reaches a train/eval split — this hook never assigns a split itself. Capture is
/// best-effort: errors are silently swallowed so a capture failure never disrupts the
/// swarm run or chat turn.
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
    /// SHA-256 of training_pit/schemas/toolcaller_v1_frozen_tools.json's raw bytes (see
    /// docs/TOOLCALLER_V1_FROZEN_INVENTORY.md) — the wider, OrcChat-inclusive tool universe.
    /// Deliberately a SEPARATE constant from v0's: v1 captures never share a schema_version,
    /// hash, or staging directory with v0, so nothing can accidentally merge the two streams.
    /// </summary>
    private const string FrozenToolSchemaHashV1 =
        "58a0e50de6cb6d6ae54a6034534026f97af9ea681361bb55e7e1dfacc3ea629a";

    /// <summary>
    /// Opt-in, off by default. Driven by AppSettings.ToolcallerDatasetCaptureEnabled (see
    /// SettingsPanel's "Foundry F-1 dataset capture" toggle), which also drives the status
    /// bar's "Dataset Gathering Active" indicator so capture is never silent. Set directly only
    /// in tests.
    /// </summary>
    public static bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Bump when the opt-in toggle's scope or disclosure copy changes materially (e.g. a new
    /// data category gets captured under the same setting) — lets a future export distinguish
    /// examples captured under an old consent description from a new one.
    /// </summary>
    private const string ConsentSettingVersion = "1";

    private const string PromptVersionV0 = "v0-2026-07"; // export_toolcaller_dataset.py SYSTEM_TEMPLATE
    private const string PromptVersionV1 = "v1-2026-07"; // ChatEngine's live per-turn prompt (no fixed template)

    private static int _sequence;

    /// <summary>
    /// Governance/provenance fields recommended by the 2026-07-12 external release review's
    /// "universal contamination and provenance enforcement" P0 finding. Additive to the existing
    /// `provenance` object rather than replacing it, to avoid a breaking schema change.
    ///
    /// This is the HARD GATE the review asked for, made mechanical rather than a checklist: a
    /// capture whose producing model IS the toolcaller specialist's own deployed tag
    /// (candidate_vs_incumbent = "self_incumbent") means the model's own output would become
    /// positive training truth for its own family — StageCallAsync/StageChatDecisionAsync check
    /// this and REFUSE to write the capture, as defense-in-depth on top of the call-site
    /// exclusion in SwarmSession (which checks ToolcallerService.RepairProvenanceMarker before
    /// ever calling in). Two independent checks so forgetting one at a future call site doesn't
    /// silently reopen the contamination path the review caught.
    ///
    /// NOT yet true, recorded honestly rather than faked: workspace_sensitivity and
    /// redaction_state have no automated classifier behind them yet (sanitize_dataset.py runs
    /// later in the pipeline, not at capture time) — both fields exist so a future classifier
    /// has somewhere to write its verdict, not because classification happens today. This
    /// governance block also only covers the toolcaller v0/v1 streams; DatasetCapture.cs (plan
    /// captures), OllamaReviewService (dataset-judge outputs), and Context Fabric repair data
    /// do not yet carry equivalent fields — see docs/CURRENT_STATE.yaml
    /// (capture_provenance_enforcement) for the tracked scope gap.
    /// </summary>
    private static object BuildGovernance(string producingSubsystem, string producingModel,
        bool isRepairLaneOutput, string promptVersion) => new
    {
        producing_subsystem = producingSubsystem,
        candidate_vs_incumbent = producingModel == ToolcallerService.Model
            ? "self_incumbent"
            : "external",
        human_modified = false, // true only after a reviewer edits the staged file; capture-time is always false
        repair_lineage = isRepairLaneOutput,
        parent_example_ids = Array.Empty<string>(), // organic capture has no synthetic/paraphrase parent yet
        runtime_version = typeof(ToolcallerDatasetCapture).Assembly.GetName().Version?.ToString() ?? "unknown",
        prompt_version = promptVersion,
        workspace_sensitivity = "unclassified",
        redaction_state = "none",
        consent_setting_version = ConsentSettingVersion,
    };

    /// <summary>True when a ToolCall was proposed by the repair lane itself, not the worker/chat model.</summary>
    private static bool IsRepairLaneOutput(ToolCall call) =>
        call.ExplainWhy?.Contains(ToolcallerService.RepairProvenanceMarker) == true;

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
        // Hard gate (defense-in-depth on top of the SwarmSession call-site exclusion): never
        // stage the toolcaller specialist's own repair-lane proposal as if a worker produced it.
        if (IsRepairLaneOutput(call)) return;

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
                governance = BuildGovernance("swarm", model, isRepairLaneOutput: false, PromptVersionV0),
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
                governance = BuildGovernance("swarm", model, isRepairLaneOutput: false, PromptVersionV0),
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

    /// <summary>
    /// Stage one toolcaller-v1 chat decision. Handles both shapes: <paramref name="calls"/>
    /// empty means "no_tool" (one capture written); non-empty means "call" (one capture
    /// written per proposed tool, matching StageCallAsync's one-capture-per-dispatch
    /// convention). Filenamed with a "toolcaller_v1_chat_" prefix into a dedicated staging
    /// directory so v1 examples can never land in or be mistaken for the v0 stream even by
    /// an exporter that doesn't check schema_version.
    /// </summary>
    public static async Task StageChatDecisionAsync(
        string                         runId,
        string                         model,
        string                         request,
        IReadOnlyList<ToolDefinition>  availableTools,
        IReadOnlyList<ToolCall>        calls,
        string?                        workspaceRoot,
        string                         stagingDir)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(request)) return;

        try
        {
            if (calls.Count == 0)
            {
                if (request.Length < 20) return; // mirrors StageNoToolAsync's triviality filter, applied to the request since chat no_tool has no separate "content" field
                var exampleId = NextChatExampleId(runId);
                await WriteChatAsync(BuildChatCapture(
                    exampleId, model, request, availableTools,
                    decision: "no_tool", tool: null, arguments: null, policy: null),
                    exampleId, stagingDir);
                return;
            }

            foreach (var call in calls)
            {
                // Hard gate, same reasoning as StageCallAsync: never stage the specialist's own
                // repair-lane proposal as if the chat model produced it. The repair lane is
                // Swarm-only today, so this branch should be unreachable in practice — kept as
                // defense-in-depth in case OrcChat ever gains repair-lane assistance later.
                if (IsRepairLaneOutput(call)) continue;

                var exampleId = NextChatExampleId(runId);
                var policy = workspaceRoot is not null
                    ? ToolPolicyEngine.Evaluate(call.Name, call.Arguments, workspaceRoot)
                    : null;
                await WriteChatAsync(BuildChatCapture(
                    exampleId, model, request, availableTools,
                    decision: "call", tool: call.Name, arguments: call.Arguments, policy: policy),
                    exampleId, stagingDir);
            }
        }
        catch
        {
            // Best-effort — never propagate capture errors to the caller.
        }
    }

    private static object BuildChatCapture(
        string exampleId, string model, string request,
        IReadOnlyList<ToolDefinition> availableTools,
        string decision, string? tool, Dictionary<string, object?>? arguments,
        Trust.ToolRiskAssessment? policy) => new
    {
        schema_version = "toolcaller-v1",
        tool_schema_hash = FrozenToolSchemaHashV1,
        example_id = exampleId,
        lineage_group_id = exampleId, // organic capture, no paraphrase/repair siblings yet
        captured_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        provenance = new
        {
            source_type = "chat_capture",
            producing_model = model,
            teacher_model = (string?)null,
            prompt_or_recipe_id = (string?)null,
            derived_from_example_id = (string?)null,
        },
        governance = BuildGovernance("orcchat", model, isRepairLaneOutput: false, PromptVersionV1),
        role = "chat",
        request,
        available_tools = availableTools.Select(t => t.Name).ToArray(),
        approval_state = "n/a", // OrcChat has no swarm-style approval gate
        expected = new
        {
            decision,
            tool = (string?)tool,
            arguments = (object?)arguments,
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
            policy_gap_tool = false, // ToolPolicyEngine's known-gap list is v0-tool-specific; not asserted for v1
        },
        review_status = "pending",
        reviewer = (string?)null,
        split = (string?)null,
        notes = "",
        tags = Array.Empty<string>(),
    };

    private static async Task WriteChatAsync(object capture, string exampleId, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var filePath = Path.Combine(stagingDir, $"toolcaller_v1_chat_{exampleId}.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(capture, _jsonOpts));
    }

    private static int _chatSequence;

    private static string NextChatExampleId(string runId) =>
        $"tcv1_{runId}_{Interlocked.Increment(ref _chatSequence):D4}";

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
