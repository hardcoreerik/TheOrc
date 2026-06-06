using OrchestratorIDE.Models;

namespace OrchestratorIDE.Trust;

/// <summary>
/// Async approval gate. The agent calls RequestApprovalAsync and awaits the user's decision.
/// The UI completes the TaskCompletionSource when the user clicks Approve/Reject.
/// </summary>
public class ApprovalQueue
{
    private readonly object _lock = new();
    private readonly List<PendingApproval> _queue = [];

    public event Action<PendingApproval>? ApprovalRequested;

    /// <summary>
    /// When true, every approval request is immediately granted without UI interaction.
    /// Used by --autotest mode. Never set this in production.
    /// </summary>
    public bool AutoApprove { get; set; } = false;

    public async Task<bool> RequestApprovalAsync(ToolCall call, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        ct.Register(() => tcs.TrySetCanceled());

        var pending = new PendingApproval(call, tcs);
        lock (_lock) { _queue.Add(pending); }

        if (AutoApprove)
        {
            // Headless mode — approve immediately, no UI needed
            Approve(pending);
        }
        else
        {
            ApprovalRequested?.Invoke(pending);
        }

        try { return await tcs.Task; }
        finally { lock (_lock) { _queue.Remove(pending); } }
    }

    public void Approve(PendingApproval approval)
    {
        approval.Call.Status = ToolCallStatus.Approved;
        approval.Tcs.TrySetResult(true);
    }

    public void Reject(PendingApproval approval)
    {
        approval.Call.Status = ToolCallStatus.Rejected;
        approval.Tcs.TrySetResult(false);
    }

    public IReadOnlyList<PendingApproval> Pending
    {
        get { lock (_lock) return _queue.ToList(); }
    }
}

public class PendingApproval(ToolCall call, TaskCompletionSource<bool> tcs)
{
    public ToolCall Call { get; } = call;
    public TaskCompletionSource<bool> Tcs { get; } = tcs;
    public DateTime RequestedAt { get; } = DateTime.UtcNow;
}
