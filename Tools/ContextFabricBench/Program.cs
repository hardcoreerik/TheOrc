// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.ContextFabric;

namespace ContextFabricBench;

internal static class Program
{
    private enum BenchmarkSuite
    {
        Cf0,
        QuoteAnchor,
        Stitch,
        Cf7Gate,
        Scale,
        ExportLedger,
        MergeAuthored,
        Cf7GateExpanded,
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ParseArgs(args);
            var output = options.OutputDirectory ?? Path.Combine(
                Environment.CurrentDirectory,
                ".orc",
                "context-fabric",
                "benchmarks");
            if (options.Suite == BenchmarkSuite.ExportLedger)
            {
                var expanded = DeterministicExpandedFabricCorpus.Create();
                var grokPath = await ExpandedFabricLedgerExport
                    .WriteAsync(ExpandedFabricLedgerExport.BuildGrokLedger(expanded.Manifest),
                        Path.Combine(output, "grok-ledger.json"))
                    .ConfigureAwait(false);
                var codexPath = await ExpandedFabricLedgerExport
                    .WriteAsync(ExpandedFabricLedgerExport.BuildCodexLedger(expanded.Manifest),
                        Path.Combine(output, "codex-ledger.json"))
                    .ConfigureAwait(false);
                Console.WriteLine("Context Fabric expanded-corpus authoring ledgers prepared");
                Console.WriteLine($"Grok ledger: {grokPath}");
                Console.WriteLine($"Codex ledger: {codexPath}");
                return 0;
            }

            if (options.Suite == BenchmarkSuite.MergeAuthored)
            {
                if (string.IsNullOrWhiteSpace(options.GrokAuthoredPath) || string.IsNullOrWhiteSpace(options.CodexAuthoredPath))
                {
                    Console.Error.WriteLine("merge-authored requires --grok-authored and --codex-authored paths.");
                    return 64;
                }

                var expanded = DeterministicExpandedFabricCorpus.Create();
                var hostQuestions = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(expanded.Manifest);

                var grokLedger = ExpandedFabricLedgerExport.BuildGrokLedger(expanded.Manifest);
                var codexLedger = ExpandedFabricLedgerExport.BuildCodexLedger(expanded.Manifest);
                var grokDrafts = ExpandedFabricAuthoredQuestionMerger.ParseDrafts(
                    await File.ReadAllTextAsync(options.GrokAuthoredPath).ConfigureAwait(false));
                var codexDrafts = ExpandedFabricAuthoredQuestionMerger.ParseDrafts(
                    await File.ReadAllTextAsync(options.CodexAuthoredPath).ConfigureAwait(false));

                var paraphraseQuestions = ExpandedFabricAuthoredQuestionMerger.MergeParaphraseQuestions(grokDrafts, grokLedger.ParaphraseTargets);
                var grokMultiHop = ExpandedFabricAuthoredQuestionMerger.MergeMultiHopQuestions(grokDrafts, grokLedger.MultiHopTargets);
                var codexMultiHop = ExpandedFabricAuthoredQuestionMerger.MergeMultiHopQuestions(codexDrafts, codexLedger.MultiHopTargets);
                var synthesisQuestions = ExpandedFabricAuthoredQuestionMerger.MergeGlobalSynthesisQuestions(codexDrafts, codexLedger.GlobalSynthesisTargets);

                var allCandidates = hostQuestions
                    .Concat(paraphraseQuestions)
                    .Concat(grokMultiHop)
                    .Concat(codexMultiHop)
                    .Concat(synthesisQuestions)
                    .ToArray();

                var (verified, failures) = ExpandedFabricAuthoredQuestionMerger.Verify(allCandidates, expanded.Corpus.Segments);

                Console.WriteLine($"Candidates: {allCandidates.Length} (host {hostQuestions.Count}, paraphrase {paraphraseQuestions.Count}, " +
                    $"multi-hop {grokMultiHop.Count + codexMultiHop.Count}, global synthesis {synthesisQuestions.Count})");
                Console.WriteLine($"Verified: {verified.Count} / Rejected: {failures.Count}");
                foreach (var failure in failures)
                    Console.WriteLine($"REJECTED {failure.QuestionId}: {failure.Reason}");

                var byKind = verified.GroupBy(q => q.Kind).ToDictionary(g => g.Key, g => g.Count());
                foreach (FabricQuestionKind kind in Enum.GetValues<FabricQuestionKind>())
                    Console.WriteLine($"  {kind}: {(byKind.TryGetValue(kind, out var n) ? n : 0)}");

                Directory.CreateDirectory(output);
                var jsonOptions = new System.Text.Json.JsonSerializerOptions(FabricJson.Options) { WriteIndented = true };
                var manifestPath = Path.Combine(output, "expanded-question-suite.json");
                await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(verified, jsonOptions))
                    .ConfigureAwait(false);
                Console.WriteLine($"Verified suite: {manifestPath}");

