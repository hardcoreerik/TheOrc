// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolcallerBench;

/// <summary>
/// A single tool's frozen schema, as recorded in
/// training_pit/schemas/toolcaller_v0_frozen_tools.json. This is a data mirror of the
/// live ToolDefinition registrations in OrchestratorIDE/Tools/*.cs — see
/// docs/TOOLCALLER_V0_FROZEN_INVENTORY.md for the verification trail and the hash
/// this file's canonical form must reproduce.
/// </summary>
public sealed record FrozenTool(
    string Name,
    string Description,
    IReadOnlyDictionary<string, FrozenToolParameter> Parameters,
    IReadOnlyList<string> Required);

public sealed record FrozenToolParameter(string Type, string Description);

/// <summary>
/// One toolcaller-v0 dataset example, per training_pit/TOOLCALLER_CAPTURE_SCHEMA.md.
/// </summary>
public sealed record ToolcallerCapture(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("tool_schema_hash")] string ToolSchemaHash,
    [property: JsonPropertyName("example_id")] string ExampleId,
    [property: JsonPropertyName("lineage_group_id")] string LineageGroupId,
    [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("provenance")] ToolcallerProvenance Provenance,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("available_tools")] IReadOnlyList<string> AvailableTools,
    [property: JsonPropertyName("approval_state")] string ApprovalState,
    [property: JsonPropertyName("expected")] ToolcallerExpected Expected,
    [property: JsonPropertyName("policy_outcome")] ToolcallerPolicyOutcome? PolicyOutcome,
    [property: JsonPropertyName("review_status")] string ReviewStatus,
    [property: JsonPropertyName("reviewer")] string? Reviewer,
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags);

public sealed record ToolcallerProvenance(
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("producing_model")] string? ProducingModel,
    [property: JsonPropertyName("teacher_model")] string? TeacherModel,
    [property: JsonPropertyName("prompt_or_recipe_id")] string? PromptOrRecipeId,
    [property: JsonPropertyName("derived_from_example_id")] string? DerivedFromExampleId);

public sealed record ToolcallerExpected(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, JsonElement>? Arguments,
    [property: JsonPropertyName("reason_code")] string? ReasonCode);

public sealed record ToolcallerPolicyOutcome(
    [property: JsonPropertyName("evaluated")] bool Evaluated,
    [property: JsonPropertyName("risk_level")] string? RiskLevel,
    [property: JsonPropertyName("is_destructive")] bool IsDestructive,
    [property: JsonPropertyName("touches_outside_workspace")] bool TouchesOutsideWorkspace,
    [property: JsonPropertyName("network_access")] bool NetworkAccess,
    [property: JsonPropertyName("block_reason")] string? BlockReason,
    [property: JsonPropertyName("policy_gap_tool")] bool PolicyGapTool);

public enum FindingSeverity { Error, Info }

/// <summary>One admission-gate violation or informational note found in a single capture.</summary>
public sealed record ValidationFinding(
    string ExampleId,
    string Gate,
    FindingSeverity Severity,
    string Detail);

/// <summary>
/// Result of mechanically validating a set of toolcaller captures against the frozen
/// tool inventory and the admission gates in TOOLCALLER_CAPTURE_SCHEMA.md.
/// </summary>
public sealed record ToolcallerValidationReport(
    string SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string FrozenToolSchemaHash,
    int TotalExamples,
    int PassedExamples,
    int FailedExamples,
    IReadOnlyList<ValidationFinding> Findings)
{
    public bool Passed => FailedExamples == 0 && TotalExamples > 0;
}
