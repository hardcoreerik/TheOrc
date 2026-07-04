// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace ToolcallerBench;

/// <summary>
/// Implements the mechanical dataset admission gates from
/// training_pit/TOOLCALLER_CAPTURE_SCHEMA.md. This runs before any model-based judge,
/// per FOUNDRY_ARENA.md's general policy.
///
/// One gate from the schema doc — "approval_state implying the call already executed
/// or was already approved by the model itself" — is NOT mechanically checked here.
/// It requires semantic judgment about free-text request/notes content that a keyword
/// heuristic would either miss or false-positive on; building a fragile approximation
/// and reporting it as "checked" would misrepresent this validator's real coverage.
/// It remains a reviewer-only gate until a real approach is chosen (see the "Reviewer
/// Coverage" note in ToolcallerValidationReport output).
///
/// The other schema-doc gate this validator does NOT check — live cross-verification
/// of policy_outcome against a fresh OrchestratorIDE.Trust.ToolPolicyEngine.Evaluate()
/// call — is intentionally out of scope for this skeleton. ToolPolicyEngine.cs is only
/// compiled into OrchestratorIDE.Avalonia.csproj today; referencing it from this bench
/// tool would pull in the full Avalonia UI stack for a validator that doesn't need it.
/// This validator instead checks policy_outcome for internal self-consistency (e.g.
/// "evaluated" must be true whenever decision is "call") and leaves live cross-checking
/// as an explicit open decision for whoever builds the baseline-generation phase: either
/// extract ToolPolicyEngine into a shared library, or run the cross-check from inside
/// the main app instead of this standalone tool.
/// </summary>
public static class ToolcallerCaptureValidator
{
    public static ToolcallerValidationReport Validate(
        IReadOnlyList<ToolcallerCapture> captures,
        IReadOnlyList<FrozenTool> frozenTools,
        string frozenToolSchemaHash)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(frozenTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenToolSchemaHash);

        var toolsByName = frozenTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var findings = new List<ValidationFinding>();
        var failedIds = new HashSet<string>(StringComparer.Ordinal);

        void Fail(ToolcallerCapture capture, string gate, string detail)
        {
            findings.Add(new ValidationFinding(capture.ExampleId, gate, FindingSeverity.Error, detail));
            failedIds.Add(capture.ExampleId);
        }

        void Info(ToolcallerCapture capture, string gate, string detail) =>
            findings.Add(new ValidationFinding(capture.ExampleId, gate, FindingSeverity.Info, detail));

        foreach (var capture in captures)
        {
            // Gate: stale schema hash — example was generated against a since-changed
            // tool inventory and must be regenerated or explicitly re-validated.
            if (!string.Equals(capture.ToolSchemaHash, frozenToolSchemaHash, StringComparison.Ordinal))
            {
                Fail(capture, "stale_tool_schema_hash",
                    $"Capture references hash '{capture.ToolSchemaHash}' but the frozen inventory is " +
                    $"'{frozenToolSchemaHash}'.");
            }

            // Gate: reason_code required for clarify/unsupported.
            var needsReasonCode = capture.Expected.Decision is "clarify" or "unsupported";
            if (needsReasonCode && string.IsNullOrWhiteSpace(capture.Expected.ReasonCode))
            {
                Fail(capture, "missing_reason_code",
                    $"Decision '{capture.Expected.Decision}' requires a non-null reason_code.");
            }

            if (capture.Expected.Decision == "call")
            {
                // Gate: call examples must name a tool.
                if (string.IsNullOrWhiteSpace(capture.Expected.Tool))
                {
                    Fail(capture, "call_missing_tool", "Decision 'call' requires expected.tool.");
                }
                else
                {
                    // Gate: target tool must exist in the frozen universe.
                    if (!toolsByName.TryGetValue(capture.Expected.Tool, out var tool))
                    {
                        Fail(capture, "tool_outside_frozen_universe",
                            $"expected.tool '{capture.Expected.Tool}' is not in the frozen v0 tool set.");
                    }
                    else
                    {
                        // Gate: target tool must be in this example's own available_tools.
                        if (!capture.AvailableTools.Contains(capture.Expected.Tool, StringComparer.Ordinal))
                        {
                            Fail(capture, "tool_outside_available_tools",
                                $"expected.tool '{capture.Expected.Tool}' is not in this example's available_tools.");
                        }

                        // Gate: no invented arguments, no missing required arguments.
                        var arguments = capture.Expected.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>();
                        var invented = arguments.Keys.Where(k => !tool.Parameters.ContainsKey(k)).ToArray();
                        if (invented.Length > 0)
                        {
                            Fail(capture, "invented_argument",
                                $"Argument(s) not in {tool.Name}'s frozen schema: {string.Join(", ", invented)}.");
                        }

                        var missingRequired = tool.Required.Where(r => !arguments.ContainsKey(r)).ToArray();
                        if (missingRequired.Length > 0)
                        {
                            Fail(capture, "missing_required_argument",
                                $"{tool.Name} requires argument(s) not present: {string.Join(", ", missingRequired)}.");
                        }
                    }
                }

                // Gate: a proposed call must have policy_outcome evaluated.
                if (capture.PolicyOutcome is null || !capture.PolicyOutcome.Evaluated)
                {
                    Fail(capture, "call_missing_policy_outcome",
                        "Decision 'call' requires policy_outcome.evaluated == true.");
                }
            }
            else
            {
                // Non-call decisions should not carry an evaluated policy outcome —
                // there is no proposed call to evaluate against ToolPolicyEngine.
                if (capture.PolicyOutcome is { Evaluated: true })
                {
                    Info(capture, "policy_outcome_evaluated_without_call",
                        $"Decision '{capture.Expected.Decision}' has policy_outcome.evaluated == true; " +
                        "expected only for 'call' decisions.");
                }
            }

            // Note (not a failure): flag examples touching the two tools ToolPolicyEngine
            // does not actively evaluate, per docs/TOOLCALLER_V0_FROZEN_INVENTORY.md.
            if (capture.Expected.Tool is "grep_code" or "ask_user")
            {
                if (capture.PolicyOutcome is { PolicyGapTool: false })
                {
                    Info(capture, "policy_gap_tool_flag_mismatch",
                        $"expected.tool '{capture.Expected.Tool}' has no dedicated ToolPolicyEngine case; " +
                        "policy_outcome.policy_gap_tool should be true.");
                }
            }
        }

        // Gate: every member of a lineage_group_id must share the same split.
        foreach (var group in captures.GroupBy(c => c.LineageGroupId))
        {
            var splits = group.Select(c => c.Split).Distinct(StringComparer.Ordinal).ToArray();
            if (splits.Length > 1)
            {
                foreach (var capture in group)
                {
                    Fail(capture, "lineage_group_split_conflict",
                        $"lineage_group_id '{group.Key}' spans splits: {string.Join(", ", splits)}.");
                }
            }
        }

        var total = captures.Count;
        var failed = failedIds.Count;
        return new ToolcallerValidationReport(
            SchemaVersion: "toolcaller-v0",
            GeneratedUtc: DateTimeOffset.UtcNow,
            FrozenToolSchemaHash: frozenToolSchemaHash,
            TotalExamples: total,
            PassedExamples: total - failed,
            FailedExamples: failed,
            Findings: findings);
    }
}