                var split = ExpandedFabricQuestionSplitter.Split(verified);
                var devPath = Path.Combine(output, "expanded-question-suite-dev.json");
                var heldOutPath = Path.Combine(output, "expanded-question-suite-heldout.json");
                await File.WriteAllTextAsync(devPath, System.Text.Json.JsonSerializer.Serialize(split.Development, jsonOptions))
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(heldOutPath, System.Text.Json.JsonSerializer.Serialize(split.HeldOut, jsonOptions))
                    .ConfigureAwait(false);
                Console.WriteLine($"Development set ({split.Development.Count}): {devPath}");
                Console.WriteLine($"Held-out set ({split.HeldOut.Count}): {heldOutPath}");

                return failures.Count == 0 ? 0 : 2;
            }

            if (options.Suite == BenchmarkSuite.QuoteAnchor)
            {
                var fixture = DeterministicFabricCorpus.Create();
                var quoteReport = new ContextFabricBenchmarkExpansionRunner(runtime: null)
                    .RunQuoteAnchoringDiagnostics(fixture);
                var quotePaths = await ContextFabricBenchmarkExpansionWriter
                    .WriteQuoteAnchoringAsync(quoteReport, output)
                    .ConfigureAwait(false);
                Console.WriteLine("Context Fabric quote-anchor diagnostics prepared");
                Console.WriteLine($"JSON: {quotePaths.JsonPath}");
                Console.WriteLine($"Markdown: {quotePaths.MarkdownPath}");
                return 0;
            }

            var modelRoot = options.ModelRoot ?? Environment.GetEnvironmentVariable("THEORC_MODEL_ROOT");
            if (string.IsNullOrWhiteSpace(modelRoot) || !Directory.Exists(modelRoot))
            {
                Console.Error.WriteLine("A valid --model-root or THEORC_MODEL_ROOT is required.");
                PrintUsage();
                return 64;
            }

            var depot = ModelDepot.Scan(modelRoot);
            // --model pins role resolution to a specific GGUF by name substring, so a shared
            // depot changing underneath the run (models added/disabled by other work) can't
            // silently swap the benchmarked model. THEORC_CF_MODEL is the env-var equivalent.
            var modelPin = options.ModelPin ?? Environment.GetEnvironmentVariable("THEORC_CF_MODEL");
            if (!string.IsNullOrWhiteSpace(modelPin))
            {
                depot = depot.WithBaseModelFilter(modelPin);
                Console.WriteLine($"Model pin: '{modelPin}'");
            }
            var researcher = depot.ResolveRole(RuntimeRole.Researcher, RuntimeWorkloadKind.ContextFabricReader);
            var reviewer = depot.ResolveRole(RuntimeRole.Reviewer, RuntimeWorkloadKind.ContextFabricReviewer);
            var requiresReviewer = options.Suite is BenchmarkSuite.Cf0 or BenchmarkSuite.Cf7Gate or BenchmarkSuite.Scale or BenchmarkSuite.Cf7GateExpanded;
            if (researcher is null || (reviewer is null && requiresReviewer))
            {
                Console.Error.WriteLine($"No native base GGUF was resolved beneath '{Path.GetFullPath(modelRoot)}'.");
                PrintDepotDiagnostics(depot, modelRoot);
                return 65;
            }

