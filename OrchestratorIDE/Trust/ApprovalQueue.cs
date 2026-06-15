// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Trust;

/// <summary>
/// Async approval gate. The agent calls RequestApprovalAsync and awaits the user's decision.
/// The UI completes the TaskCompletionSource when the user clicks Approve/Reject.
///
/// Behaviour is governed by <see cref="Level"/>:
///   Plan     — all side-effecting tool calls are rejected (agent is read-only)
///   Guarded  — every write + shell surfaces a UI prompt  (default)
///   Standard — file writes auto-approved; shell still prompts
///   FullAuto — everything auto-approved, no UI shown
/// </summary>
public class ApprovalQueue
{
    private readonly object               _lock  = new();
    private readonly List<PendingApproval> _queue = [];

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised (on an arbitrary thread) whenever a pending item needs UI.</summary>
    public event Action<PendingApproval>? ApprovalRequested;

    /// <summary>Raised whenever the pending count changes (item added or resolved).
    /// Dispatch to UI thread before touching WPF elements.</summary>
    public event Action? PendingCountChanged;

    // ── Trust level ───────────────────────────────────────────────────────────

    /// <summary>Active trust level — changing this takes effect on the next tool call.</summary>
    public TrustLevel Level { get; set; } = TrustLevel.Guarded;

    // ── Legacy test-mode flag (never set in production UI) ───────────────────

    /// <summary>
    /// When true every request is immediately granted without UI interaction.
    /// Used only by --autotest / FlaUI headless tests.
    /// </summary>
    public bool AutoApprove { get; set; } = false;

    // ── Core gate ─────────────────────────────────────────────────────────────

    public async Task<bool> RequestApprovalAsync(ToolCall call, CancellationToken ct)
    {
        var tcs     = new TaskCompletionSource<bool>();
        ct.Register(() => tcs.TrySetCanceled());

        var pending = new PendingApproval(call, tcs);
        lock (_lock) { _queue.Add(pending); }
        PendingCountChanged?.Invoke();

        if (AutoApprove)
        {
            Approve(pending);
        }
        else
        {
            switch (Level)
            {
                case TrustLevel.FullAuto:
                    Approve(pending);
                    break;

                case TrustLevel.Standard:
                    // File writes are trusted; shell and everything else still prompts
                    if (TrustLevelInfo.IsFileWriteTool(call.Name))
                        Approve(pending);
                    else
                        ApprovalRequested?.Invoke(pending);
                    break;

                case TrustLevel.Plan:
                    // Read-only mode — reject side-effecting tools so the agent
                    // describes what it would do rather than doing it.
                    Reject(pending,
                        reason: "[Plan mode] Tool execution is disabled. " +
                                "Describe the steps you would take instead of executing them.");
                    break;

                default: // Guarded — prompt for everything
                    ApprovalRequested?.Invoke(pending);
                    break;
            }
        }

        try   { return await tcs.Task; }
        finally
        {
            lock (_lock) { _queue.Remove(pending); }
            PendingCountChanged?.Invoke();
        }
    }

    // ── Approve / Reject ──────────────────────────────────────────────────────

    public void Approve(PendingApproval approval)
    {
        approval.Call.Status = ToolCallStatus.Approved;
        approval.Tcs.TrySetResult(true);
    }

    public void Reject(PendingApproval approval, string? reason = null)
    {
        approval.Call.Status     = ToolCallStatus.Rejected;
        approval.RejectionReason = reason;
        approval.Tcs.TrySetResult(false);
    }

    // ── Convenience: act on the oldest pending item ───────────────────────────

    public void ApproveFirst()
    {
        PendingApproval? first;
        lock (_lock) first = _queue.FirstOrDefault();
        if (first is not null) Approve(first);
    }

    public void RejectFirst()
    {
        PendingApproval? first;
        lock (_lock) first = _queue.FirstOrDefault();
        if (first is not null) Reject(first);
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<PendingApproval> Pending
    {
        get { lock (_lock) return _queue.ToList(); }
    }

    public int PendingCount { get { lock (_lock) return _queue.Count; } }
}

// ── Model ─────────────────────────────────────────────────────────────────────

public class PendingApproval(ToolCall call, TaskCompletionSource<bool> tcs)
{
    public ToolCall                   Call            { get; } = call;
    public TaskCompletionSource<bool> Tcs             { get; } = tcs;
    public DateTime                   RequestedAt     { get; } = DateTime.UtcNow;
    public string?                    RejectionReason { get; set; }
}
