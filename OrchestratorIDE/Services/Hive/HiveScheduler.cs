using OrchestratorIDE.Agents;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND H2 — assigns swarm tasks to the best available hive node.
///
/// Assignment strategy:
///   1. Boss always runs on the local node (highest model quality needed).
///   2. Researcher tasks go to the first alive node that has the researcher model,
///      falling back to the node with the most free VRAM.
///   3. Coder / UIDev / Tester tasks are round-robined across all alive nodes
///      (including "This PC"), prioritising nodes with more free VRAM so large
///      models land on capable hardware.
///
/// When only "This PC" is alive the method is a no-op — all tasks keep their
/// null TargetNodeUrl and the session routes everything through _ollama as before.
/// </summary>
public static class HiveScheduler
{
    /// <summary>
    /// Sets <see cref="SwarmTask.TargetNodeUrl"/> and <see cref="SwarmTask.TargetNodeName"/>
    /// on each task. Tasks that already have a TargetNodeUrl are left untouched.
    /// </summary>
    public static void AssignNodes(
        IReadOnlyList<SwarmTask> tasks,
        IReadOnlyList<HiveHost>  hosts,
        string                   localUrl)
    {
        // Only remote nodes participate in routing (local node is always available).
        var remote = hosts
            .Where(h => h.Reachable == true && h.Name != "This PC")
            .OrderByDescending(h => h.VramFreeMb)
            .ToList();

        if (remote.Count == 0) return;   // no alive remote nodes — nothing to route

        // Build the full pool: local first (best model quality), then remotes.
        var local = hosts.FirstOrDefault(h => h.Name == "This PC")
                    ?? new HiveHost { Name = "This PC", Url = localUrl };
        local.Url = localUrl;

        var pool = new List<HiveHost> { local };
        pool.AddRange(remote);

        int remoteIdx = 0;   // round-robin index through remote nodes

        foreach (var task in tasks)
        {
            if (!string.IsNullOrEmpty(task.TargetNodeUrl)) continue;

            // Boss always stays local — it needs the highest-capability model.
            // (Boss tasks never pass through AssignNodes in normal flow, but guard anyway.)
            // Researcher: prefer the node with the matching researcher model or most VRAM.
            // Coder/UIDev/Tester: round-robin across remote nodes so work spreads.
            HiveHost chosen;
            if (task.Role == SwarmWorkerRole.Researcher && remote.Count > 0)
            {
                // Pick the remote node that has the most free VRAM for research.
                chosen = remote[0];
            }
            else if (remote.Count > 0)
            {
                // Round-robin: each successive task goes to the next remote node.
                // This naturally balances load when nodes have similar hardware.
                chosen = remote[remoteIdx % remote.Count];
                remoteIdx++;
            }
            else
            {
                continue;   // only local — no routing
            }

            task.TargetNodeUrl  = chosen.Url;
            task.TargetNodeName = chosen.Name;
        }
    }

    /// <summary>
    /// Returns the URL of the node most suitable for a specific model name.
    /// Prefers the node that already has the model loaded (has it in its model list).
    /// Falls back to the node with the most free VRAM, then to localUrl.
    /// </summary>
    public static string GetUrlForModel(
        string                  modelName,
        IReadOnlyList<HiveHost> hosts,
        string                  localUrl)
    {
        var baseName = modelName.Split(':')[0];
        var match = hosts
            .Where(h => h.Reachable == true)
            .OrderByDescending(h =>
                h.Models.Any(m => m.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenByDescending(h => h.VramFreeMb)
            .FirstOrDefault();
        return match?.Url ?? localUrl;
    }
}