            var researcherAdmission = ModelAdmissionGate.Evaluate(researcher!.BaseModel, RuntimeWorkloadKind.ContextFabricReader);
            var reviewerAdmission = reviewer is null
                ? null
                : ModelAdmissionGate.Evaluate(reviewer.BaseModel, RuntimeWorkloadKind.ContextFabricReviewer);
            var benchmarkEnvironment = new FabricBenchmarkEnvironment(BuildEnvironmentLanes(researcher, reviewer, researcherAdmission, reviewerAdmission));
            if (researcherAdmission.Verdict == ModelAdmissionVerdict.Rejected ||
                (requiresReviewer && reviewerAdmission?.Verdict == ModelAdmissionVerdict.Rejected))
            {
                Console.Error.WriteLine("Context Fabric preflight rejected the resolved native model selection.");
                PrintAdmission("Researcher", researcher.BaseModel.DisplayName, researcherAdmission);
                if (reviewerAdmission is not null && reviewer is not null)
                    PrintAdmission("Reviewer", reviewer.BaseModel.DisplayName, reviewerAdmission);
                Console.Error.WriteLine("Choose a stronger admitted or provisional GGUF for Context Fabric workloads.");
                PrintDepotDiagnostics(depot, modelRoot);
                return 66;
            }

            PrintAdmission("Researcher", researcher.BaseModel.DisplayName, researcherAdmission);
            if (reviewerAdmission is not null && reviewer is not null)
                PrintAdmission("Reviewer", reviewer.BaseModel.DisplayName, reviewerAdmission);

            var runOptions = new FabricRunOptions(
                new FabricContextBudget(options.ContextLength, options.ResponseReserve, 512),
                ReaderMaxTokens: options.ReaderMaxTokens,
                ReducerMaxTokens: options.ReducerMaxTokens,
                AnswerMaxTokens: options.AnswerMaxTokens,
                ReductionFanIn: 4,
                Temperature: 0.0);

            Console.WriteLine(options.Suite switch
            {
                BenchmarkSuite.Stitch => "Context Fabric boundary-stitch diagnostics",
                BenchmarkSuite.Cf7Gate => "Context Fabric CF-7 benchmark gate",
                BenchmarkSuite.Scale => "Context Fabric large-corpus scale run",
                _ => "Context Fabric CF-0 native feasibility run",
            });
            Console.WriteLine($"Model root: {Path.GetFullPath(modelRoot)}");
            Console.WriteLine($"Context: {options.ContextLength:N0} tokens");
            Console.WriteLine(options.Suite switch
            {
                BenchmarkSuite.Stitch => "Fixture: deterministic boundary-stitch cases",
                BenchmarkSuite.Scale => $"Corpus: deterministic synthetic book, {options.ScaleSegments} segments x {options.ScaleBackgroundLines} background lines",
                _ => "Corpus: deterministic 16-segment synthetic book",
            });

