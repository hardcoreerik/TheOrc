// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

public static class CampaignTemplates
{
    public static CampaignDefinition NativeAiEval(string name, IEnumerable<string> prompts,
        string modelHash, string adapterHash = "")
    {
        var units = prompts.Select((prompt, index) => new WorkUnit
        {
            WorkUnitId = $"eval-{index + 1:00000}",
            Title = $"Native eval case {index + 1}",
            Role = "Worker",
            Spec = prompt,
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            PackId = "theorc.native-ai-eval",
            PackVersion = "1.0.0",
            Requirements = new ResourceRequirements
            {
                NativeModelHash = modelHash,
                NativeAdapterHash = adapterHash,
                RequiredPacks = ["theorc.native-ai-eval@1.0.0"],
            },
            Verification = new VerificationPolicy { Mode = "independent_consensus", RequiredIndependentRuns = 1 },
            TimeoutMs = 900_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = "theorc.native-ai-eval",
            PackVersion = "1.0.0",
            WorkUnits = units,
        };
    }

    public static CampaignDefinition AlienSignalSearch(string name,
        IEnumerable<ArtifactRef> observations, double maxDrift = 4, double snr = 25, bool gpu = false)
    {
        var units = observations.Select((observation, index) => new WorkUnit
        {
            WorkUnitId = $"observation-{index + 1:00000}",
            Title = $"Technosignature search: {observation.Name}",
            Role = "Compute",
            ExecutionKind = HiveExecutionKinds.ContainerPack,
            PackId = "theorc.alien-signal-search",
            PackVersion = "1.0.0",
            Requirements = new ResourceRequirements
            {
                MinCpuCores = 1,
                MinMemoryMb = 2048,
                MinVramMb = gpu ? 2048 : 0,
                RequiredPacks = ["theorc.alien-signal-search@1.0.0"],
            },
            Verification = new VerificationPolicy
            {
                Mode = "independent_rerun",
                RequiredIndependentRuns = 2,
                RequireDifferentNode = true,
            },
            Inputs = [observation],
            Parameters = new Dictionary<string, JsonElement>
            {
                ["max-drift"] = JsonSerializer.SerializeToElement(maxDrift),
                ["snr"] = JsonSerializer.SerializeToElement(snr),
                ["gpu"] = JsonSerializer.SerializeToElement(gpu),
            },
            TimeoutMs = 7_200_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = "theorc.alien-signal-search",
            PackVersion = "1.0.0",
            WorkUnits = units,
        };
    }
}
