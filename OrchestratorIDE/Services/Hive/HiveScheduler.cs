// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
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
        string                   localUrl,
        string?                  researcherModel = null,
        string?                  coderModel      = null)
    {
        // Only remote nodes participate in routing (local node is always available).
        var remote = hosts
            .Where(h => h.Reachable == true && h.Name != "This PC")
            .OrderByDescending(h => h.VramFreeMb)
            .ToList();

        if (remote.Count == 0) return;   // no alive remote nodes — nothing to route

        int remoteIdx = 0;   // round-robin index through remote nodes

        foreach (var task in tasks)
        {
            if (!string.IsNullOrEmpty(task.TargetNodeUrl)) continue;

            // For each role prefer a node that already has the right model loaded.
            // Falls back to the node with the most free VRAM, then to the round-robin remote.
            var modelHint = task.Role == SwarmWorkerRole.Researcher ? researcherModel : coderModel;
            HiveHost target;
            if (!string.IsNullOrWhiteSpace(modelHint))
            {
                var bestUrl = GetUrlForModel(modelHint, hosts, localUrl);

                // Best match is local — leave task unassigned so it runs on This PC.
                if (string.Equals(bestUrl, localUrl, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Best match is a specific remote — use it; fallback to highest-VRAM.
                var matched = remote.FirstOrDefault(h =>
                    string.Equals(h.Url, bestUrl, StringComparison.OrdinalIgnoreCase));
                target = matched ?? remote[0];
            }
            else
            {
                // No model hint — round-robin across remotes ordered by VRAM.
                target = remote[remoteIdx % remote.Count];
                remoteIdx++;
            }

            task.TargetNodeUrl  = target.Url;
            task.TargetNodeName = target.Name;
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