            await using var runtime = new NativeRoleRuntime(
                depot,
                new RuntimeOptions(options.ContextLength, options.GpuLayers, PreferGpu: options.GpuLayers != 0),
                roleBindings: BuildRoleBindings(researcher, reviewer));
            if (options.Suite == BenchmarkSuite.Stitch)
            {
                // Same reasoning as the expanded reader's ReaderMaxTokens bump above: the stitch
                // prompt asks for a full JSON summary plus every linked fact from both segments,
                // which needs more headroom than the frozen fixture's 1024-token default before
                // the model reliably finishes the JSON object instead of truncating mid-response
                // (observed live: a stitch call cut off mid-sentence, producing invalid JSON).
                var stitchRunOptions = runOptions with { ReaderMaxTokens = Math.Max(runOptions.ReaderMaxTokens, 2048) };
                var stitchReport = await new ContextFabricBenchmarkExpansionRunner(runtime, stitchRunOptions)
                    .RunBoundaryStitchDiagnosticsAsync(DeterministicFabricCorpus.CreateBoundaryStitchFixture())
                    .ConfigureAwait(false);
                var stitchPaths = await ContextFabricBenchmarkExpansionWriter
                    .WriteBoundaryStitchAsync(stitchReport, output)
                    .ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"Passed: {stitchReport.Results.Count(result => result.Passed)} / {stitchReport.Results.Count}");
                Console.WriteLine($"JSON: {stitchPaths.JsonPath}");
                Console.WriteLine($"Markdown: {stitchPaths.MarkdownPath}");
                foreach (var result in stitchReport.Results.Where(result => !result.Passed))
                    Console.WriteLine($"FAILED {result.CaseId}: {string.Join("; ", result.Errors)}");
                return stitchReport.Results.All(result => result.Passed) ? 0 : 2;
            }

            if (options.Suite == BenchmarkSuite.Cf7GateExpanded)
            {
                if (string.IsNullOrWhiteSpace(options.HeldOutQuestionsPath) || !File.Exists(options.HeldOutQuestionsPath))
                {
                    Console.Error.WriteLine("cf7-gate-expanded requires --heldout-questions pointing at a verified question-suite JSON file.");
                    return 64;
                }

                var expanded = DeterministicExpandedFabricCorpus.Create();
                var heldOutQuestions = System.Text.Json.JsonSerializer.Deserialize<List<FabricBenchmarkQuestion>>(
                    await File.ReadAllTextAsync(options.HeldOutQuestionsPath).ConfigureAwait(false), FabricJson.Options)
                    ?? throw new InvalidOperationException("Held-out question file parsed to null.");
                if (options.MaxQuestions is { } cap && cap < heldOutQuestions.Count)
                    heldOutQuestions = heldOutQuestions.Take(cap).ToList();

                Console.WriteLine($"Expanded corpus: {expanded.Corpus.Segments.Count} segments, {expanded.Corpus.EstimatedSourceTokens:N0} estimated source tokens");
                Console.WriteLine($"Held-out questions: {heldOutQuestions.Count}");

                var expandedFixture = new FabricBenchmarkFixture(expanded.Corpus, heldOutQuestions);
                // Open-extraction reading has no fixed claim-count checklist to bound completion
                // length the way the marked reader prompt's evidenceLines count does, so a segment
                // with several genuine facts plus full citation quotes needs more headroom than the
                // frozen fixture's 1024-token default before the model reliably finishes the JSON object.
                var expandedRunOptions = runOptions with
                {
                    OpenExtractionReading = true,
                    ReaderMaxTokens = Math.Max(runOptions.ReaderMaxTokens, 2048),
                };

                // Quote-anchor and boundary-stitch diagnostics test host-verification MECHANISM
                // (does an exact/normalized quote anchor, does a stitch preserve linked facts) --
                // corpus-agnostic checks, so these still run against the frozen fixture rather than
                // requiring a second full diagnostic pass over the expanded corpus.
                var frozenForDiagnostics = DeterministicFabricCorpus.Create();
                var quoteReport = new ContextFabricBenchmarkExpansionRunner(runtime: null)
                    .RunQuoteAnchoringDiagnostics(frozenForDiagnostics);
                // Same reasoning as the expanded reader's ReaderMaxTokens bump above: the stitch
                // prompt asks for a full JSON summary plus every linked fact from both segments,
                // which needs more headroom than the frozen fixture's 1024-token default before
                // the model reliably finishes the JSON object instead of truncating mid-response
                // (observed live: a stitch call cut off mid-sentence, producing invalid JSON).
                var stitchRunOptions = runOptions with { ReaderMaxTokens = Math.Max(runOptions.ReaderMaxTokens, 2048) };
                var stitchReport = await new ContextFabricBenchmarkExpansionRunner(runtime, stitchRunOptions)
                    .RunBoundaryStitchDiagnosticsAsync(DeterministicFabricCorpus.CreateBoundaryStitchFixture())
                    .ConfigureAwait(false);

                Console.WriteLine("Running B3 single-node Context Fabric (open-extraction reading)...");
                var expandedRunner = new ContextFabricFeasibilityRunner(runtime, expandedRunOptions);
                var expandedReport = (await expandedRunner.RunAsync(expandedFixture).ConfigureAwait(false)) with
                {
                    Environment = benchmarkEnvironment,
                };
                var expandedReportPaths = await ContextFabricReportWriter.WriteAsync(expandedReport, output).ConfigureAwait(false);
                Console.WriteLine($"  B3 verdict: {(expandedReport.Passed ? "PASS" : "FAIL")}, " +
                    $"segments {expandedReport.Summary.AcceptedSegments}/{expandedReport.Summary.ExpectedSegments}, " +
                    $"questions {expandedReport.Summary.PassedQuestions}/{expandedReport.Summary.TotalQuestions}");
                Console.WriteLine($"  JSON: {expandedReportPaths.JsonPath}");

                var expandedBaselineRunner = new ContextFabricBaselineRunner(runtime, expandedRunOptions);
                var expandedFrozenRuns = new List<FabricBenchmarkSystemGate>();
                foreach (var (label, run) in new (string, Func<Task<FabricBaselineSystemReport>>)[]
                {
                    ("B0 closed-book", () => expandedBaselineRunner.RunClosedBookAsync(expandedFixture)),
                    ("B1 truncated-prompt", () => expandedBaselineRunner.RunTruncatedPromptAsync(expandedFixture)),
                    ("B2 top-k RAG", () => expandedBaselineRunner.RunTopKRagAsync(expandedFixture)),
                })
                {
                    Console.WriteLine($"Running {label} baseline against the expanded corpus...");
                    var baselineReport = await run().ConfigureAwait(false);
                    var baselinePath = await ContextFabricBaselineWriter.WriteAsync(baselineReport, output).ConfigureAwait(false);
                    Console.WriteLine($"  {baselineReport.Detail}");
                    Console.WriteLine($"  JSON: {baselinePath}");
                    expandedFrozenRuns.Add(ContextFabricBaselineRunner.ToSystemGate(baselineReport));
                }

                // B4 remains the CF-6 distributed-HIVE evidence from its own prior run; re-validating
                // distributed recovery against this new corpus is a separate, later undertaking, not
                // re-run here -- reported as-is rather than silently assumed equivalent.
                var expandedB4Gate = ContextFabricBaselineRunner.LoadHiveAcceptanceGate(options.B4ArtifactPath);
                Console.WriteLine($"B4 HIVE artifact (frozen-corpus evidence, not re-validated against the expanded corpus this pass): " +
                    $"{expandedB4Gate.Status} - {expandedB4Gate.Detail}");
                expandedFrozenRuns.Add(expandedB4Gate);

                var expandedGateReport = ContextFabricBenchmarkGateEvaluator.Evaluate(expandedReport, quoteReport, stitchReport, expandedFrozenRuns);
                var expandedGatePaths = await ContextFabricBenchmarkGateWriter.WriteAsync(expandedGateReport, output).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"Verdict (expanded corpus, real held-out suite): {(expandedGateReport.ReadyForExpansion ? "GO" : "NO-GO")}");
                Console.WriteLine($"JSON: {expandedGatePaths.JsonPath}");
                Console.WriteLine($"Markdown: {expandedGatePaths.MarkdownPath}");
                foreach (var gate in expandedGateReport.Gates.Where(gate => !gate.Passed))
                    Console.WriteLine($"FAILED {gate.Name}: {gate.Detail}");
                return expandedGateReport.ReadyForExpansion ? 0 : 2;
            }

            var runFixture = options.Suite == BenchmarkSuite.Scale
                ? DeterministicFabricCorpus.Create(options.ScaleSegments, options.ScaleBackgroundLines)
                : DeterministicFabricCorpus.Create();
            if (options.Suite == BenchmarkSuite.Scale)
                Console.WriteLine($"Estimated source tokens: {runFixture.Corpus.EstimatedSourceTokens:N0}");
            var runner = new ContextFabricFeasibilityRunner(runtime, runOptions);
            var report = (await runner.RunAsync(runFixture).ConfigureAwait(false)) with
            {
                Environment = benchmarkEnvironment,
            };
            if (options.Suite == BenchmarkSuite.Cf7Gate)
            {
                var fixture = DeterministicFabricCorpus.Create();
                var quoteReport = new ContextFabricBenchmarkExpansionRunner(runtime: null)
                    .RunQuoteAnchoringDiagnostics(fixture);
                // Same reasoning as the expanded reader's ReaderMaxTokens bump above: the stitch
                // prompt asks for a full JSON summary plus every linked fact from both segments,
                // which needs more headroom than the frozen fixture's 1024-token default before
                // the model reliably finishes the JSON object instead of truncating mid-response
                // (observed live: a stitch call cut off mid-sentence, producing invalid JSON).
                var stitchRunOptions = runOptions with { ReaderMaxTokens = Math.Max(runOptions.ReaderMaxTokens, 2048) };
                var stitchReport = await new ContextFabricBenchmarkExpansionRunner(runtime, stitchRunOptions)
                    .RunBoundaryStitchDiagnosticsAsync(DeterministicFabricCorpus.CreateBoundaryStitchFixture())
                    .ConfigureAwait(false);

                var baselineRunner = new ContextFabricBaselineRunner(runtime, runOptions);
                var frozenRuns = new List<FabricBenchmarkSystemGate>();
                foreach (var (label, run) in new (string, Func<Task<FabricBaselineSystemReport>>)[]
                {
                    ("B0 closed-book", () => baselineRunner.RunClosedBookAsync(fixture)),
                    ("B1 truncated-prompt", () => baselineRunner.RunTruncatedPromptAsync(fixture)),
                    ("B2 top-k RAG", () => baselineRunner.RunTopKRagAsync(fixture)),
                })
                {
                    Console.WriteLine($"Running {label} baseline...");
                    var baselineReport = await run().ConfigureAwait(false);
                    var baselinePath = await ContextFabricBaselineWriter
                        .WriteAsync(baselineReport, output)
                        .ConfigureAwait(false);
                    Console.WriteLine($"  {baselineReport.Detail}");
                    Console.WriteLine($"  JSON: {baselinePath}");
                    frozenRuns.Add(ContextFabricBaselineRunner.ToSystemGate(baselineReport));
                }

                var b4Gate = ContextFabricBaselineRunner.LoadHiveAcceptanceGate(options.B4ArtifactPath);
                Console.WriteLine($"B4 HIVE artifact: {b4Gate.Status} - {b4Gate.Detail}");
                frozenRuns.Add(b4Gate);

                var gateReport = ContextFabricBenchmarkGateEvaluator.Evaluate(report, quoteReport, stitchReport, frozenRuns);
                var gatePaths = await ContextFabricBenchmarkGateWriter.WriteAsync(gateReport, output).ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"Verdict: {(gateReport.ReadyForExpansion ? "GO" : "NO-GO")}");
                Console.WriteLine($"JSON: {gatePaths.JsonPath}");
                Console.WriteLine($"Markdown: {gatePaths.MarkdownPath}");
                foreach (var gate in gateReport.Gates.Where(gate => !gate.Passed))
                    Console.WriteLine($"FAILED {gate.Name}: {gate.Detail}");
                return gateReport.ReadyForExpansion ? 0 : 2;
            }

            var paths = await ContextFabricReportWriter.WriteAsync(report, output).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"Segments: {report.Summary.AcceptedSegments}/{report.Summary.ExpectedSegments}");
            Console.WriteLine($"Questions: {report.Summary.PassedQuestions}/{report.Summary.TotalQuestions}");
            Console.WriteLine($"Source/working ratio: {report.Summary.SourceToWorkingContextRatio:F2}x");
            Console.WriteLine($"JSON: {paths.JsonPath}");
            Console.WriteLine($"Markdown: {paths.MarkdownPath}");
            foreach (var gate in report.Gates.Where(gate => !gate.Passed))
                Console.WriteLine($"FAILED {gate.Name}: {gate.Detail}");

            return report.Passed ? 0 : 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
            PrintUsage();
            return 64;
        }
        catch (FabricContextBudgetExceededException ex)
        {
            Console.Error.WriteLine($"Context budget failure: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Benchmark failed: {ex.Message}");
            return 1;
        }
    }

    private static CliOptions ParseArgs(string[] args)
    {
        string? modelRoot = null;
        string? output = null;
        string? b4Artifact = null;
        string? grokAuthored = null;
        string? codexAuthored = null;
        string? heldOutQuestions = null;
        int? maxQuestions = null;
        string? modelPin = null;
        var context = 8192;
        var scaleSegments = 640;
        var scaleBackgroundLines = 60;
        var responseReserve = 1536;
        var readerMax = 1024;
        var reducerMax = 768;
        var answerMax = 1536;
        var gpuLayers = -1;
        var suite = BenchmarkSuite.Cf0;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string NextValue()
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}.");
                return args[index];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--model-root": modelRoot = NextValue(); break;
                case "--output": output = NextValue(); break;
                case "--context": context = ParsePositive(NextValue(), arg); break;
                case "--response-reserve": responseReserve = ParsePositive(NextValue(), arg); break;
                case "--reader-max": readerMax = ParsePositive(NextValue(), arg); break;
                case "--reducer-max": reducerMax = ParsePositive(NextValue(), arg); break;
                case "--answer-max": answerMax = ParsePositive(NextValue(), arg); break;
                case "--gpu-layers": gpuLayers = ParseGpuLayers(NextValue(), arg); break;
                case "--suite": suite = ParseSuite(NextValue()); break;
                case "--b4-artifact": b4Artifact = NextValue(); break;
                case "--segments": scaleSegments = ParsePositive(NextValue(), arg); break;
                case "--background-lines": scaleBackgroundLines = ParsePositive(NextValue(), arg); break;
                case "--grok-authored": grokAuthored = NextValue(); break;
                case "--codex-authored": codexAuthored = NextValue(); break;
                case "--heldout-questions": heldOutQuestions = NextValue(); break;
                case "--max-questions": maxQuestions = ParsePositive(NextValue(), arg); break;
                case "--model": modelPin = NextValue(); break;
                default: throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new CliOptions(modelRoot, output, context, responseReserve, readerMax, reducerMax, answerMax, gpuLayers, suite, b4Artifact, scaleSegments, scaleBackgroundLines, grokAuthored, codexAuthored, heldOutQuestions, maxQuestions, modelPin);
    }

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{option} requires a positive integer.");

    private static int ParseGpuLayers(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed >= -1
            ? parsed
            : throw new ArgumentException($"{option} requires -1, 0, or a positive integer.");

    private static BenchmarkSuite ParseSuite(string value) => value.ToLowerInvariant() switch
    {
        "cf0" => BenchmarkSuite.Cf0,
        "quote-anchor" => BenchmarkSuite.QuoteAnchor,
        "stitch" => BenchmarkSuite.Stitch,
        "cf7-gate" => BenchmarkSuite.Cf7Gate,
        "scale" => BenchmarkSuite.Scale,
        "export-ledger" => BenchmarkSuite.ExportLedger,
        "merge-authored" => BenchmarkSuite.MergeAuthored,
        "cf7-gate-expanded" => BenchmarkSuite.Cf7GateExpanded,
        _ => throw new ArgumentException("Unknown suite. Use cf0, quote-anchor, stitch, cf7-gate, scale, export-ledger, merge-authored, or cf7-gate-expanded."),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: context-fabric-bench --model-root <folder> [options]");
        Console.WriteLine("  --suite <name>            cf0 | quote-anchor | stitch | cf7-gate | cf7-gate-expanded | scale (default cf0)");
        Console.WriteLine("  --output <folder>          Report directory (default .orc/context-fabric/benchmarks)");
        Console.WriteLine("  --context <tokens>        Native context length (default 8192)");
        Console.WriteLine("  --response-reserve <n>    Reserved response tokens (default 1536)");
        Console.WriteLine("  --reader-max <n>          Reader output limit (default 1024)");
        Console.WriteLine("  --reducer-max <n>         Reducer output limit (default 768)");
        Console.WriteLine("  --answer-max <n>          Answer output limit (default 1536)");
        Console.WriteLine("  --gpu-layers <n>          LLamaSharp GPU layers; 0 forces CPU (default -1)");
        Console.WriteLine("  --b4-artifact <path>      CF-6 HIVE acceptance JSON used as the frozen B4 run (cf7-gate suite)");
        Console.WriteLine("  --heldout-questions <p>   Path to held-out question JSON (required for cf7-gate-expanded)");
        Console.WriteLine("  --max-questions <n>       Cap questions processed (useful for smoke tests; default: all)");
        Console.WriteLine("  --segments <n>            Scale-suite segment count (default 640)");
        Console.WriteLine("  --background-lines <n>    Scale-suite background lines per segment (default 60; 640x60 is ~1M source tokens)");
    }

    private static void PrintDepotDiagnostics(ModelDepot depot, string modelRoot)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Model depot scan of '{Path.GetFullPath(modelRoot)}':");
        var baseModels = depot.Assets.Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf).ToArray();
        if (baseModels.Length == 0)
        {
            Console.Error.WriteLine("  (no active base-model .gguf files found)");
        }
        foreach (var asset in baseModels)
        {
            var verdict = ModelAdmissionGate.Evaluate(asset, RuntimeWorkloadKind.ContextFabricReader).Verdict;
            Console.Error.WriteLine($"  {asset.DisplayName}  ->  {verdict} for ContextFabricReader");
        }

        string[] disabled;
        try
        {
            disabled = Directory.GetFiles(modelRoot, "*.gguf.disabled", SearchOption.AllDirectories);
        }
        catch
        {
            disabled = [];
        }
        if (disabled.Length > 0)
        {
            Console.Error.WriteLine($"  {disabled.Length} disabled model file(s) were skipped (rename to .gguf to enable):");
            foreach (var file in disabled)
                Console.Error.WriteLine($"    {Path.GetFileName(file)}");
        }
    }

    private static void PrintAdmission(string role, string modelName, ModelAdmissionDecision decision)
    {
        Console.WriteLine($"{role} model: {modelName}");
        Console.WriteLine($"  Admission: {decision.Verdict} for {decision.Workload}");
        Console.WriteLine($"  Family: {decision.Fingerprint.FamilyLabel}; params: {(decision.Fingerprint.ParametersB?.ToString("0.#") ?? "unknown")}B");
        Console.WriteLine($"  Why: {decision.Summary}");
    }

    private static FabricBenchmarkLane ToBenchmarkLane(string role, RuntimeRoleBinding binding, ModelAdmissionDecision decision) =>
        new(
            role,
            binding.BaseModel.DisplayName,
            binding.BaseModel.Id,
            decision.Verdict,
            decision.Fingerprint.FamilyLabel,
            decision.Fingerprint.ParametersB,
            decision.Reasons);

    private static IReadOnlyList<FabricBenchmarkLane> BuildEnvironmentLanes(
        RuntimeRoleBinding researcher,
        RuntimeRoleBinding? reviewer,
        ModelAdmissionDecision researcherAdmission,
        ModelAdmissionDecision? reviewerAdmission)
    {
        var lanes = new List<FabricBenchmarkLane>
        {
            ToBenchmarkLane("Researcher", researcher, researcherAdmission),
        };
        if (reviewer is not null && reviewerAdmission is not null)
            lanes.Add(ToBenchmarkLane("Reviewer", reviewer, reviewerAdmission));
        return lanes;
    }

    private static IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding> BuildRoleBindings(
        RuntimeRoleBinding researcher,
        RuntimeRoleBinding? reviewer)
    {
        var bindings = new Dictionary<RuntimeRole, RuntimeRoleBinding>
        {
            [RuntimeRole.Researcher] = researcher,
        };
        if (reviewer is not null)
            bindings[RuntimeRole.Reviewer] = reviewer;
        return bindings;
    }

    private sealed record CliOptions(
        string? ModelRoot,
        string? OutputDirectory,
        int ContextLength,
        int ResponseReserve,
        int ReaderMaxTokens,
        int ReducerMaxTokens,
        int AnswerMaxTokens,
        int GpuLayers,
        BenchmarkSuite Suite,
        string? B4ArtifactPath,
        int ScaleSegments,
        int ScaleBackgroundLines,
        string? GrokAuthoredPath,
        string? CodexAuthoredPath,
        string? HeldOutQuestionsPath,
        int? MaxQuestions,
        string? ModelPin);
}
