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
        string frozenToolSchemaHash,
        IReadOnlySet<string>? heldOutToolNames = null)
    {
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(frozenTools);
        ArgumentException.ThrowIfNullOrWhiteSpace(frozenToolSchemaHash);
        heldOutToolNames ??= new HashSet<string>(StringComparer.Ordinal);

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
                    // Gate: a held-out family's tool must never be admissible as a training
                    // capture's target -- checked FIRST, before the embedded-schema fallback
                    // below, because that fallback would otherwise happily accept a capture that
                    // embeds its own schema for a genuinely held-out real tool name (CodeRabbit
                    // review, PR #99). The embedded-schema fallback is only meant to cover
                    // train-pool SYNTHETIC tools (which have no frozen registration at all,
                    // held-out or otherwise) -- it was never meant to let a capture self-certify
                    // a real held-out tool as usable just by attaching a schema for it. This is
                    // an `if/else if/else` chain (not three independent ifs) so a held-out name
                    // is never ALSO processed by the tool-resolution branch below it.
                    if (heldOutToolNames.Contains(capture.Expected.Tool))
                    {
                        Fail(capture, "tool_outside_frozen_universe",
                            $"expected.tool '{capture.Expected.Tool}' belongs to a held_out family -- " +
                            "never admissible as a training capture's target, regardless of any embedded schema.");
                    }
                    else if ((toolsByName.TryGetValue(capture.Expected.Tool, out var frozenTool)
                                ? frozenTool
                                : capture.AvailableToolsSchema?.GetValueOrDefault(capture.Expected.Tool))
                             is not { } tool)
                    {
                        // Gate: target tool must exist in the frozen universe, OR (v2 only) carry
                        // its own embedded schema from available_tools_schema -- train-pool
                        // synthetic tools (synthetic_tool_schemas.py) are procedurally generated
                        // per run and deliberately never persisted in any frozen registry, so a
                        // capture's own embedded schema IS its "live registration" for those.
                        // The frozen registry still wins when the tool IS a real, registered one
                        // (catches registry drift via the tool_schema_hash gate above); embedded
                        // schemas are only a fallback for tools the registry never claims to know.
                        //
                        // Not v0-specific wording -- this same gate now also validates v2
                        // captures against the v2 tool-family registry's trainable subset
                        // (held-out families are never in `frozenTools` in the first place,
                        // so this is also the mechanical enforcement of "a held-out family's
                        // tools must never be admissible as a training capture's target").
                        Fail(capture, "tool_outside_frozen_universe",
                            $"expected.tool '{capture.Expected.Tool}' is not in the frozen tool set and has no " +
                            $"embedded schema in available_tools_schema (schema_version '{capture.SchemaVersion}', " +
                            $"hash '{frozenToolSchemaHash[..12]}...').");
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
        // Was hardcoded "toolcaller-v0" regardless of what was actually validated -- already
        // wrong for the v1 chat stream, and would have been wrong for v2 too. Derive it from
        // the captures themselves: report the single schema_version if the batch is
        // homogeneous (the normal case), or "mixed" with the distinct set if not.
        var distinctVersions = captures.Select(c => c.SchemaVersion).Distinct(StringComparer.Ordinal).ToArray();
        var reportedSchemaVersion = distinctVersions.Length switch
        {
            0 => "(no captures)",
            1 => distinctVersions[0],
            _ => $"mixed ({string.Join(", ", distinctVersions.OrderBy(v => v, StringComparer.Ordinal))})",
        };
        return new ToolcallerValidationReport(
            SchemaVersion: reportedSchemaVersion,
            GeneratedUtc: DateTimeOffset.UtcNow,
            FrozenToolSchemaHash: frozenToolSchemaHash,
            TotalExamples: total,
            PassedExamples: total - failed,
            FailedExamples: failed,
            Findings: findings);
    }
}
