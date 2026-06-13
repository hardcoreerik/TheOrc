using System.Collections.Concurrent;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Maps each file path to the single swarm task that currently owns it.
/// Ownership is claimed before dispatch and released on task completion,
/// so two concurrent tasks can never touch the same file.
/// Thread-safe: all operations are safe for concurrent callers.
/// </summary>
public sealed class FileOwnershipLedger
{
    // normalized-path → taskId
    private readonly ConcurrentDictionary<string, string> _map =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Attempt to claim ownership of <paramref name="files"/> for <paramref name="taskId"/>.
    /// Files that are already owned by a different task are returned as conflicts;
    /// all non-conflicting files are claimed immediately.
    /// The caller (scheduler) uses the conflict list to add sequencing edges
    /// rather than dispatching the task in parallel with its conflict owner.
    /// </summary>
    public IReadOnlyList<OwnershipConflict> TryClaim(string taskId, IEnumerable<string> files)
    {
        var conflicts = new List<OwnershipConflict>();
        foreach (var raw in files)
        {
            var key = Normalize(raw);
            if (!_map.TryAdd(key, taskId))
            {
                if (_map.TryGetValue(key, out var existing) && existing != taskId)
                    conflicts.Add(new OwnershipConflict(key, taskId, existing));
                // Same taskId re-claiming the same file: no-op, no conflict.
            }
        }
        return conflicts;
    }

    /// <summary>
    /// Release all files owned by <paramref name="taskId"/> so sequenced
    /// successor tasks can claim them.
    /// </summary>
    public void Release(string taskId)
    {
        foreach (var key in _map.Keys.ToArray())
            _map.TryRemove(new KeyValuePair<string, string>(key, taskId));
    }

    /// <summary>Who owns this file right now? Returns null if unowned.</summary>
    public string? OwnerOf(string path) =>
        _map.TryGetValue(Normalize(path), out var owner) ? owner : null;

    /// <summary>Full snapshot of the ownership map for the run manifest and UI.</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_map, StringComparer.Ordinal);

    /// <summary>
    /// Case-fold on Windows, forward-slash separators, strip leading/trailing slashes.
    /// Internal so WorktreeManager can reuse the same rule.
    /// </summary>
    internal static string Normalize(string path) =>
        path.Replace('\\', '/').ToLowerInvariant().Trim('/');
}

public sealed record OwnershipConflict(string Path, string RequestedBy, string OwnedBy);
