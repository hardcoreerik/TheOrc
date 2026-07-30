// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core;

/// <summary>
/// What the native runtime can actually do right now — distinct from what a tool family
/// declares it supports in principle. A tool whose <see cref="ToolDefinition.RequiredCapability"/>
/// isn't currently available is excluded, with an explicit reason
/// (<see cref="NativeToolCapabilities.Reason"/>) rather than a silent omission
/// (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §2.1 Phase 0 exit criteria). As of this Phase 0 landing,
/// <see cref="ToolRegistry"/> (the interactive surface) consults this via
/// <see cref="Has"/> in both <c>GetForProfile</c> and <c>ExecuteAsync</c> -- the headless
/// (Warband/HIVE worker) tool-list construction does not yet, since no capability-gated headless
/// tool exists before Phase 1b adds the first one (browser automation). This class is designed to
/// be the single shared query point both surfaces consult once that lands, not a claim that both
/// already do.
/// </summary>
[Flags]
public enum NativeToolCapability
{
    None              = 0,
    BrowserAutomation = 1 << 0,
    ImageInput        = 1 << 1,
    Ocr               = 1 << 2,
    ShellExecution    = 1 << 3,
    ArtifactExport    = 1 << 4,
}

/// <summary>
/// Process-wide snapshot of which <see cref="NativeToolCapability"/> flags are currently
/// available, queried the same way by the interactive surface (<see cref="ToolRegistry"/>) and
/// the headless surface (Warband/HIVE worker tool-list construction) — the Function Pack Plan's
/// own Phase 0 exit criterion. Deliberately NOT a settings-only toggle: a capability being
/// "enabled" in configuration and a capability being genuinely usable right now (e.g. Playwright
/// browsers actually installed) are different questions, and only the second one should gate
/// tool availability.
/// </summary>
public static class NativeToolCapabilities
{
    private static NativeToolCapability _current = NativeToolCapability.None;
    private static readonly Dictionary<NativeToolCapability, string> _unavailableReasons = [];
    private static readonly Lock _lock = new();

    public static NativeToolCapability Current
    {
        get { lock (_lock) return _current; }
    }

    public static bool Has(NativeToolCapability capability) =>
        capability != NativeToolCapability.None && (Current & capability) == capability;

    /// <summary>
    /// Marks a capability as available. Clears any previously recorded unavailable-reason for it.
    /// </summary>
    public static void MarkAvailable(NativeToolCapability capability)
    {
        lock (_lock)
        {
            _current |= capability;
            _unavailableReasons.Remove(capability);
        }
    }

    /// <summary>
    /// Marks a capability as unavailable, with a human-readable reason surfaced by
    /// <see cref="Reason"/> — e.g. "Playwright browsers are not installed; run `playwright
    /// install` and restart." Never silently omit the reason: an unsupported request must fail
    /// explicitly (Phase 0 exit criterion).
    /// </summary>
    public static void MarkUnavailable(NativeToolCapability capability, string reason)
    {
        lock (_lock)
        {
            _current &= ~capability;
            _unavailableReasons[capability] = reason;
        }
    }

    /// <summary>The recorded reason a capability is unavailable, or null if it's available or was
    /// never explicitly marked unavailable (in which case it simply was never enabled).</summary>
    public static string? Reason(NativeToolCapability capability)
    {
        lock (_lock) return _unavailableReasons.GetValueOrDefault(capability);
    }

    /// <summary>Test/reset seam: restores the default (no capabilities available) state. Not
    /// exposed outside this assembly + friends -- production code should only ever call
    /// MarkAvailable/MarkUnavailable to reflect real detection results, never blanket-reset.</summary>
    internal static void ResetForTest()
    {
        lock (_lock)
        {
            _current = NativeToolCapability.None;
            _unavailableReasons.Clear();
        }
    }
}
