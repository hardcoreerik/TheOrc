// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class EvidencePackBuilder(
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository)
{
    // Covers AnswerSystemPrompt (~190 est. tokens) plus a slice of the unbudgeted userPrompt
    // scaffolding ("Evidence from the corpus:" / "Question:" / etc.), the question text itself,
    // and each packed item's "[SOURCE ...]"/"[SUMMARY ...]" header line -- none of which
    // EnumerateCandidates charges against usedEvidenceTokens below.
    private const int BasePromptTokens = 256;

    // ContextManager.EstimateTokens is a crude chars/4 heuristic shared app-wide for general chat
    // context trimming. For Context Fabric's evidence budget specifically, an under-estimate isn't
    // just imprecise -- it lets more evidence get packed than the model's real tokenizer/context
    // window can hold, which fails hard at native decode time ("NoKvSlot") instead of degrading
    // gracefully (observed live on the real Darwin PDF corpus once genuine evidence started
    // flowing through the CF-5 search fix). This margin is scoped to evidence sizing only -- it
    // does not touch the shared estimator used by ordinary chat context trimming. Kept moderate
    // (not larger) so a single large reopened source segment can still fit under a tightly
    // constrained prompt budget (see EvidencePackBuilder_Respects_8k_Budget_And_Reserves_Response_Tokens).
    private const double EvidenceTokenSafetyMargin = 1.15;

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
        (int)Math.Ceiling(ContextManager.EstimateTokens(text ?? "") * EvidenceTokenSafetyMargin);
}
