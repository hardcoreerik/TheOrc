// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;

namespace OrchestratorIDE.Services.Data;

/// <summary>
/// One-shot, idempotent backfill of the SQLite tables from the existing on-disk files.
/// Safe to re-run: every row is upserted on its natural key, and triage review state is
/// preserved (see TriageRepository). Files remain the canonical source during Phase 1.
///
///   captures ← {root}/.orc/swarm/dataset-staging/plan_capture_*.json
///   triage   ← {root}/training_pit/batch_*_triage.tsv
/// </summary>
public sealed class MetadataImporter
{
    private readonly string             _root;
    private readonly CaptureRepository  _captures;
    private readonly TriageRepository   _triage;
    private readonly PlanRepository?    _plans;
    private readonly DatasetRepository? _datasets;

    public MetadataImporter(string workspaceRoot, CaptureRepository captures,
        TriageRepository triage, PlanRepository? plans = null, DatasetRepository? datasets = null)
    {
        _root     = workspaceRoot;
        _captures = captures;
        _triage   = triage;
        _plans    = plans;
        _datasets = datasets;
    }

    public sealed record Result(
        int Captures, int CaptureErrors,
        int Triage,   int TriageErrors,
        int Plans,    int PlanErrors,
        int Datasets, int DatasetErrors);

    public Result ImportAll()
    {
        var (cOk, cErr) = ImportCaptures();
        var (tOk, tErr) = ImportTriage();
        var (pOk, pErr) = ImportPlans();
        var (dOk, dErr) = ImportDatasets();
        return new Result(cOk, cErr, tOk, tErr, pOk, pErr, dOk, dErr);
    }

    // ── captures ─────────────────────────────────────────────────────────────────

