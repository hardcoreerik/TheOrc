// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Services.ContextFabric;

/// <summary>One local fact selected for an external model to paraphrase into a low-lexical-overlap
/// question. <see cref="ReferenceStatement"/> is supplied only so the authoring model can see what
/// NOT to echo -- the produced question must not reuse its distinctive wording.</summary>
public sealed record FabricParaphraseTarget(
    string FactId, string SegmentId, IReadOnlyList<string> ExpectedTerms, string ReferenceStatement);

public sealed record FabricMultiHopTarget(
    string ChainId,
    IReadOnlyList<string> HopStatements,
    IReadOnlyList<string> HopSegmentIds,
    string DerivedAnswer,
    IReadOnlyList<string> DerivedAnswerTerms);

public sealed record FabricGlobalSynthesisTarget(
    string ThemeId, string ThemeDescription, IReadOnlyList<string> ThemeFacts, IReadOnlyList<string> SegmentIds);

public sealed record FabricAuthoringLedger(
    string Instructions,
    IReadOnlyList<FabricParaphraseTarget> ParaphraseTargets,
    IReadOnlyList<FabricMultiHopTarget> MultiHopTargets,
    IReadOnlyList<FabricGlobalSynthesisTarget> GlobalSynthesisTargets);

/// <summary>
/// Builds the private authoring ledgers handed to external models (Grok, Codex) for the three
/// question categories that need natural-language phrasing diversity rather than deterministic
/// templating: Paraphrased retrieval, Multi-hop, and Global synthesis. Splitting authorship across
/// two different model families (rather than one model writing all 65) avoids a single model's
/// phrasing voice dominating the suite -- see .orc/adversarial/remediation-scope.md.
/// </summary>
public static class ExpandedFabricLedgerExport
{
    private const string ParaphraseAndMultiHopInstructions =
        "You are authoring benchmark questions for a source-grounded local-AI reading system. " +
        "For each ParaphraseTarget, write ONE question whose correct answer is exactly the fact " +
        "described, but phrase the question with LOW lexical overlap against ReferenceStatement -- " +
        "use synonyms, restructure the sentence, ask indirectly. Do not copy 3 or more consecutive " +
        "words from ReferenceStatement. Do not state the answer in the question. " +
        "For each MultiHopTarget, write ONE question that requires combining information from " +
        "every hop in HopStatements (in order) to arrive at DerivedAnswer -- do not give the answer " +
        "away, and do not require the reader to already know DerivedAnswer to understand the question. " +
        "Return a strict JSON array only, no prose, no markdown fences: " +
        "[{\"targetId\":\"<FactId or ChainId>\",\"questionText\":\"...\"}, ...] " +
        "with exactly one entry per target supplied, in the same order.";

    private const string GlobalSynthesisAndMultiHopInstructions =
        "You are authoring benchmark questions for a source-grounded local-AI reading system. " +
        "For each GlobalSynthesisTarget, write ONE open-ended synthesis question about the section " +
        "range described by ThemeDescription -- something that requires reading across multiple " +
        "sections in that range and summarizing a pattern or theme, not a single fact lookup. Use " +
        "ThemeFacts only as background context for what the range actually contains. " +
        "For each MultiHopTarget, write ONE question that requires combining information from " +
        "every hop in HopStatements (in order) to arrive at DerivedAnswer -- do not give the answer " +
        "away, and do not require the reader to already know DerivedAnswer to understand the question. " +
        "Return a strict JSON array only, no prose, no markdown fences: " +
        "[{\"targetId\":\"<ThemeId or ChainId>\",\"questionText\":\"...\"}, ...] " +
        "with exactly one entry per target supplied, in the same order.";

    public static FabricAuthoringLedger BuildGrokLedger(FabricExpandedManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var paraphraseFacts = manifest.LocalFacts.Skip(20).Take(20)
            .Select(fact => new FabricParaphraseTarget(fact.FactId, fact.SegmentId, fact.KeyTerms, fact.StatementText))
            .ToArray();
        var twoHopChains = manifest.MultiHopChains
            .Where(chain => chain.ChainId.StartsWith("chain-2h-", StringComparison.Ordinal))
            .Take(15)
            .Select(ToTarget)
            .ToArray();
        return new FabricAuthoringLedger(ParaphraseAndMultiHopInstructions, paraphraseFacts, twoHopChains, []);
    }

    public static FabricAuthoringLedger BuildCodexLedger(FabricExpandedManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var themes = manifest.ThemeClusters.Take(15)
            .Select(theme => new FabricGlobalSynthesisTarget(theme.ThemeId, theme.ThemeDescription, theme.ThemeFacts, theme.SegmentIds))
            .ToArray();
        var longChains = manifest.MultiHopChains
            .Where(chain => chain.ChainId.StartsWith("chain-lh-", StringComparison.Ordinal))
            .Take(15)
            .Select(ToTarget)
            .ToArray();
        return new FabricAuthoringLedger(GlobalSynthesisAndMultiHopInstructions, [], longChains, themes);
    }

    private static FabricMultiHopTarget ToTarget(FabricMultiHopChain chain) =>
        new(chain.ChainId, chain.HopStatements, chain.HopSegmentIds, chain.DerivedAnswer, chain.DerivedAnswerTerms);

    public static async Task<string> WriteAsync(FabricAuthoringLedger ledger, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new JsonSerializerOptions(FabricJson.Options) { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ledger, options), ct).ConfigureAwait(false);
        return path;
    }
}
