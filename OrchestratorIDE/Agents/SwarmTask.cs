using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrchestratorIDE.Agents;

public enum SwarmTaskStatus { Pending, InProgress, Done, Error }
public enum SwarmWorkerRole  { Researcher, Coder, UIDeveloper }

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

    public string? ErrorMessage { get; set; }
    public DateTime  CreatedAt   { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt   { get; set; }
    public DateTime? CompletedAt { get; set; }

    // ── Display helpers ───────────────────────────────────────────────────────
    public bool   IsActive    => Status == SwarmTaskStatus.InProgress;

    public string StatusIcon  => Status switch
    {
        SwarmTaskStatus.Pending    => "⏳",
        SwarmTaskStatus.InProgress => "⚡",
        SwarmTaskStatus.Done       => "✓",
        SwarmTaskStatus.Error      => "✗",
        _                          => "?"
    };

    public string StatusColor => Status switch
    {
        SwarmTaskStatus.Pending    => "#666666",
        SwarmTaskStatus.InProgress => "#76B900",
        SwarmTaskStatus.Done       => "#4EC94E",
        SwarmTaskStatus.Error      => "#F44747",
        _                          => "#666666"
    };

    public string RoleIcon => Role switch
    {
        SwarmWorkerRole.Researcher  => "🔍",
        SwarmWorkerRole.Coder       => "💻",
        SwarmWorkerRole.UIDeveloper => "🎨",
        _                           => "⚡"
    };

    public string RoleLabel => Role switch
    {
        SwarmWorkerRole.Researcher  => "RESEARCHER",
        SwarmWorkerRole.Coder       => "CODER",
        SwarmWorkerRole.UIDeveloper => "UI DEV",
        _                           => "WORKER"
    };

    public string RoleColor => Role switch
    {
        SwarmWorkerRole.Researcher  => "#4A9FD9",
        SwarmWorkerRole.Coder       => "#76B900",
        SwarmWorkerRole.UIDeveloper => "#C586C0",
        _                           => "#888888"
    };

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
