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
            var researcher = depot.ResolveRole(RuntimeRole.Researcher, RuntimeWorkloadKind.ContextFabricReader);
            var reviewer = depot.ResolveRole(RuntimeRole.Reviewer, RuntimeWorkloadKind.ContextFabricReviewer);
            if (researcher is null || (reviewer is null && options.Suite == BenchmarkSuite.Cf0))
            {
                Console.Error.WriteLine($"No native base GGUF was resolved beneath '{Path.GetFullPath(modelRoot)}'.");
                return 65;
            }

            var researcherAdmission = ModelAdmissionGate.Evaluate(researcher!.BaseModel, RuntimeWorkloadKind.ContextFabricReader);
            var reviewerAdmission = reviewer is null
                ? null
                : ModelAdmissionGate.Evaluate(reviewer.BaseModel, RuntimeWorkloadKind.ContextFabricReviewer);
            var benchmarkEnvironment = new FabricBenchmarkEnvironment(BuildEnvironmentLanes(researcher, reviewer, researcherAdmission, reviewerAdmission));
            if (researcherAdmission.Verdict == ModelAdmissionVerdict.Rejected ||
                (options.Suite == BenchmarkSuite.Cf0 && reviewerAdmission?.Verdict == ModelAdmissionVerdict.Rejected))
            {
                Console.Error.WriteLine("Context Fabric preflight rejected the resolved native model selection.");
                PrintAdmission("Researcher", researcher.BaseModel.DisplayName, researcherAdmission);
                if (reviewerAdmission is not null && reviewer is not null)
                    PrintAdmission("Reviewer", reviewer.BaseModel.DisplayName, reviewerAdmission);
                Console.Error.WriteLine("Choose a stronger admitted or provisional GGUF for Context Fabric workloads.");
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

            Console.WriteLine(options.Suite == BenchmarkSuite.Stitch
                ? "Context Fabric boundary-stitch diagnostics"
                : "Context Fabric CF-0 native feasibility run");
            Console.WriteLine($"Model root: {Path.GetFullPath(modelRoot)}");
            Console.WriteLine($"Context: {options.ContextLength:N0} tokens");
            Console.WriteLine(options.Suite == BenchmarkSuite.Stitch
                ? "Fixture: deterministic boundary-stitch cases"
                : "Corpus: deterministic 16-segment synthetic book");

            await using var runtime = new NativeRoleRuntime(
                depot,
                new RuntimeOptions(options.ContextLength, options.GpuLayers, PreferGpu: options.GpuLayers != 0),
                roleBindings: BuildRoleBindings(researcher, reviewer));
            if (options.Suite == BenchmarkSuite.Stitch)
            {
                var stitchReport = await new ContextFabricBenchmarkExpansionRunner(runtime, runOptions)
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

            var runner = new ContextFabricFeasibilityRunner(runtime, runOptions);
            var report = (await runner.RunAsync(DeterministicFabricCorpus.Create()).ConfigureAwait(false)) with
            {
                Environment = benchmarkEnvironment,
            };
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
        var context = 8192;
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
                default: throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return new CliOptions(modelRoot, output, context, responseReserve, readerMax, reducerMax, answerMax, gpuLayers, suite);
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
        _ => throw new ArgumentException("Unknown suite. Use cf0, quote-anchor, or stitch."),
    };

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: context-fabric-bench --model-root <folder> [options]");
        Console.WriteLine("  --suite <name>            cf0 | quote-anchor | stitch (default cf0)");
        Console.WriteLine("  --output <folder>          Report directory (default .orc/context-fabric/benchmarks)");
        Console.WriteLine("  --context <tokens>        Native context length (default 8192)");
        Console.WriteLine("  --response-reserve <n>    Reserved response tokens (default 1536)");
        Console.WriteLine("  --reader-max <n>          Reader output limit (default 1024)");
        Console.WriteLine("  --reducer-max <n>         Reducer output limit (default 768)");
        Console.WriteLine("  --answer-max <n>          Answer output limit (default 1536)");
        Console.WriteLine("  --gpu-layers <n>          LLamaSharp GPU layers; 0 forces CPU (default -1)");
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
        BenchmarkSuite Suite);
}
