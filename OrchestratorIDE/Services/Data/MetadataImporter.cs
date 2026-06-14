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
    private readonly string            _root;
    private readonly CaptureRepository _captures;
    private readonly TriageRepository  _triage;

    public MetadataImporter(string workspaceRoot, CaptureRepository captures, TriageRepository triage)
    {
        _root     = workspaceRoot;
        _captures = captures;
        _triage   = triage;
    }

    public sealed record Result(int Captures, int CaptureErrors, int Triage, int TriageErrors);

    public Result ImportAll()
    {
        var (cOk, cErr) = ImportCaptures();
        var (tOk, tErr) = ImportTriage();
        return new Result(cOk, cErr, tOk, tErr);
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
