// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricCf0Tests
{
    [Test]
    public void DeterministicCorpus_RebuildsIdentically_AndExceeds8KContext()
    {
        var first = DeterministicFabricCorpus.Create();
        var second = DeterministicFabricCorpus.Create();

        Assert.Multiple(() =>
        {
            Assert.That(first.Corpus.Segments, Has.Count.EqualTo(16));
            Assert.That(first.Corpus.EstimatedSourceTokens, Is.GreaterThan(8192));
            Assert.That(first.Corpus.SourceDigest, Is.EqualTo(second.Corpus.SourceDigest));
            Assert.That(first.Corpus.GenerationId, Is.EqualTo(second.Corpus.GenerationId));
            Assert.That(first.Corpus.Segments.Select(segment => segment.SegmentId),
                Is.EqualTo(second.Corpus.Segments.Select(segment => segment.SegmentId)));
            // The frozen 5-question fixture predates the Paraphrased/GlobalSynthesis kinds added
            // for the expanded corpus's question suite (docs' 7-category, 150-question spec) --
            // it only ever needs to cover its own 5 hardcoded kinds, not every enum value.
            Assert.That(first.Questions.Select(question => question.Kind), Is.EquivalentTo(
                new[]
                {
                    FabricQuestionKind.LocalFact,
                    FabricQuestionKind.MultiHop,
                    FabricQuestionKind.Contradiction,
                    FabricQuestionKind.Exhaustive,
                    FabricQuestionKind.Unanswerable,
                }));
        });
    }

    [Test]
    public void EvidenceProcessor_AnchorsExactQuote_AndComputesTrustedDigest()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var corpus = fixture.Corpus;
        var segment = corpus.Segments[0];
        const string quote = "Observatory Seven's assigned call sign is LANTERN.";
        var draft = CardFor(corpus, segment, quote);

        var result = FabricEvidenceProcessor.NormalizeAndValidate(corpus, segment, draft);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
        var citation = result.Card!.Claims.Single().Citations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(citation.CharStart, Is.EqualTo(segment.Text.IndexOf(quote, StringComparison.Ordinal)));
            Assert.That(citation.CharEnd, Is.EqualTo(citation.CharStart + quote.Length));
            Assert.That(citation.QuoteDigest, Is.EqualTo(FabricHashing.Sha256(quote)));
            Assert.That(citation.SegmentId, Is.EqualTo(segment.SegmentId));
        });
    }

    [Test]
    public void EvidenceProcessor_RepairsMalformedCoordinates_WhenQuoteIsExact()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var corpus = fixture.Corpus;
        var segment = corpus.Segments[0];
        const string quote = "Observatory Seven's assigned call sign is LANTERN.";
        var draft = CardFor(corpus, segment, quote) with
        {
            Claims =
            [
                CardFor(corpus, segment, quote).Claims[0] with
                {
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = segment.SegmentId,
                            CharStart = 0,
                            CharEnd = 5,
                            Quote = quote,
                            QuoteDigest = "wrong",
                        },
                    ],
                },
            ],
        };

        var result = FabricEvidenceProcessor.NormalizeAndValidate(corpus, segment, draft);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
        var citation = result.Card!.Claims.Single().Citations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(citation.CharStart, Is.EqualTo(segment.Text.IndexOf(quote, StringComparison.Ordinal)));
            Assert.That(citation.CharEnd, Is.EqualTo(citation.CharStart + quote.Length));
            Assert.That(citation.QuoteDigest, Is.EqualTo(FabricHashing.Sha256(quote)));
        });
    }

    [Test]
    public void EvidenceProcessor_RejectsCrossSegmentCitation()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var corpus = fixture.Corpus;
        var segment = corpus.Segments[0];
        var wrong = corpus.Segments[1];
        var draft = CardFor(corpus, segment, "Observatory Seven's assigned call sign is LANTERN.");
        draft = draft with
        {
            Claims =
            [
                draft.Claims[0] with
                {
                    Citations = [draft.Claims[0].Citations[0] with { SegmentId = wrong.SegmentId }],
                },
            ],
        };

        var result = FabricEvidenceProcessor.NormalizeAndValidate(corpus, segment, draft);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("unexpected segment"));
    }

    [Test]
    public void EvidenceProcessor_RejectsAmbiguousOverlappingQuote()
    {
        var fixture = DeterministicFabricCorpus.Create();
        const string text = "aaa";
        var segment = new FabricSegment("overlap", 1, "Overlap", text, FabricHashing.Sha256(text), 1);
        var corpus = fixture.Corpus with { Segments = [segment] };

        var result = FabricEvidenceProcessor.NormalizeAndValidate(corpus, segment, CardFor(corpus, segment, "aa"));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("ambiguous"));
    }

    [Test]
    public void EvidenceProcessor_RejectsCanonicalClaimIdCollision()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var segment = fixture.Corpus.Segments[0];
        const string quote = "Observatory Seven's assigned call sign is LANTERN.";
        var draft = CardFor(fixture.Corpus, segment, quote);
        draft = draft with { Claims = [draft.Claims[0], draft.Claims[0] with { ClaimId = "different-input-id" }] };

        var result = FabricEvidenceProcessor.NormalizeAndValidate(fixture.Corpus, segment, draft);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("normalized claimId"));
    }

    [Test]
    public void JsonParser_ExtractsObjectFromDecoratedModelOutput()
    {
        var parsed = FabricJson.ParseModelObject<FabricReductionDraft>(
            "preface\n{\"schemaVersion\":\"cf0-reduction-1.0\",\"summary\":\"ok\",\"claimIds\":[],\"conflicts\":[]}\nafter");

        Assert.That(parsed.Summary, Is.EqualTo("ok"));
    }

    [Test]
    public void JsonParser_RepairsUnterminatedObject_WhenThePayloadIsOtherwiseComplete()
    {
        var parsed = FabricJson.ParseModelObject<FabricReductionDraft>(
            "{\"schemaVersion\":\"cf0-reduction-1.0\",\"summary\":\"ok\",\"claimIds\":[],\"conflicts\":[]");

        Assert.That(parsed.Summary, Is.EqualTo("ok"));
    }

    [Test]
    public void JsonParser_SanitizesKeywordSuffixArtifact_FalseC()
    {
        // "falseC" is the classic token-boundary artifact: the literal token "false" immediately
        // followed by the first character of the next word token (e.g. "Charles"). The sanitizer
        // must strip the suffix without touching "false" inside string values.
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"ok\",\"abstained\":falseC,\"claims\":[]}");

        Assert.That(parsed.Answer, Is.EqualTo("ok"));
        Assert.That(parsed.Abstained, Is.False);
    }

    [Test]
    public void JsonParser_SanitizesKeywordSuffixArtifact_TrueX()
    {
        // "trueX" outside a string should collapse to the keyword "true".
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"ok\",\"abstained\":trueX,\"claims\":[]}");
        Assert.That(parsed.Abstained, Is.True);
    }

    [Test]
    public void JsonParser_SanitizesKeywordSuffixArtifact_NullValue()
    {
        // "nullValue" outside a string should collapse to "null". Test via a nullable string field
        // because null → non-nullable bool is a deserializer type error, not a JSON syntax error.
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":nullValue,\"abstained\":false,\"claims\":[]}");
        Assert.That(parsed.Answer, Is.Null);
    }

    [Test]
    public void JsonParser_SanitizesKeywordSuffix_DoesNotCorruptStringContents()
    {
        // "falsehood" and "trueColor" inside JSON string values must be preserved verbatim —
        // the sanitizer is only allowed to modify tokens that appear outside string boundaries.
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"falsehood is trueColor nullValue\",\"abstained\":false,\"claims\":[]}");

        Assert.That(parsed.Answer, Is.EqualTo("falsehood is trueColor nullValue"));
        Assert.That(parsed.Abstained, Is.False);
    }

    [Test]
    public void JsonParser_HandlesTrailingCommaInObject()
    {
        // Models sometimes emit a trailing comma after the last key-value pair.
        var parsed = FabricJson.ParseModelObject<FabricReductionDraft>(
            "{\"schemaVersion\":\"cf0-reduction-1.0\",\"summary\":\"ok\",\"claimIds\":[],\"conflicts\":[],}");

        Assert.That(parsed.Summary, Is.EqualTo("ok"));
    }

    [Test]
    public void JsonParser_HandlesTrailingCommaInArray()
    {
        var parsed = FabricJson.ParseModelObject<FabricReductionDraft>(
            "{\"schemaVersion\":\"cf0-reduction-1.0\",\"summary\":\"ok\",\"claimIds\":[\"a\",\"b\",],\"conflicts\":[]}");

        Assert.That(parsed.ClaimIds, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void JsonParser_TrySanitizeLiteralSuffixes_ReturnsNullWhenNothingChanged()
    {
        // If the input JSON is already clean, the sanitizer must return null (no copy allocated).
        var clean = "{\"schemaVersion\":\"cf0-reduction-1.0\",\"summary\":\"ok\",\"claimIds\":[],\"conflicts\":[]}";
        Assert.That(FabricJson.TrySanitizeLiteralSuffixes(clean), Is.Null);
    }

    [Test]
    public void JsonParser_SanitizesUnescapedInnerQuotes()
    {
        // Reproduces the exact CF-7 HARDCOREPC smoke2 B0 failure (local-fact-008): the model
        // quoted a term ("Chapter Alpha") inline inside the answer string without escaping it,
        // which otherwise truncates the JSON string at the first embedded quote and corrupts
        // everything after it ("'C' is invalid after a value...").
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"I do not have information regarding a " +
            "\"Chapter Alpha\" or its reported base reading.\",\"abstained\":true,\"claims\":[]}");

        Assert.That(parsed.Answer, Is.EqualTo(
            "I do not have information regarding a \"Chapter Alpha\" or its reported base reading."));
        Assert.That(parsed.Abstained, Is.True);
    }

    [Test]
    public void JsonParser_TrySanitizeUnescapedQuotes_ReturnsNullWhenNothingChanged()
    {
        // If the input JSON is already clean, the sanitizer must return null (no copy allocated).
        var clean = "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"ok\",\"abstained\":false,\"claims\":[]}";
        Assert.That(FabricJson.TrySanitizeUnescapedQuotes(clean), Is.Null);
    }

    [Test]
    public void JsonParser_SanitizesUnescapedQuoteFollowedByColon_InValueString()
    {
        // CodeRabbit PR #37 finding: a colon is only a real string terminator for object *keys*.
        // A value can legitimately contain a quoted term immediately followed by a colon (e.g.
        // "...called it "Chapter Alpha": a fitting name..."); treating every in-string quote
        // followed by ':' as a terminator would wrongly cut the value there.
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"The crew called it " +
            "\"Chapter Alpha\": a fitting name.\",\"abstained\":false,\"claims\":[]}");

        Assert.That(parsed.Answer, Is.EqualTo(
            "The crew called it \"Chapter Alpha\": a fitting name."));
    }

    [Test]
    public void JsonParser_SanitizesUnescapedQuote_CombinedWithKeywordSuffixArtifact()
    {
        // CodeRabbit PR #37 finding: quote repair and keyword-suffix repair must both be
        // attempted regardless of which artifact appears first, since a single response can
        // carry both (an unescaped inner quote plus a keyword run into the next token).
        var parsed = FabricJson.ParseModelObject<FabricAnswerDraft>(
            "{\"schemaVersion\":\"cf0-answer-1.0\",\"answer\":\"I found " +
            "\"Chapter Alpha\" in the log.\",\"abstained\":trueX,\"claims\":[]}");

        Assert.That(parsed.Answer, Is.EqualTo("I found \"Chapter Alpha\" in the log."));
        Assert.That(parsed.Abstained, Is.True);
    }

    [Test]
    public void ContextBudget_RejectsImpossibleConfiguration()
    {
        Assert.That(
            () => new FabricContextBudget(ContextLimit: 1024).Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(
            () => new FabricContextBudget(ContextLimit: 2048, ResponseReserve: 1900, SystemReserve: 512).Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ContextBudget_ReportsTheOffendingSetting()
    {
        var reserveError = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FabricContextBudget(SystemReserve: 64).Validate());
        var generationError = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FabricRunOptions(new FabricContextBudget(), ReducerMaxTokens: 64).Validate());

        Assert.Multiple(() =>
        {
            Assert.That(reserveError!.ParamName, Is.EqualTo("SystemReserve"));
            Assert.That(generationError!.ParamName, Is.EqualTo("ReducerMaxTokens"));
        });
    }

    [Test]
    public async Task FeasibilityRunner_CompletesNativeMapReduce_WithVerified8KAnswers()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime());

        var report = await runner.RunAsync(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(report.Passed, Is.True,
                string.Join("; ", report.Gates.Where(gate => !gate.Passed).Select(gate => $"{gate.Name}: {gate.Detail}")));
            Assert.That(report.Summary.AcceptedSegments, Is.EqualTo(16));
            Assert.That(report.Summary.PassedQuestions, Is.EqualTo(5));
            Assert.That(report.Summary.MaximumPromptTokens, Is.LessThan(8192));
            Assert.That(report.Summary.SourceToWorkingContextRatio, Is.GreaterThan(1));
            Assert.That(report.Reductions.Last().CoveredSegmentIds, Has.Count.EqualTo(16));
            Assert.That(report.Calls, Has.Count.EqualTo(26));
            Assert.That(report.Calls, Has.All.Matches<FabricCallMetrics>(call => call.FitsContext));
            Assert.That(report.Calls.Select(call => call.PromptPath).Distinct(), Is.EquivalentTo(new[] { "Scripted", "HostDeterministic" }));
            Assert.That(report.Calls, Has.All.Matches<FabricCallMetrics>(call => !string.IsNullOrWhiteSpace(call.RawOutputExcerpt)));
        });
    }

    [Test]
    public void AnswerVerifier_RejectsForgedCitation()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var segment = fixture.Corpus.Segments[0];
        var question = fixture.Questions.Single(item => item.QuestionId == "local-call-sign");
        var answer = new FabricAnswerDraft
        {
            Answer = "The call sign is LANTERN.",
            Claims =
            [
                new FabricAnswerClaim
                {
                    Text = "The call sign is LANTERN.",
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = segment.SegmentId,
                            Quote = "This sentence is not in the source.",
                        },
                    ],
                },
            ],
        };

        var result = FabricAnswerVerifier.NormalizeAndVerify(fixture.Corpus, question, answer);

        Assert.That(result.Verification.Passed, Is.False);
        Assert.That(result.Verification.Errors, Has.Some.Contains("not found"));
    }

    [Test]
    public void AnswerVerifier_RejectsBlankCitationSegmentIdWithoutThrowing()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var question = fixture.Questions.Single(item => item.QuestionId == "local-call-sign");
        var answer = new FabricAnswerDraft
        {
            Answer = "The call sign is LANTERN.",
            Claims =
            [
                new FabricAnswerClaim
                {
                    Text = "The call sign is LANTERN.",
                    Citations = [new FabricCitation { SegmentId = null!, Quote = "LANTERN" }],
                },
            ],
        };

        var result = FabricAnswerVerifier.NormalizeAndVerify(fixture.Corpus, question, answer);

        Assert.That(result.Verification.Passed, Is.False);
        Assert.That(result.Verification.Errors, Has.Some.Contains("segmentId is required"));
    }

    [Test]
    public void DigestOrdered_PreservesElementBoundaries()
    {
        Assert.That(
            FabricHashing.DigestOrdered(["a\nb", "c"]),
            Is.Not.EqualTo(FabricHashing.DigestOrdered(["a", "b\nc"])));
    }

    [Test]
    public void QuoteAnchorDiagnostics_Classify_Exact_Normalized_Soft_And_Missing()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var report = new ContextFabricBenchmarkExpansionRunner(runtime: null)
            .RunQuoteAnchoringDiagnostics(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(report.Results.Single(item => item.CaseId == "exact-call-sign").Mode, Is.EqualTo(FabricAnchorMode.Exact));
            Assert.That(report.Results.Single(item => item.CaseId == "normalized-smart-apostrophe").Mode, Is.EqualTo(FabricAnchorMode.NormalizedExact));
            Assert.That(report.Results.Single(item => item.CaseId == "normalized-whitespace").Mode, Is.EqualTo(FabricAnchorMode.NormalizedExact));
            Assert.That(report.Results.Single(item => item.CaseId == "soft-truncated-tail").Mode, Is.EqualTo(FabricAnchorMode.SoftCandidate));
            Assert.That(report.Results.Single(item => item.CaseId == "missing-hallucinated").Mode, Is.EqualTo(FabricAnchorMode.None));
        });
    }

    [Test]
    public async Task BoundaryStitchDiagnostics_ProduceDeterministicPasses_WithScriptedRuntime()
    {
        var fixture = DeterministicFabricCorpus.CreateBoundaryStitchFixture();
        var runner = new ContextFabricBenchmarkExpansionRunner(new ScriptedFabricRuntime());

        var report = await runner.RunBoundaryStitchDiagnosticsAsync(fixture);

        Assert.Multiple(() =>
        {
            Assert.That(report.Results, Has.Count.EqualTo(2));
            Assert.That(report.Results, Has.All.Matches<FabricBoundaryStitchResult>(item => item.Passed));
            Assert.That(report.Calls, Has.Count.EqualTo(2));
            Assert.That(report.Results.Select(item => item.CaseId), Is.EquivalentTo(fixture.Cases.Select(item => item.CaseId)));
            Assert.That(report.Calls.Select(call => call.PromptPath).Distinct(), Is.EqualTo(new[] { "Scripted" }));
        });
    }

    private static FabricEvidenceCard CardFor(FabricCorpus corpus, FabricSegment segment, string quote) => new()
    {
        CorpusId = corpus.CorpusId,
        DocumentId = corpus.DocumentId,
        SegmentId = segment.SegmentId,
        Summary = quote,
        Claims =
        [
            new FabricClaim
            {
                ClaimId = $"{segment.SegmentId}-claim-1",
                Text = quote,
                Confidence = 1,
                Citations =
                [
                    new FabricCitation
                    {
                        SegmentId = segment.SegmentId,
                        CharStart = -1,
                        CharEnd = -1,
                        Quote = quote,
                    },
                ],
            },
        ],
    };
}
