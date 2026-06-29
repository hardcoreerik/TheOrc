// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class EvidencePackBuilder(
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository)
{
    private const int BasePromptTokens = 192;

    public FabricEvidencePack Build(FabricQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var included = new List<FabricEvidenceItem>();
        var excluded = new List<FabricEvidenceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var evidenceBudget = plan.MaxPromptTokens - plan.ResponseTokenReserve - BasePromptTokens;
        var usedEvidenceTokens = 0;

        foreach (var item in EnumerateCandidates(plan))
        {
            if (!seen.Add($"{item.Kind}:{item.Id}"))
                continue;

            if (usedEvidenceTokens + item.TokenCount <= evidenceBudget)
            {
                included.Add(item);
                usedEvidenceTokens += item.TokenCount;
            }
            else
            {
                excluded.Add(item);
            }
        }

        return new FabricEvidencePack(
            plan.Query,
            plan.Mode,
            plan.MaxPromptTokens,
            plan.ResponseTokenReserve,
            BasePromptTokens + usedEvidenceTokens,
            included,
            excluded,
            BasePromptTokens + usedEvidenceTokens + plan.ResponseTokenReserve <= plan.MaxPromptTokens,
            plan.TriggeredSourceReopen);
    }

    private IEnumerable<FabricEvidenceItem> EnumerateCandidates(FabricQueryPlan plan)
    {
        foreach (var segment in libraryRepository.GetSegmentsByIds(plan.ReopenedSegmentIds))
        {
            yield return new FabricEvidenceItem(
                "source",
                segment.SegmentId,
                segment.Text,
                EstimatePromptTokensConservatively(segment.Text),
                true,
                $"{segment.DocumentId}:{segment.Ordinal}");
        }

        foreach (var hit in plan.SeedHits)
        {
            yield return new FabricEvidenceItem(
                "source",
                hit.SegmentId,
                hit.Text,
                EstimatePromptTokensConservatively(hit.Text),
                true,
                $"{hit.DocumentId}:{hit.Ordinal}:{hit.RetrievalPath}");
        }

        foreach (var nodeId in plan.SummaryNodeIds)
        {
            var node = graphRepository.GetMemoryNode(nodeId);
            if (node is null)
                continue;

            yield return new FabricEvidenceItem(
                "summary",
                node.NodeId,
                node.SummaryText,
                EstimatePromptTokensConservatively(node.SummaryText),
                false,
                $"{node.DocumentId}:g{node.Generation}:{node.CoverageStatus}");
        }
    }

    private static int EstimatePromptTokensConservatively(string text) =>
        ContextManager.EstimateTokens(text ?? "");
}
