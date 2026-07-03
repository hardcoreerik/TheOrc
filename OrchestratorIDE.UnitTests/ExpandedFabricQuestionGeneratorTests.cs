// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ExpandedFabricQuestionGeneratorTests
{
    [Test]
    public void GenerateHostTemplatedQuestions_ProducesExactlyEightyFive_AcrossFourCategories()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var questions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);

        Assert.Multiple(() =>
        {
            Assert.That(questions, Has.Count.EqualTo(85));
            Assert.That(questions.Count(q => q.Kind == FabricQuestionKind.LocalFact), Is.EqualTo(40));
            Assert.That(questions.Count(q => q.Kind == FabricQuestionKind.Exhaustive), Is.EqualTo(15));
            Assert.That(questions.Count(q => q.Kind == FabricQuestionKind.Unanswerable), Is.EqualTo(20));
            Assert.That(questions.Count(q => q.Kind == FabricQuestionKind.Contradiction), Is.EqualTo(10));
            Assert.That(questions.Select(q => q.QuestionId), Is.Unique);
        });
    }

    [Test]
    public void GenerateHostTemplatedQuestions_UnanswerableQuestions_ExpectAbstentionWithNoExpectedTerms()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var questions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);

        foreach (var question in questions.Where(q => q.Kind == FabricQuestionKind.Unanswerable))
        {
            Assert.That(question.ExpectAbstention, Is.True);
            Assert.That(question.ExpectedTerms, Is.Empty);
            Assert.That(question.ExpectedSegmentIds, Is.Empty);
        }
    }

    [Test]
    public void GenerateHostTemplatedQuestions_EveryExpectedSegmentId_ExistsInTheRenderedCorpus()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var questions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);
        var realSegmentIds = fixture.Corpus.Segments.Select(s => s.SegmentId).ToHashSet(StringComparer.Ordinal);

        foreach (var question in questions)
            foreach (var segmentId in question.ExpectedSegmentIds)
                Assert.That(realSegmentIds, Does.Contain(segmentId), $"question {question.QuestionId} references an unknown segment");
    }

    [Test]
    public void GenerateHostTemplatedQuestions_EveryExpectedTerm_AppearsSomewhereInItsExpectedSegments()
    {
        // This is the same mechanical check task #14 will run against the externally-authored
        // questions -- proving now, on the host-templated set, that it actually rejects a
        // fabricated/hallucinated ground-truth pairing before it is trusted for the other 65.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var questions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);
        var textBySegment = fixture.Corpus.Segments.ToDictionary(s => s.SegmentId, s => s.Text);

        foreach (var question in questions.Where(q => !q.ExpectAbstention))
        {
            var combinedText = string.Join(" ", question.ExpectedSegmentIds.Select(id => textBySegment[id]));
            foreach (var term in question.ExpectedTerms)
                Assert.That(combinedText, Does.Contain(term),
                    $"question {question.QuestionId} claims term '{term}' but it does not appear in its expected segments");
        }
    }

    [Test]
    public void GenerateHostTemplatedQuestions_ExhaustiveCategories_HaveExpectedSegmentIdsAlignedWithOccurrences()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var questions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);

        foreach (var question in questions.Where(q => q.Kind == FabricQuestionKind.Exhaustive))
            Assert.That(question.ExpectedSegmentIds, Has.Count.EqualTo(question.ExpectedTerms.Count));
    }
}
