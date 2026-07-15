// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.Models;

// ── Storage schema ────────────────────────────────────────────────────────────

/// <summary>Per-model Context Fabric benchmark result. Schema version "model-benchmark-1.0".</summary>
public sealed record ModelBenchmarkRecord(
    string SchemaVersion,
    string ModelFilename,
    string BenchmarkId,
    string BenchmarkTier,
    DateTimeOffset RunDate,
    string Machine,
    string CommitSha,
    bool Passed,
    double QuestionPassRate,
    int QuestionsPassed,
    int QuestionsTotal,
    double CitationPrecision,
    double SegmentCoverage,
    double BoundaryStitchPassRate,
    string? Notes = null)
{
    public const string CurrentSchemaVersion = "model-benchmark-1.0";

    /// <summary>Short display label for the tier (e.g. "Tier 2").</summary>
    [JsonIgnore]
    public string TierLabel => BenchmarkTier.Replace("-", " ");

    /// <summary>GO / NO-GO label for the overall verdict.</summary>
    [JsonIgnore]
    public string VerdictLabel => Passed ? "GO" : "NO-GO";

    /// <summary>Color for the verdict badge.</summary>
    [JsonIgnore]
    public string VerdictColor => Passed ? "#4ACA4A" : "#E84040";

    /// <summary>0–100 rounded question pass percentage.</summary>
    [JsonIgnore]
    public int QuestionPassPct => (int)Math.Round(QuestionPassRate * 100);

    /// <summary>0–100 rounded citation precision percentage.</summary>
    [JsonIgnore]
    public int CitationPrecisionPct => (int)Math.Round(CitationPrecision * 100);

    /// <summary>0–100 rounded segment coverage percentage.</summary>
    [JsonIgnore]
    public int SegmentCoveragePct => (int)Math.Round(SegmentCoverage * 100);
}

