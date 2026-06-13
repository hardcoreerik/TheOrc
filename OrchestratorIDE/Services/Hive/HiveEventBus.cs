namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Thread-safe in-memory ring buffer for HIVE task lifecycle events.
/// Ring size 512 — large enough that a 2s-polling UI never falls behind
/// even during a busy session (tasks take at least ~10s each).
/// Consumers call Since(seq) with the last seq they saw; the bus returns
/// only new events, never resending old ones.
/// </summary>
public sealed class HiveEventBus
{
    private const int RingSize = 512;
    private readonly HiveEvent[] _ring = new HiveEvent[RingSize];
    private long _next;
    private readonly object _lock = new();

    public void Append(string type, string msg,
        string taskId = "", string workerId = "", string sessionId = "")
    {
        lock (_lock)
        {
            var seq = _next++;
            _ring[seq % RingSize] = new HiveEvent
            {
                Seq       = seq,
                Ts        = DateTime.UtcNow,
                Type      = type,
                Msg       = msg,
                TaskId    = taskId,
                WorkerId  = workerId,
                SessionId = sessionId,
            };
        }
    }

    /// <summary>
    /// Returns events with Seq > sinceSeq (exclusive), up to 100 at a time.
    /// Pass sinceSeq = -1 to get the tail of the buffer on first connect.
    /// </summary>
    public HiveEvent[] Since(long sinceSeq)
    {
        lock (_lock)
        {
            if (_next == 0) return [];
            var from = Math.Max(sinceSeq + 1, Math.Max(0L, _next - RingSize));
            var result = new List<HiveEvent>();
            for (var seq = from; seq < _next && result.Count < 100; seq++)
            {
                var e = _ring[seq % RingSize];
                if (e is not null && e.Seq == seq)
                    result.Add(e);
            }
            return [.. result];
        }
    }

    /// <summary>Highest seq assigned so far, or -1 if empty.</summary>
    public long HeadSeq { get { lock (_lock) return _next - 1; } }
}
