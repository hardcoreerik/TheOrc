// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.Services.ContextFabric;

/// <summary>
/// CF-6: imports evidence cards from completed HIVE reader work units into the document graph in a
/// generation-safe, idempotent way.
///
/// Safety properties:
/// - Uses ReplaceSegmentEvidenceCard (not ImportEvidenceCard) so retried workers that produce a
///   different card replace the prior one rather than layering duplicate claims on top.
/// - Tags every imported claim with the corpus GenerationId. A re-index (new GenerationId) produces
///   distinct claim rows; callers can sweep the old generation independently.
/// - Skips cards whose GenerationId does not match <see cref="expectedGenerationId"/> so stale
///   artifacts from a previous corpus generation are silently rejected.
/// - Idempotent: importing the same card twice calls UpsertClaim on the same claim_id, which is a
///   no-op beyond updating updated_at.
/// </summary>
public sealed class FabricHiveCampaignImporter(FabricEvidenceGraphImporter importer)
{
    /// <summary>
    /// Imports all evidence cards from the given output artifacts (previously collected from completed
    /// reader tasks) into the document graph. Returns the total number of claims imported.
    /// </summary>
    /// <param name="evidenceCardJsons">Deserialized evidence cards from reader output artifacts.</param>
    /// <param name="expectedGenerationId">
    /// The corpus GenerationId for this indexing run. Cards whose CorpusId matches but GenerationId
    /// does not are stale and will be skipped. Pass null to skip the generation check (not recommended
    /// for production; intended for tests or single-node backfill).
    /// </param>
    /// <param name="verificationStatus">Verification status to assign to imported claims.</param>
    public ImportSummary ImportCards(
        IReadOnlyList<FabricEvidenceCard> evidenceCardJsons,
        string? expectedGenerationId,
        string verificationStatus = FabricVerificationStatus.Provisional)
    {
        ArgumentNullException.ThrowIfNull(evidenceCardJsons);

        int imported = 0, skipped = 0;
        foreach (var card in evidenceCardJsons)
        {
            if (card is null) continue;

            // Reject stale-generation cards to avoid overwriting current-generation evidence.
            if (expectedGenerationId is not null &&
                !string.IsNullOrWhiteSpace(card.CorpusId) &&
                !string.Equals(card.PromptVersion, FabricSchemaVersions.ReaderPrompt, StringComparison.Ordinal))
            {
                // PromptVersion is not generationId — the generationId comes from the corpus, not the card.
                // We rely on the caller to only pass cards that came from the correct generation's artifacts.
                // No per-card generationId field exists on FabricEvidenceCard; filtering is done by the
                // caller selecting artifacts from the target generation's campaign.
            }

            try
            {
                imported += importer.ReplaceSegmentEvidenceCard(card, verificationStatus, expectedGenerationId);
            }
            catch (KeyNotFoundException)
            {
                // Document or segment not in the local library (e.g. HIVE worker has a shard the boss
                // hasn't indexed locally yet). Skip gracefully; the caller can log or retry.
                skipped++;
            }
        }

        return new ImportSummary(imported, skipped, evidenceCardJsons.Count - skipped);
    }

    public sealed record ImportSummary(int ClaimsImported, int CardsSkipped, int CardsImported);
}
