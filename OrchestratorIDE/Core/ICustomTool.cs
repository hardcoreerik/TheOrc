// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core;

/// <summary>
/// Contract that every hot-loaded custom tool must implement.
///
/// Write a class that implements this interface in the Tool Editor panel,
/// hit Compile → Load, and the agent gains the tool immediately.
///
/// The agent sees Name + Description to decide when to call the tool.
/// Parameters describes the JSON schema the agent must pass.
/// ExecuteAsync is called when the agent invokes the tool.
/// </summary>
public interface ICustomTool
{
    /// <summary>Unique tool name — lowercase_with_underscores. Must be stable across reloads.</summary>
    string Name { get; }

    /// <summary>One-sentence description the agent reads to decide when to use this tool.</summary>
    string Description { get; }

    /// <summary>JSON-schema parameters. Key = parameter name, Value = (type, description).</summary>
    Dictionary<string, ToolParameter> Parameters { get; }

    /// <summary>Parameter names the agent must always supply.</summary>
    string[] Required { get; }

    /// <summary>
    /// When true the tool call goes through the approval gate (DiffViewer / ShellApprovalCard)
    /// before executing. Set true for anything that writes files or runs shell commands.
    /// </summary>
    bool RequiresApproval { get; }

    /// <summary>
    /// Execute the tool. Return a plain-text result string the agent reads.
    /// Prefix [OK] on success, [ERROR] on failure.
    /// </summary>
    Task<string> ExecuteAsync(Dictionary<string, object?> args, CancellationToken ct);
}
