// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Trust;

/// <summary>
/// Controls how aggressively TheOrc auto-approves tool calls.
/// Mirrors Claude Code's permission tiers so the mental model is familiar.
///
///   Plan      → agent reads and reasons only; write/shell tools blocked
///   Guarded   → every write + shell needs explicit user approval (default)
///   Standard  → file writes auto-approved; shell still requires approval
///   FullAuto  → everything auto-approved — no prompts (dangerous mode)
/// </summary>
public enum TrustLevel
{
    Plan     = 0,
    Guarded  = 1,
    Standard = 2,
    FullAuto = 3,
}

public static class TrustLevelInfo
{
    public static string Icon(TrustLevel t) => t switch
    {
        TrustLevel.Plan     => "📋",
        TrustLevel.Guarded  => "🛡",
        TrustLevel.Standard => "✅",
        TrustLevel.FullAuto => "⚡",
        _                   => "🛡",
    };

    public static string Label(TrustLevel t) => t switch
    {
        TrustLevel.Plan     => "Plan",
        TrustLevel.Guarded  => "Guarded",
        TrustLevel.Standard => "Standard",
        TrustLevel.FullAuto => "Full Auto",
        _                   => "Guarded",
    };

    public static string Tooltip(TrustLevel t) => t switch
    {
        TrustLevel.Plan     => "Plan mode — agent reads and reasons only. No files written, no shell commands run.",
        TrustLevel.Guarded  => "Guarded — every file write and shell command requires your explicit approval.",
        TrustLevel.Standard => "Standard — file writes are auto-approved; shell commands still require approval.",
        TrustLevel.FullAuto => "Full Auto — all tools run without prompts. Use with care.",
        _                   => "",
    };

    /// <summary>
    /// Accent colour for the active trust level chip in the status bar.
    /// </summary>
    public static string ActiveColor(TrustLevel t) => t switch
    {
        TrustLevel.Plan     => "#4A9FD9",   // blue  — read-only / safe
        TrustLevel.Guarded  => "#76B900",   // green — default / cautious
        TrustLevel.Standard => "#CCA700",   // amber — relaxed
        TrustLevel.FullAuto => "#F44747",   // red   — dangerous
        _                   => "#76B900",
    };

    /// <summary>
    /// Returns true when the given tool name should be auto-approved at
    /// Standard trust level (file writes only — shell still prompts).
    /// </summary>
    public static bool IsFileWriteTool(string toolName) =>
        toolName is "write_file" or "edit_file" or "patch_file" or "create_file";
}
