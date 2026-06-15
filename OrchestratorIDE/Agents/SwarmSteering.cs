// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.Agents;

/// <summary>
/// GOBLIN MIND capability-aware steering decisions, extracted from SwarmSession
/// as pure functions so routing behavior is unit-testable without a live
/// session, Ollama, or the on-disk profile store (the map lookup is injected).
///
/// Behavior contract (guarded by T11_SteeringTests):
///   - An unprobed model is assumed capable — setups without probes keep working.
///   - A primary that fails its role's required categories yields to the fallback,
///     even when the fallback is also deficient (the swarm must still run).
///   - The decision reports exactly which categories failed so the session can
///     surface the warnings verbatim in the activity log.
/// </summary>
internal static class SwarmSteering
{
    /// <summary>Outcome of a routing decision for one task.</summary>
    internal sealed record Decision(
        string   Model,
        bool     UsedFallback,
        string[] PrimaryMissing,
        string[] FallbackMissing);

    /// <summary>Required capability categories for a swarm role.</summary>
    internal static CategoryId[] RequiredCategories(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => SwarmRoleRequirements.Researcher,
        SwarmWorkerRole.Coder       => SwarmRoleRequirements.Worker,
        SwarmWorkerRole.UIDeveloper => SwarmRoleRequirements.Worker,
        SwarmWorkerRole.Tester      => [CategoryId.CodeExec, CategoryId.SystemInspect],
        _                           => SwarmRoleRequirements.Boss,
    };

    /// <summary>
    /// Picks primary or fallback for a role based on the primary's category map.
    /// <paramref name="mapLookup"/> abstracts ToolCallProfileStore.GetCategoryMap.
    /// </summary>
    internal static Decision SelectModel(
        SwarmWorkerRole role,
        string primary,
        string fallback,
        Func<string, CategoryBoundaryMap?> mapLookup)
    {
        var required = RequiredCategories(role);

        var map = mapLookup(primary);
        if (map == null || map.MeetsRoleRequirements(required))
            return new Decision(primary, false, [], []);

        string[] primaryMissing = [.. required
            .Where(c => !map.CanHandle(c))
            .Select(c => c.ToString())];

        var fallbackMap = mapLookup(fallback);
        string[] fallbackMissing = fallbackMap == null ? [] : [.. required
            .Where(c => !fallbackMap.CanHandle(c))
            .Select(c => c.ToString())];

        return new Decision(fallback, true, primaryMissing, fallbackMissing);
    }

    /// <summary>
    /// Activity-log warning when a primary model fails its role requirements.
    /// Wording is part of the steering contract (operators grep for it) —
    /// pinned by T11; SwarmSession must emit it verbatim.
    /// </summary>
    internal static string PrimaryFallbackWarning(
        string primaryShort, string fallbackShort, SwarmWorkerRole role, string[] missing)
        => $"⚠ {primaryShort} missing categories [{string.Join(", ", missing)}] " +
           $"for {role} — falling back to {fallbackShort}";

    /// <summary>Warning when the fallback is also deficient but used anyway.</summary>
    internal static string FallbackDeficientWarning(string fallbackShort, string[] missing)
        => $"⚠ Fallback {fallbackShort} also missing [{string.Join(", ", missing)}] — proceeding anyway";

    /// <summary>
    /// Builds the capability map block injected into the boss decompose prompt.
    /// Researcher line is omitted when it is the same model as the coder.
    /// </summary>
    internal static string BuildCapabilitySummary(
        string bossModel,
        string coderModel,
        string researcherModel,
        Func<string, CategoryBoundaryMap?> mapLookup,
        Func<string, string> shortName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Goblin Capability Map");
        sb.AppendLine("Use this to route tasks to the right goblin. " +
                      "Do NOT assign tasks to goblins that lack required categories.");
        sb.AppendLine();

        void AppendModel(string label, string modelId, CategoryId[] highlight)
        {
            sb.Append($"- **{label}** ({shortName(modelId)}): ");
            var map = mapLookup(modelId);
            if (map == null) { sb.AppendLine("not yet profiled (assume capable)"); return; }

            var cats = highlight
                .Select(c => $"{c} {(map.CanHandle(c) ? "✅" : "⚠")}")
                .ToList();
            sb.AppendLine(string.Join("  ", cats) + $"  [{map.ShortSummary}]");
        }

        AppendModel("Boss/TheOrc", bossModel,  SwarmRoleRequirements.Boss);
        AppendModel("Coder",       coderModel, SwarmRoleRequirements.Worker);
        if (researcherModel != coderModel)
            AppendModel("Researcher", researcherModel, SwarmRoleRequirements.Researcher);

        return sb.ToString();
    }
}
