namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Read/write access to the <c>datasets</c> table. All SQL is parameterized via RepositoryBase.
///
/// This is an index/cache over training_pit/datasets/*.jsonl — files remain canonical.
/// Rows are refreshed every time TrainingPitRegistry.LoadDatasets runs (dual-write),
/// so indexed_at tracks freshness. Call <see cref="IsEmpty"/> to decide whether to
/// fall back to a file scan.
/// </summary>
public sealed class DatasetRepository : RepositoryBase
{
    public DatasetRepository(SqliteStore store) : base(store) { }

    /// <summary>
    /// Idempotent upsert keyed on <c>file_path</c>. Updates all fields including
    /// indexed_at so callers can detect stale entries.
    /// </summary>
    public void Upsert(DatasetRecord d)
    {
        Execute(
            """
            INSERT INTO datasets
                (file_path, name, source, context, data_type, role,
                 is_new_convention, in_progress, train_count, eval_count,
                 total_count, last_modified, indexed_at)
            VALUES
                ($file_path, $name, $source, $context, $data_type, $role,
                 $is_new, $in_progress, $train_count, $eval_count,
                 $total_count, $last_modified, $indexed_at)
            ON CONFLICT(file_path) DO UPDATE SET
                name              = excluded.name,
                source            = excluded.source,
                context           = excluded.context,
                data_type         = excluded.data_type,
                role              = excluded.role,
                is_new_convention = excluded.is_new_convention,
                in_progress       = excluded.in_progress,
                train_count       = excluded.train_count,
                eval_count        = excluded.eval_count,
                total_count       = excluded.total_count,
                last_modified     = excluded.last_modified,
                indexed_at        = excluded.indexed_at
            """,
            ps =>
            {
                P(ps, "$file_path",    d.FilePath);
                P(ps, "$name",         d.Name);
                P(ps, "$source",       d.Source);
                P(ps, "$context",      d.Context);
                P(ps, "$data_type",    d.DataType);
                P(ps, "$role",         d.Role);
                P(ps, "$is_new",       d.IsNewConvention ? 1 : 0);
                P(ps, "$in_progress",  d.InProgress      ? 1 : 0);
                P(ps, "$train_count",  d.TrainCount);
                P(ps, "$eval_count",   d.EvalCount);
                P(ps, "$total_count",  d.TotalCount);
                P(ps, "$last_modified", d.LastModified);
                P(ps, "$indexed_at",   d.IndexedAt);
            });
    }

    /// <summary>All indexed datasets, newest-modified first.</summary>
    public List<DatasetRecord> LoadAll()
        => Query(
            """
            SELECT file_path, name, source, context, data_type, role,
                   is_new_convention, in_progress, train_count, eval_count,
                   total_count, last_modified, indexed_at
            FROM datasets ORDER BY last_modified DESC
            """,
            Map);

    /// <summary>Datasets from a specific source (e.g. "cerebras", "hardcorepc").</summary>
    public List<DatasetRecord> BySource(string source)
        => Query(
            """
            SELECT file_path, name, source, context, data_type, role,
                   is_new_convention, in_progress, train_count, eval_count,
                   total_count, last_modified, indexed_at
            FROM datasets WHERE source = $source ORDER BY last_modified DESC
            """,
            Map,
            ps => P(ps, "$source", source));

    /// <summary>Datasets filtered by role (e.g. "boss").</summary>
    public List<DatasetRecord> ByRole(string role)
        => Query(
            """
            SELECT file_path, name, source, context, data_type, role,
                   is_new_convention, in_progress, train_count, eval_count,
                   total_count, last_modified, indexed_at
            FROM datasets WHERE role = $role ORDER BY last_modified DESC
            """,
            Map,
            ps => P(ps, "$role", role));

    /// <summary>Total indexed dataset count.</summary>
    public int Count()
        => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM datasets") ?? 0);

    /// <summary>True if the index has no rows — use as a cue to fall back to file scan.</summary>
    public bool IsEmpty() => Count() == 0;

    private static DatasetRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        FilePath:        GetStr(r, "file_path")   ?? "",
        Name:            GetStr(r, "name")         ?? "",
        Source:          GetStr(r, "source")       ?? "",
        Context:         GetStr(r, "context")      ?? "",
        DataType:        GetStr(r, "data_type")    ?? "",
        Role:            GetStr(r, "role")         ?? "",
        IsNewConvention: (GetInt(r, "is_new_convention") ?? 0) == 1,
        InProgress:      (GetInt(r, "in_progress")       ?? 0) == 1,
        TrainCount:      GetInt(r, "train_count")  ?? 0,
        EvalCount:       GetInt(r, "eval_count")   ?? 0,
        TotalCount:      GetInt(r, "total_count")  ?? 0,
        LastModified:    GetStr(r, "last_modified") ?? "",
        IndexedAt:       GetStr(r, "indexed_at")   ?? "");
}
