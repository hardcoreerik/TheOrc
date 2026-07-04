// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricExpandedCorpusTests
{
    [Test]
    public void Create_IsDeterministic_AcrossRepeatedCalls()
    {
        var first = DeterministicExpandedFabricCorpus.Create();
        var second = DeterministicExpandedFabricCorpus.Create();

        Assert.Multiple(() =>
        {
            Assert.That(first.Corpus.SourceDigest, Is.EqualTo(second.Corpus.SourceDigest));
            Assert.That(first.Corpus.GenerationId, Is.EqualTo(second.Corpus.GenerationId));
            Assert.That(first.Corpus.Segments, Has.Count.EqualTo(128));
        });
    }

    [Test]
    public void Create_HasNoEvidenceMarkerAnywhereInRenderedText()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();

        foreach (var segment in fixture.Corpus.Segments)
            Assert.That(segment.Text, Does.Not.Contain("EVIDENCE:"),
                $"segment {segment.SegmentId} must not carry the frozen fixture's literal marker");
    }

    [Test]
    public void Create_ManifestCounts_MatchTheDocumentedCorpusASpec()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var manifest = fixture.Manifest;

        Assert.Multiple(() =>
        {
            Assert.That(manifest.LocalFacts, Has.Count.EqualTo(40));
            Assert.That(manifest.MultiHopChains.Count(c => c.HopStatements.Count == 2), Is.EqualTo(30),
                "30 two-hop chains");
            Assert.That(manifest.MultiHopChains.Count(c => c.HopStatements.Count is >= 3 and <= 5), Is.EqualTo(15),
                "15 three-to-five-hop chains");
            Assert.That(manifest.Contradictions, Has.Count.EqualTo(20));
            Assert.That(manifest.UnanswerableGaps, Has.Count.EqualTo(20));
            Assert.That(manifest.ExhaustiveCategories, Has.Count.EqualTo(15));
            Assert.That(manifest.ThemeClusters, Has.Count.GreaterThanOrEqualTo(15));
        });
    }

    [Test]
    public void Create_EveryManifestStatement_VerifiesAgainstRenderedSegmentText()
    {
        // Create() throws internally if this doesn't hold; this test additionally re-checks a
        // representative cross-section from the outside, using only public manifest data, so a
        // future refactor that removed the internal check would still be caught here.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var textBySegment = fixture.Corpus.Segments.ToDictionary(s => s.SegmentId, s => s.Text);

        foreach (var fact in fixture.Manifest.LocalFacts)
            Assert.That(textBySegment[fact.SegmentId], Does.Contain(fact.StatementText));

        foreach (var chain in fixture.Manifest.MultiHopChains)
            for (var i = 0; i < chain.HopStatements.Count; i++)
                Assert.That(textBySegment[chain.HopSegmentIds[i]], Does.Contain(chain.HopStatements[i]));

        foreach (var contradiction in fixture.Manifest.Contradictions)
        {
            Assert.That(textBySegment[contradiction.EarlierSegmentId], Does.Contain(contradiction.EarlierStatement));
            Assert.That(textBySegment[contradiction.LaterSegmentId], Does.Contain(contradiction.LaterStatement));
        }
    }

    [Test]
    public void Create_LongChains_DerivedAnswerTerms_MatchTheActualClosingStatement()
    {
        // Regression guard: an earlier version advanced the running reference one step past what
        // the closing hop's own sentence stated, so the manifest's DerivedAnswerTerms referenced a
        // value that never appeared anywhere in the rendered corpus. Create()'s own self-check
        // would have caught this too, but this pins the specific failure mode directly.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var textBySegment = fixture.Corpus.Segments.ToDictionary(s => s.SegmentId, s => s.Text);

        foreach (var chain in fixture.Manifest.MultiHopChains.Where(c => c.ChainId.StartsWith("chain-lh-", StringComparison.Ordinal)))
        {
            var closingStatement = chain.HopStatements[^1];
            var closingSegmentText = textBySegment[chain.HopSegmentIds[^1]];
            Assert.That(closingSegmentText, Does.Contain(closingStatement));
            foreach (var term in chain.DerivedAnswerTerms)
                Assert.That(closingStatement, Does.Contain(term),
                    $"chain {chain.ChainId}'s derived term '{term}' must appear in its own closing statement");
        }
    }

    [Test]
    public void Create_ExhaustiveCategories_HaveExactUniqueOccurrenceIds()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();

        foreach (var category in fixture.Manifest.ExhaustiveCategories)
        {
            Assert.That(category.OccurrenceIds, Is.Unique);
            Assert.That(category.OccurrenceIds.Count, Is.EqualTo(category.OccurrenceSegmentIds.Count));
        }
    }

    [Test]
    public void Create_RejectsSectionCountBelowMinimum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicExpandedFabricCorpus.Create(32));
    }

    [Test]
    public void Create_FrozenFixtureIdentity_IsUnaffected()
    {
        // The expanded generator must never change the frozen CF-0/CF-7 fixture's identity.
        var frozen = DeterministicFabricCorpus.Create();
        Assert.That(frozen.Corpus.CorpusId, Is.EqualTo(DeterministicFabricCorpus.CorpusId));
        Assert.That(DeterministicExpandedFabricCorpus.CorpusId, Is.Not.EqualTo(DeterministicFabricCorpus.CorpusId));
    }
}