    private (int ok, int err) ImportCaptures()
    {
        var dir = Path.Combine(_root, ".orc", "swarm", "dataset-staging");
        if (!Directory.Exists(dir)) return (0, 0);

        int ok = 0, err = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "plan_capture_*.json"))
        {
            try
            {
                var rec = ParseCapture(file);
                if (rec is null) { err++; continue; }
                _captures.Upsert(rec);
                ok++;
            }
            catch { err++; }
        }
        return (ok, err);
    }

    private static CaptureRecord? ParseCapture(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;

        var exampleId = Str(root, "example_id");
        if (string.IsNullOrEmpty(exampleId)) return null;   // example_id is the natural key

        return new CaptureRecord(
            ExampleId:    exampleId,
            RunId:        Str(root, "run_id")      ?? "",
            CapturedAt:   Str(root, "captured_at") ?? "",
            Source:       Str(root, "source")      ?? "",
            BossModel:    Str(root, "boss_model")  ?? "",
            Goal:         Str(root, "goal")        ?? "",
            Domain:       Str(root, "domain"),
            Difficulty:   Int(root, "difficulty"),
            QualityScore: Int(root, "quality_score") ?? 0,
            ExampleClass: Str(root, "example_class"),
            FailureMode:  Str(root, "failure_mode"),
            PlanJson:     RawOrNull(root, "plan"),
            RubricJson:   RawOrNull(root, "rubric_scores"),
            Annotator:    Str(root, "annotator") ?? "auto",
            Notes:        Str(root, "notes")     ?? "",
            SourceFile:   file);
    }

    // ── triage ───────────────────────────────────────────────────────────────────

    private (int ok, int err) ImportTriage()
    {
        var dir = Path.Combine(_root, "training_pit");
        if (!Directory.Exists(dir)) return (0, 0);

        int ok = 0, err = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "batch_*_triage.tsv"))
        {
            var batchId = BatchIdFromFile(file);
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { err++; continue; }
            if (lines.Length < 2) continue;

            var header = lines[0].Split('\t');
            int iRisk  = IndexOf(header, "risk");
            int iId    = IndexOf(header, "id");
            int iScore = IndexOf(header, "score");
            int iFile  = IndexOf(header, "file");
            int iSum   = IndexOf(header, "summary");
            if (iRisk < 0 || iId < 0) { err++; continue; }   // unrecognized layout

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var col = lines[i].Split('\t');
                    var captureRef = At(col, iId);
                    if (string.IsNullOrEmpty(captureRef)) { err++; continue; }

                    _triage.Upsert(new TriageRecord(
                        CaptureRef: captureRef,
                        BatchId:    batchId,
                        Risk:       (At(col, iRisk) ?? "").Trim().ToUpperInvariant(),
                        Score:      ParseInt(At(col, iScore)),
                        Rationale:  At(col, iSum),
                        SourceFile: At(col, iFile) ?? file));
                    ok++;
                }
                catch { err++; }
            }
        }
        return (ok, err);
    }

    /// <summary>batch_NH260614_022050c6_triage.tsv → NH260614_022050c6</summary>
    private static string BatchIdFromFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);   // batch_<id>_triage
        if (name.StartsWith("batch_")) name = name["batch_".Length..];
        if (name.EndsWith("_triage"))  name = name[..^"_triage".Length];
        return name;
    }

    // ── datasets ─────────────────────────────────────────────────────────────────

    private (int ok, int err) ImportDatasets()
    {
        if (_datasets is null) return (0, 0);
        int ok = 0, err = 0;
        try
        {
            // Capture scan timestamp before any I/O so all rows in this batch share it.
            // After upserting, PruneOlderThan removes rows for deleted/renamed files.
            // LoadDatasets also fires TryIndexDatasets internally (using the static
            // DatasetRepo), but that is fine — the explicit _datasets upserts here are
            // the authoritative write for the backfill path.
            var scanTs = DateTime.UtcNow.ToString("o");
            var list   = Services.TrainingPitRegistry.LoadDatasets(_root);
            foreach (var di in list)
            {
                try
                {
                    _datasets.Upsert(new DatasetRecord(
                        FilePath:        di.FilePath,
                        Name:            di.Name,
                        Source:          di.Source,
                        Context:         di.Context,
                        DataType:        di.DataType,
                        Role:            di.Role,
                        IsNewConvention: di.IsNewConvention,
                        InProgress:      di.InProgress,
                        TrainCount:      di.TrainCount,
                        EvalCount:       di.EvalCount,
                        TotalCount:      di.TotalCount,
                        LastModified:    di.LastModified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        IndexedAt:       scanTs));
                    ok++;
                }
                catch { err++; }
            }
            // Prune phantom rows (files deleted/renamed since last scan).
            try { _datasets.PruneOlderThan(scanTs); } catch { }
        }
        catch { err++; }
        return (ok, err);
    }

    // ── plans ────────────────────────────────────────────────────────────────────

    private (int ok, int err) ImportPlans()
    {
        if (_plans is null) return (0, 0);
        var dir = Path.Combine(_root, "training_pit", "plans");
        if (!Directory.Exists(dir)) return (0, 0);

        int ok = 0, err = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "plan_*.json"))
        {
            try
            {
                var rec = ParsePlan(file);
                if (rec is null) { err++; continue; }
                _plans.Upsert(rec);
                ok++;
            }
            catch { err++; }
        }
        return (ok, err);
    }

    private static PlanRecord? ParsePlan(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;

        var planId = Str(root, "PlanId") ?? Str(root, "plan_id");
        if (string.IsNullOrEmpty(planId)) return null;

        // Languages and TaskMix are stored as JSON arrays/objects in the file;
        // re-serialise the raw JsonElement so they round-trip cleanly.
        string? langsJson  = RawOrNull(root, "Languages")  ?? RawOrNull(root, "languages");
        string? taskMixJson = RawOrNull(root, "TaskMix")   ?? RawOrNull(root, "task_mix");
        string? hiveJson    = RawOrNull(root, "Hive")      ?? RawOrNull(root, "hive");

        // CreatedAt may be a DateTime (C# default serialisation) or ISO string
        string createdAt = Str(root, "CreatedAt") ?? Str(root, "created_at") ?? "";

        return new PlanRecord(
            PlanId:        planId,
            CreatedAt:     createdAt,
            Goal:          Str(root, "Goal")          ?? Str(root, "goal")          ?? "",
            Persona:       Str(root, "Persona")       ?? Str(root, "persona")       ?? "",
            Style:         Str(root, "Style")         ?? Str(root, "style")         ?? "",
            LanguagesJson: langsJson,
            TaskMixJson:   taskMixJson,
            DatasetTarget: Int(root, "DatasetTarget") ?? Int(root, "dataset_target") ?? 0,
            DatasetSource: Str(root, "DatasetSource") ?? Str(root, "dataset_source") ?? "",
            BaseModel:     Str(root, "BaseModel")     ?? Str(root, "base_model")    ?? "",
            AdapterName:   Str(root, "AdapterName")   ?? Str(root, "adapter_name")  ?? "",
            LoraRank:      Int(root, "LoraRank")      ?? Int(root, "lora_rank")      ?? 0,
            Epochs:        Int(root, "Epochs")        ?? Int(root, "epochs")         ?? 0,
            LearningRate:  Real(root, "LearningRate") ?? Real(root, "learning_rate") ?? 0.0,
            Phase:         Str(root, "Phase")         ?? Str(root, "phase")          ?? "",
            DatasetFile:   Str(root, "DatasetFile")   ?? Str(root, "dataset_file")   ?? "",
            AdapterPath:   Str(root, "AdapterPath")   ?? Str(root, "adapter_path")   ?? "",
            HiveJson:      hiveJson,
            Notes:         Str(root, "Notes")         ?? Str(root, "notes")          ?? "");
    }

    // ── JSON / TSV helpers ────────────────────────────────────────────────────────

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : null;

    private static string? RawOrNull(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind is not JsonValueKind.Null
            ? v.GetRawText() : null;

    private static double? Real(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

    private static int IndexOf(string[] header, string col)
    {
        for (int i = 0; i < header.Length; i++)
            if (string.Equals(header[i].Trim(), col, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string? At(string[] cols, int idx)
        => idx >= 0 && idx < cols.Length ? cols[idx] : null;

    private static int? ParseInt(string? s)
        => int.TryParse(s?.Trim(), out var n) ? n : null;
}
