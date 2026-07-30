// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core;

/// <summary>
/// Typed tool-result shape (docs/NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md Phase 0 / 6;
/// docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §2.1). Additive, not a replacement for the existing
/// plain-string tool-result convention: <see cref="ToolDefinition.Handler"/> and
/// <see cref="OrchestratorIDE.Core.Runtime.HeadlessTool.ExecuteAsync"/> both keep returning
/// <c>Task&lt;string&gt;</c> unchanged, since that's what both text tool-call parsers
/// (<see cref="ToolCallTextParser"/>'s JSON-brace convention and ChatEngine's ReAct-XML
/// convention) and every existing tool family already produce/consume. A tool that needs to
/// return more than text (a screenshot, an exported artifact) returns a
/// <see cref="ToolResult"/>, renders its own <see cref="Summary"/> as the plain-string result
/// (so both parsers keep working unchanged), and separately attaches itself to whatever
/// per-call trace/attestation channel needs the richer payload.
/// </summary>
public abstract record ToolResult(string Summary);

/// <summary>Plain-text result — the default shape every existing tool already produces,
/// expressed as a <see cref="ToolResult"/> so callers that want a uniform type can use one.</summary>
public sealed record TextToolResult(string Summary) : ToolResult(Summary);

/// <summary>A tool result backed by a file on disk (an exported artifact, a downloaded file).
/// <paramref name="ArtifactPath"/> is expected to already be inside whatever sandbox the calling
/// tool enforces (workspace root for the interactive surface, per-task output directory for the
/// headless surface) -- this record does not itself validate that.</summary>
public sealed record ArtifactToolResult(string Summary, string ArtifactPath, string MimeType) : ToolResult(Summary);

/// <summary>A tool result whose primary payload is an image (a browser screenshot, an OCR
/// source image). <paramref name="ImagePath"/> follows the same sandbox expectation as
/// <see cref="ArtifactToolResult.ArtifactPath"/>.</summary>
public sealed record ScreenshotToolResult(string Summary, string ImagePath, int Width, int Height) : ToolResult(Summary);