// ── Persistent store ──────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes per-model benchmark records to/from
/// %APPDATA%\OrchestratorIDE\model-benchmarks\.
/// Also knows how to scan .orc/adversarial/ CF-7 gate artifacts to build
/// records from existing runs (Phase 1 read-only seeding).
/// </summary>
public static class ModelBenchmarkStore
{
    public static readonly string UserResultsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "model-benchmarks");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented          = true,
        Converters             = { new JsonStringEnumConverter() },
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Load all saved benchmark records from the user results directory.</summary>
    public static List<ModelBenchmarkRecord> LoadSaved()
    {
        var records = new List<ModelBenchmarkRecord>();
        if (!Directory.Exists(UserResultsDir)) return records;
        foreach (var file in Directory.GetFiles(UserResultsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var r = JsonSerializer.Deserialize<ModelBenchmarkRecord>(json, _json);
                if (r != null) records.Add(r);
            }
            catch { /* skip malformed files */ }
        }
        return records;
    }

    /// <summary>Persist a benchmark record. Overwrites any existing record for the same
    /// model + benchmarkId + tier combination.</summary>
    public static void Save(ModelBenchmarkRecord record)
    {
        Directory.CreateDirectory(UserResultsDir);
        var slug = Slug(record.ModelFilename) + "-" + Slug(record.BenchmarkId) + "-" + Slug(record.BenchmarkTier);
        var path = Path.Combine(UserResultsDir, slug + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, _json));
    }

    /// <summary>
    /// Scan a repo root's .orc/adversarial/ directory for CF-7 gate reports and their
    /// matching B3 sub-reports, and return synthesised ModelBenchmarkRecords.
    /// Each gate report is paired with the B3 report whose timestamp is closest to (but
    /// not after) the gate report's timestamp. Records are then de-duplicated by
    /// (modelFilename + tier): only the latest run per pair is returned.
    /// </summary>
    public static List<ModelBenchmarkRecord> ScanAdversarialDir(string repoRoot)
    {
        var adversarialDir = Path.Combine(repoRoot, ".orc", "adversarial");
        if (!Directory.Exists(adversarialDir)) return [];

        // Load all gate reports
        var gateReports = new List<(string GenerationId, string SourceDigest, DateTimeOffset Generated,
            double QPassRate, int QPassed, int QTotal,
            double CitPrec, double SegCov, double StitchRate, bool Passed)>();

        foreach (var file in Directory.GetFiles(adversarialDir, "cf7_gate_*.json"))
        {
            try
            {
                var gate = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file), _json);
                var genId  = gate.GetProperty("generationId").GetString() ?? "";
                var digest = gate.GetProperty("sourceDigest").GetString() ?? "";
                var ts     = gate.GetProperty("generatedUtc").GetDateTimeOffset();
                var passed = false;

                double qPassRate = 0, citPrec = 0, segCov = 0, stitchRate = 0;
                int    qPassed   = 0, qTotal  = 0;

                foreach (var metric in gate.GetProperty("metrics").EnumerateArray())
                {
                    var name = metric.GetProperty("name").GetString();
                    var val  = metric.GetProperty("value").GetDouble();
                    switch (name)
                    {
                        case "question_pass_rate":           qPassRate   = val; break;
                        case "citation_precision":           citPrec     = val; break;
                        case "segment_terminal_coverage":   segCov      = val; break;
                        case "boundary_stitch_pass_rate":   stitchRate  = val; break;
                    }
                    if (name == "question_pass_rate")
                    {
                        var detail = metric.GetProperty("detail").GetString() ?? "";
                        // "56/100 questions passed" → parse numerator/denominator
                        var slash = detail.IndexOf('/');
                        if (slash > 0 && int.TryParse(detail[..slash].Trim(), out var p))
                        {
                            qPassed = p;
                            var rest = detail[(slash + 1)..];
                            var space = rest.IndexOf(' ');
                            if (space > 0 && int.TryParse(rest[..space], out var t)) qTotal = t;
                        }
                    }
                }

                // Overall verdict: read the report's own readyForExpansion rather than
                // re-deriving "all gates pass" here -- that used to be equivalent, but since
                // gates can now be non-blocking (Remediation Phase 2, "Graded capability"
                // gate; see docs/CONTEXT_FABRIC_GRADING_SPEC.md §8.1), re-deriving from a raw
                // gates.All(...) would silently disagree with the actual verdict the moment a
                // non-blocking gate fails on an otherwise-passing run (Grok review, PR #59).
                passed = gate.TryGetProperty("readyForExpansion", out var ready) && ready.GetBoolean();

                gateReports.Add((genId, digest, ts, qPassRate, qPassed, qTotal,
                    citPrec, segCov, stitchRate, passed));
            }
            catch { /* skip malformed gate reports */ }
        }

        // Load B3 sub-reports to extract model names
        var b3Reports = new List<(string GenerationId, string ModelFilename, string FamilyLabel,
            DateTimeOffset Generated)>();

        foreach (var file in Directory.GetFiles(adversarialDir, "cf0_*.json"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file), _json);
                if (!doc.TryGetProperty("schemaVersion", out var sv)) continue;
                var schema = sv.GetString() ?? "";
                if (!schema.StartsWith("cf0-benchmark", StringComparison.Ordinal)) continue;
                if (!doc.TryGetProperty("environment", out var env)) continue;
                if (!env.TryGetProperty("lanes", out var lanes)) continue;

                var genId = doc.GetProperty("generationId").GetString() ?? "";
                var ts    = doc.GetProperty("generatedUtc").GetDateTimeOffset();
                var lane  = lanes.EnumerateArray().FirstOrDefault();
                var model = lane.TryGetProperty("modelDisplayName", out var mn) ? mn.GetString() ?? "" : "";
                var fam   = lane.TryGetProperty("familyLabel",      out var fl) ? fl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(model))
                    b3Reports.Add((genId, model, fam, ts));
            }
            catch { /* skip malformed sub-reports */ }
        }

        // Join each gate to the B3 report whose timestamp is closest to (but not after) the gate.
        // De-dup by (modelFilename + tier): only the latest run per pair is kept.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ModelBenchmarkRecord>();

        foreach (var gate in gateReports.OrderByDescending(g => g.Generated))
        {
            var b3 = b3Reports
                .Where(b => b.GenerationId == gate.GenerationId && b.Generated <= gate.Generated)
                .OrderByDescending(b => b.Generated)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(b3.ModelFilename)) continue;

            var tier = InferTier(gate.QTotal);
            var key  = b3.ModelFilename + "|" + tier;
            if (!seen.Add(key)) continue;

            results.Add(new ModelBenchmarkRecord(
                SchemaVersion:          ModelBenchmarkRecord.CurrentSchemaVersion,
                ModelFilename:          b3.ModelFilename,
                BenchmarkId:            "cf7-gate",
                BenchmarkTier:          tier,
                RunDate:                gate.Generated,
                Machine:                "",
                CommitSha:              "",
                Passed:                 gate.Passed,
                QuestionPassRate:       gate.QPassRate,
                QuestionsPassed:        gate.QPassed,
                QuestionsTotal:         gate.QTotal,
                CitationPrecision:      gate.CitPrec,
                SegmentCoverage:        gate.SegCov,
                BoundaryStitchPassRate: gate.StitchRate));
        }

        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string InferTier(int questionTotal) => questionTotal switch
    {
        >= 150 => "Tier-3",
        >= 100 => "Tier-2",
        >= 50  => "Tier-1.5",
        _      => "Tier-1",
    };

    private static string Slug(string s) =>
        s.ToLowerInvariant()
         .Replace(".gguf", "", StringComparison.Ordinal)
         .Replace(" ", "-", StringComparison.Ordinal)
         .Replace("_", "-", StringComparison.Ordinal)
         .TrimEnd('-');
}
