namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Read/write access to the <c>plans</c> table. All SQL is parameterized via RepositoryBase.
///
/// Re-import safety: upsert updates all fields on conflict because plan execution state
/// (Phase, DatasetFile, AdapterPath) is mutable and must stay current in SQL.
/// </summary>
public sealed class PlanRepository : RepositoryBase
{
    public PlanRepository(SqliteStore store) : base(store) { }

    /// <summary>Idempotent upsert keyed on <c>plan_id</c>. Updates all fields on re-import.</summary>
    public void Upsert(PlanRecord p)
    {
        Execute(
            """
            INSERT INTO plans
                (plan_id, created_at, goal, persona, style, languages_json, task_mix_json,
                 dataset_target, dataset_source, base_model, adapter_name, lora_rank,
                 epochs, learning_rate, phase, dataset_file, adapter_path, hive_json, notes)
            VALUES
                ($plan_id, $created_at, $goal, $persona, $style, $languages_json, $task_mix_json,
                 $dataset_target, $dataset_source, $base_model, $adapter_name, $lora_rank,
                 $epochs, $learning_rate, $phase, $dataset_file, $adapter_path, $hive_json, $notes)
            ON CONFLICT(plan_id) DO UPDATE SET
                goal           = excluded.goal,
                persona        = excluded.persona,
                style          = excluded.style,
                languages_json = excluded.languages_json,
                task_mix_json  = excluded.task_mix_json,
                dataset_target = excluded.dataset_target,
                dataset_source = excluded.dataset_source,
                base_model     = excluded.base_model,
                adapter_name   = excluded.adapter_name,
                lora_rank      = excluded.lora_rank,
                epochs         = excluded.epochs,
                learning_rate  = excluded.learning_rate,
                phase          = excluded.phase,
                dataset_file   = excluded.dataset_file,
                adapter_path   = excluded.adapter_path,
                hive_json      = excluded.hive_json,
                notes          = excluded.notes
            """,
            ps =>
            {
                P(ps, "$plan_id",         p.PlanId);
                P(ps, "$created_at",      p.CreatedAt);
                P(ps, "$goal",            p.Goal);
                P(ps, "$persona",         p.Persona);
                P(ps, "$style",           p.Style);
                P(ps, "$languages_json",  p.LanguagesJson);
                P(ps, "$task_mix_json",   p.TaskMixJson);
                P(ps, "$dataset_target",  p.DatasetTarget);
                P(ps, "$dataset_source",  p.DatasetSource);
                P(ps, "$base_model",      p.BaseModel);
                P(ps, "$adapter_name",    p.AdapterName);
                P(ps, "$lora_rank",       p.LoraRank);
                P(ps, "$epochs",          p.Epochs);
                P(ps, "$learning_rate",   p.LearningRate);
                P(ps, "$phase",           p.Phase);
                P(ps, "$dataset_file",    p.DatasetFile);
                P(ps, "$adapter_path",    p.AdapterPath);
                P(ps, "$hive_json",       p.HiveJson);
                P(ps, "$notes",           p.Notes);
            });
    }

    /// <summary>All plans, newest first.</summary>
    public List<PlanRecord> LoadAll()
        => Query(
            "SELECT plan_id, created_at, goal, persona, style, languages_json, task_mix_json, " +
            "dataset_target, dataset_source, base_model, adapter_name, lora_rank, " +
            "epochs, learning_rate, phase, dataset_file, adapter_path, hive_json, notes " +
            "FROM plans ORDER BY created_at DESC",
            Map);

    /// <summary>Plans filtered by phase name (e.g. "Ready", "Complete").</summary>
    public List<PlanRecord> ByPhase(string phase)
        => Query(
            "SELECT plan_id, created_at, goal, persona, style, languages_json, task_mix_json, " +
            "dataset_target, dataset_source, base_model, adapter_name, lora_rank, " +
            "epochs, learning_rate, phase, dataset_file, adapter_path, hive_json, notes " +
            "FROM plans WHERE phase = $phase ORDER BY created_at DESC",
            Map,
            ps => P(ps, "$phase", phase));

    /// <summary>Total plan count.</summary>
    public int Count()
        => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM plans") ?? 0);

    private static PlanRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        PlanId:        GetStr(r, "plan_id")        ?? "",
        CreatedAt:     GetStr(r, "created_at")     ?? "",
        Goal:          GetStr(r, "goal")           ?? "",
        Persona:       GetStr(r, "persona")        ?? "",
        Style:         GetStr(r, "style")          ?? "",
        LanguagesJson: GetStr(r, "languages_json"),
        TaskMixJson:   GetStr(r, "task_mix_json"),
        DatasetTarget: GetInt(r, "dataset_target") ?? 0,
        DatasetSource: GetStr(r, "dataset_source") ?? "",
        BaseModel:     GetStr(r, "base_model")     ?? "",
        AdapterName:   GetStr(r, "adapter_name")   ?? "",
        LoraRank:      GetInt(r, "lora_rank")      ?? 0,
        Epochs:        GetInt(r, "epochs")         ?? 0,
        LearningRate:  GetReal(r, "learning_rate") ?? 0.0,
        Phase:         GetStr(r, "phase")          ?? "",
        DatasetFile:   GetStr(r, "dataset_file")   ?? "",
        AdapterPath:   GetStr(r, "adapter_path")   ?? "",
        HiveJson:      GetStr(r, "hive_json"),
        Notes:         GetStr(r, "notes")          ?? "");
}
