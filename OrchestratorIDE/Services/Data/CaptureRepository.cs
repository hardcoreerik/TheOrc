namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Read/write access to the <c>captures</c> table. All SQL is parameterized via the
/// RepositoryBase choke point — no value is ever concatenated into a statement.
/// </summary>
public sealed class CaptureRepository : RepositoryBase
{
    public CaptureRepository(SqliteStore store) : base(store) { }

    /// <summary>
    /// Idempotent upsert keyed on <c>example_id</c>. Re-importing the same capture
    /// updates it in place rather than duplicating.
    /// </summary>
    public void Upsert(CaptureRecord c)
    {
        Execute(
            """
            INSERT INTO captures
                (example_id, run_id, captured_at, source, boss_model, goal, domain,
                 difficulty, quality_score, example_class, failure_mode, plan_json,
                 rubric_json, annotator, notes, source_file, imported_at)
            VALUES
                ($example_id, $run_id, $captured_at, $source, $boss_model, $goal, $domain,
                 $difficulty, $quality_score, $example_class, $failure_mode, $plan_json,
                 $rubric_json, $annotator, $notes, $source_file, $imported_at)
            ON CONFLICT(example_id) DO UPDATE SET
                run_id        = excluded.run_id,
                captured_at   = excluded.captured_at,
                source        = excluded.source,
                boss_model    = excluded.boss_model,
                goal          = excluded.goal,
                domain        = excluded.domain,
                difficulty    = excluded.difficulty,
                quality_score = excluded.quality_score,
                example_class = excluded.example_class,
                failure_mode  = excluded.failure_mode,
                plan_json     = excluded.plan_json,
                rubric_json   = excluded.rubric_json,
                annotator     = excluded.annotator,
                notes         = excluded.notes,
                source_file   = excluded.source_file,
                imported_at   = excluded.imported_at
            """,
            ps =>
            {
                P(ps, "$example_id",    c.ExampleId);
                P(ps, "$run_id",        c.RunId);
                P(ps, "$captured_at",   c.CapturedAt);
                P(ps, "$source",        c.Source);
                P(ps, "$boss_model",    c.BossModel);
                P(ps, "$goal",          c.Goal);
                P(ps, "$domain",        c.Domain);
                P(ps, "$difficulty",    c.Difficulty);
                P(ps, "$quality_score", c.QualityScore);
                P(ps, "$example_class", c.ExampleClass);
                P(ps, "$failure_mode",  c.FailureMode);
                P(ps, "$plan_json",     c.PlanJson);
                P(ps, "$rubric_json",   c.RubricJson);
                P(ps, "$annotator",     c.Annotator);
                P(ps, "$notes",         c.Notes);
                P(ps, "$source_file",   c.SourceFile);
                P(ps, "$imported_at",   DateTime.UtcNow.ToString("o"));
            });
    }

    /// <summary>Total capture count.</summary>
    public int Count()
        => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM captures") ?? 0);

    /// <summary>Count by example_class (e.g. "positive"/"negative").</summary>
    public int CountByClass(string exampleClass)
        => Convert.ToInt32(
            Scalar("SELECT COUNT(*) FROM captures WHERE example_class = $c",
                ps => P(ps, "$c", exampleClass)) ?? 0);

    /// <summary>Captures at or above a quality score, newest first.</summary>
    public List<CaptureRecord> ByMinScore(int minScore)
        => Query(
            """
            SELECT example_id, run_id, captured_at, source, boss_model, goal, domain,
                   difficulty, quality_score, example_class, failure_mode, plan_json,
                   rubric_json, annotator, notes, source_file
            FROM captures
            WHERE quality_score >= $min
            ORDER BY captured_at DESC
            """,
            Map,
            ps => P(ps, "$min", minScore));

    private static CaptureRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        ExampleId:    GetStr(r, "example_id")  ?? "",
        RunId:        GetStr(r, "run_id")      ?? "",
        CapturedAt:   GetStr(r, "captured_at") ?? "",
        Source:       GetStr(r, "source")      ?? "",
        BossModel:    GetStr(r, "boss_model")  ?? "",
        Goal:         GetStr(r, "goal")        ?? "",
        Domain:       GetStr(r, "domain"),
        Difficulty:   GetInt(r, "difficulty"),
        QualityScore: GetInt(r, "quality_score") ?? 0,
        ExampleClass: GetStr(r, "example_class"),
        FailureMode:  GetStr(r, "failure_mode"),
        PlanJson:     GetStr(r, "plan_json"),
        RubricJson:   GetStr(r, "rubric_json"),
        Annotator:    GetStr(r, "annotator")   ?? "auto",
        Notes:        GetStr(r, "notes")       ?? "",
        SourceFile:   GetStr(r, "source_file") ?? "");
}
