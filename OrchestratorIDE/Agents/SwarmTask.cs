using System.ComponentModel;
using System.Runtime.CompilerServices;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Agents;

public enum SwarmTaskStatus { Pending, InProgress, WaitingForUser, Done, Error }
public enum SwarmWorkerRole  { Researcher, Coder, UIDeveloper, Tester }

/// <summary>
/// A single unit of work owned by one swarm worker.
/// Observable so the SwarmBoardPanel can bind directly.
/// </summary>
public class SwarmTask : INotifyPropertyChanged
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string Id       { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public int    Priority { get; set; }  = 2;   // 1 = highest
    public string Title   { get; set; }  = "";
    public string Description { get; set; } = "";
    public SwarmWorkerRole Role { get; set; }

    /// <summary>
    /// The logical role string exactly as emitted by the boss (e.g. "TESTER", "DOCS",
    /// "BACKEND_DEVELOPER"). May differ from Role when a logical role has been normalized
    /// to a supported execution lane by ParseBossPlan's alias map.
    /// Null when the boss emitted a string that exactly matches the execution lane name.
    /// </summary>
    public string? LogicalRole { get; set; }

    // ── Mutable state ─────────────────────────────────────────────────────────
    private SwarmTaskStatus _status = SwarmTaskStatus.Pending;
    private string          _streamBuffer = "";
    private string?         _result;

    public SwarmTaskStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(IsActive)); }
    }

    public string StreamBuffer
    {
        get => _streamBuffer;
        set { _streamBuffer = value; OnPropertyChanged(); }
    }

    public string? Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage      { get; set; }
    /// <summary>Files written directly via write_file tool calls (vs ### FILE: markers).</summary>
    public int     ToolFilesWritten   { get; set; }
    /// <summary>Files extracted from ### FILE: markers in the worker's text output (vs write_file calls).</summary>
    public int     MarkerFilesWritten { get; set; }
    public DateTime  CreatedAt   { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt   { get; set; }
    public DateTime? CompletedAt { get; set; }

    // ── Co-Work state ─────────────────────────────────────────────────────────
    /// <summary>Question the worker is asking the user (set during WaitingForUser).</summary>
    public string? PendingQuestion { get; set; }

    /// <summary>Suggested reply options provided by the worker via ask_user.</summary>
    public List<string> PendingOptions { get; set; } = [];

    /// <summary>
    /// Full conversation history from RunWorkerAsync, preserved after completion
    /// so the user can continue chatting with this worker.
    /// </summary>
    public List<AgentMessage> ConversationHistory { get; set; } = [];

    // ── Display helpers ───────────────────────────────────────────────────────
    public bool   IsActive    => Status is SwarmTaskStatus.InProgress or SwarmTaskStatus.WaitingForUser;

    public string StatusIcon  => Status switch
    {
        SwarmTaskStatus.Pending        => "⏳",
        SwarmTaskStatus.InProgress     => "⚡",
        SwarmTaskStatus.WaitingForUser => "⏸",
        SwarmTaskStatus.Done           => "✓",
        SwarmTaskStatus.Error          => "✗",
        _                              => "?"
    };

    public string StatusColor => Status switch
    {
        SwarmTaskStatus.Pending        => "#666666",
        SwarmTaskStatus.InProgress     => "#76B900",
        SwarmTaskStatus.WaitingForUser => "#F0C060",
        SwarmTaskStatus.Done           => "#4EC94E",
        SwarmTaskStatus.Error          => "#F44747",
        _                              => "#666666"
    };

    public string RoleIcon => Role switch
    {
        SwarmWorkerRole.Researcher  => "🔍",
        SwarmWorkerRole.Coder       => "💻",
        SwarmWorkerRole.UIDeveloper => "🎨",
        SwarmWorkerRole.Tester      => "🧪",
        _                           => "⚡"
    };

    public string RoleLabel => Role switch
    {
        SwarmWorkerRole.Researcher  => "RESEARCHER",
        SwarmWorkerRole.Coder       => "CODER",
        SwarmWorkerRole.UIDeveloper => "UI DEV",
        SwarmWorkerRole.Tester      => "TESTER",
        _                           => "WORKER"
    };

    public string RoleColor => Role switch
    {
        SwarmWorkerRole.Researcher  => "#4A9FD9",
        SwarmWorkerRole.Coder       => "#76B900",
        SwarmWorkerRole.UIDeveloper => "#C586C0",
        SwarmWorkerRole.Tester      => "#F0C060",
        _                           => "#888888"
    };

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
