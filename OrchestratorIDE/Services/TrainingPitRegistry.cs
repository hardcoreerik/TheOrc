using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services;

/// <summary>
/// Read-only registry over Training Pit assets: datasets on disk, trained
/// LoRA adapters, and Ollama models available on the host. Used by the
/// Training Pit panel to render the Datasets / Adapters / Models row.
/// </summary>
public static class TrainingPitRegistry
{
    // Matches the naming convention: {source}[{context}].{type}.{role}.{count}
    // e.g. cerebras[api].synthetic.boss.1800
    //      mainpc[24gb].captured.boss.1384
    //      merged[mixed].normalized.boss.2244
    private static readonly Regex _newConventionRe = new(
        @"^(?<source>[^[\]]+)\[(?<ctx>[^\]]+)\]\.(?<type>[^.]+)\.(?<role>[^.]+)\.(?<n>\d+)$",
        RegexOptions.Compiled);

    // ── DTOs ─────────────────────────────────────────────────────────────

    public sealed class DatasetInfo
    {
        public string Name         { get; init; } = "";  // display name
        public string FilePath     { get; init; } = "";  // full path to file (new conv) or dir (old)
        public string Source       { get; init; } = "";  // e.g. "cerebras", "mainpc", "merged"
        public string Context      { get; init; } = "";  // e.g. "api", "24gb", "mixed"
        public string DataType     { get; init; } = "";  // e.g. "synthetic", "captured", "normalized"
        public string Role         { get; init; } = "";  // e.g. "boss"
        public bool   IsNewConvention { get; init; }
        public bool   InProgress   { get; init; }        // *.work.jsonl — still generating
        public int    TrainCount   { get; init; }        // examples available for training
        public int    EvalCount    { get; init; }
        public int    NegCount     { get; init; }
        public int    TotalCount   { get; init; }        // total lines in file
        public DateTime LastModified { get; init; }
        public string Notes        { get; init; } = "";
    }

    public sealed class AdapterInfo
    {
        public string Name { get; init; } = "";        // dir name, e.g. "lora_v1"
        public string Path { get; init; } = "";
        public string BaseModel { get; init; } = "";
        public int TrainExamples { get; init; }
        public int EvalExamples  { get; init; }
        public double? EvalLoss { get; init; }
        public DateTime Finished { get; init; }
        public double Minutes { get; init; }
        public string Tier { get; init; } = "Experimental";   // Experimental | Promoted | Trusted
        public bool MergedReady { get; init; }
        public bool AbEvalReady { get; init; }
    }

    public sealed class OllamaModelInfo
    {
        public string Name { get; init; } = "";
        public double SizeGb { get; init; }
        public DateTime Modified { get; init; }
    }

    public sealed class ReviewCapturesInfo
    {
        public int Count { get; init; }
        public DateTime LatestAt { get; init; }
        public string LatestSummary { get; init; } = "";
    }

    // ── Phase 3 SQL index ────────────────────────────────────────────────

    /// <summary>
    /// Optional SQL dataset-index target (Phase 3). Set once at app startup.
    /// When non-null, every LoadDatasets scan also upserts each result row.
    /// Best-effort: a DB failure never affects the file scan result.
    /// </summary>
    public static Data.DatasetRepository? DatasetRepo { get; set; }

    // ── Loaders ──────────────────────────────────────────────────────────

    /// <summary>
    /// Scan training_pit/datasets for *.jsonl files. Understands two conventions:
    ///
    /// New: {source}[{ctx}].{type}.{role}.{n}.jsonl — each file is its own entry.
    ///      e.g. cerebras[api].synthetic.boss.1800.jsonl
    ///
    /// Old: train_{stem}.jsonl + eval_{stem}.jsonl + negative_{stem}.jsonl
    ///      grouped into one entry keyed by stem, e.g. stem="v1".
    ///
    /// Work files (*.work.jsonl) appear as in-progress entries.
    /// Drops to a Refresh-button-driven call on the UI side — no recompile needed.
    /// </summary>
    public static List<DatasetInfo> LoadDatasets(string pitRoot)
    {
        // Capture the repo reference BEFORE any I/O — if the workspace switches
        // mid-scan, this reference stays bound to the workspace that launched the scan.
        var capturedRepo = DatasetRepo;

        var dir = System.IO.Path.Combine(pitRoot, "training_pit", "datasets");
        if (!Directory.Exists(dir)) return [];

        var results = new List<DatasetInfo>();
        // Old-convention groups: stem → mutable aggregate
        var oldGroups = new Dictionary<string, (int train, int eval, int neg, DateTime last)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in Directory.GetFiles(dir, "*.jsonl").OrderByDescending(File.GetLastWriteTime))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(f);
            var last = File.GetLastWriteTime(f);

            // ── in-progress work files ─────────────────────────────────
            if (stem.EndsWith(".work", StringComparison.OrdinalIgnoreCase))
            {
                var baseStem  = stem[..^5]; // strip ".work"
                var lineCount = CountLines(f);
                var m2        = _newConventionRe.Match(baseStem);
                results.Add(new DatasetInfo
                {
                    Name          = baseStem,
                    FilePath      = f,
                    Source        = m2.Success ? m2.Groups["source"].Value : "unknown",
                    Context       = m2.Success ? m2.Groups["ctx"].Value    : "",
                    DataType      = m2.Success ? m2.Groups["type"].Value   : "work",
                    Role          = m2.Success ? m2.Groups["role"].Value   : "",
                    IsNewConvention = m2.Success,
                    InProgress    = true,
                    TotalCount    = lineCount,
                    TrainCount    = lineCount,
                    LastModified  = last,
                    Notes         = "⏳ generation in progress",
                });
                continue;
            }

            // ── new naming convention ──────────────────────────────────
            var m = _newConventionRe.Match(stem);
            if (m.Success)
            {
                var n = CountLines(f);
                results.Add(new DatasetInfo
                {
                    Name            = stem,
                    FilePath        = f,
                    Source          = m.Groups["source"].Value,
                    Context         = m.Groups["ctx"].Value,
                    DataType        = m.Groups["type"].Value,
                    Role            = m.Groups["role"].Value,
                    IsNewConvention = true,
                    TotalCount      = n,
                    TrainCount      = n,
                    LastModified    = last,
                });
                continue;
            }

            // ── old naming convention: train_* / eval_* / negative_* ───
            var lowerStem = stem.ToLowerInvariant();
            string? oldKey = null;
            string? split  = null;

            if (lowerStem.StartsWith("train_"))    { oldKey = stem[6..]; split = "train"; }
            else if (lowerStem.StartsWith("eval_")) { oldKey = stem[5..]; split = "eval"; }
            else if (lowerStem.StartsWith("negative_")) { oldKey = stem[9..]; split = "negative"; }

            if (oldKey is not null && split is not null)
            {
                var lines = CountLines(f);
                if (!oldGroups.TryGetValue(oldKey, out var g))
                    g = (0, 0, 0, last);

                oldGroups[oldKey] = split switch
                {
                    "train"    => (lines, g.eval, g.neg,   last > g.last ? last : g.last),
                    "eval"     => (g.train, lines, g.neg,  last > g.last ? last : g.last),
                    "negative" => (g.train, g.eval, lines, last > g.last ? last : g.last),
                    _          => g,
                };
                continue;
            }

            // ── unrecognised / legacy file — show as-is ────────────────
            var total = CountLines(f);
            results.Add(new DatasetInfo
            {
                Name         = stem,
                FilePath     = f,
                DataType     = "legacy",
                TotalCount   = total,
                TrainCount   = total,
                LastModified = last,
            });
        }

        // Flush old-convention groups
        foreach (var (key, g) in oldGroups)
            results.Add(new DatasetInfo
            {
                Name         = key,
                FilePath     = System.IO.Path.Combine(dir, $"train_{key}.jsonl"),
                DataType     = "train+eval",
                TrainCount   = g.train,
                EvalCount    = g.eval,
                NegCount     = g.neg,
                TotalCount   = g.train + g.eval + g.neg,
                LastModified = g.last,
            });

        var sorted = results.OrderByDescending(d => d.LastModified).ToList();

        // Phase 3 dual-write: index into SQL using the pre-captured repo reference
        // so a mid-scan workspace switch cannot contaminate the new workspace's DB.
        TryIndexDatasets(sorted, capturedRepo);

        return sorted;
    }

    private static void TryIndexDatasets(List<DatasetInfo> list, Data.DatasetRepository? repo)
    {
        if (repo is null) return;
        // Single timestamp for the whole batch — rows from this scan get this stamp;
        // PruneOlderThan deletes any rows (deleted/renamed files) with an older stamp.
        var scanTs = DateTime.UtcNow.ToString("o");
        foreach (var di in list)
        {
            try
            {
                repo.Upsert(new Data.DatasetRecord(
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
            }
            catch { /* Best-effort — never affect the file scan result */ }
        }
        try { repo.PruneOlderThan(scanTs); }
        catch { }
    }

    private static int CountLines(string path)
    {
        try { return File.ReadLines(path).Count(l => l.Trim().Length > 0); }
        catch { return 0; }
    }

    /// <summary>Scan training_pit/outputs/* for directories holding
    /// training_summary.json. Each is one trained adapter.</summary>
    public static List<AdapterInfo> LoadAdapters(string pitRoot)
    {
        var dir = System.IO.Path.Combine(pitRoot, "training_pit", "outputs");
        if (!Directory.Exists(dir)) return [];

        var adapters = new List<AdapterInfo>();
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var summaryPath = System.IO.Path.Combine(sub, "training_summary.json");
            if (!File.Exists(summaryPath)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
                var root = doc.RootElement;

                string baseModel = root.TryGetProperty("base_model", out var bm) ? bm.GetString() ?? "" : "";
                int trainEx      = root.TryGetProperty("train_examples", out var tx) ? tx.GetInt32() : 0;
                int evalEx       = root.TryGetProperty("eval_examples", out var ex) ? ex.GetInt32() : 0;
                double? loss     = root.TryGetProperty("eval_loss", out var el) && el.ValueKind == JsonValueKind.Number
                                       ? el.GetDouble() : null;
                double minutes   = root.TryGetProperty("minutes", out var mn) && mn.ValueKind == JsonValueKind.Number
                                       ? mn.GetDouble() : 0;
                DateTime finished = root.TryGetProperty("finished", out var fn) && fn.ValueKind == JsonValueKind.String
                                       && DateTime.TryParse(fn.GetString(), out var dt) ? dt : File.GetLastWriteTime(summaryPath);

                adapters.Add(new AdapterInfo
                {
                    Name           = System.IO.Path.GetFileName(sub),
                    Path           = sub,
                    BaseModel      = baseModel,
                    TrainExamples  = trainEx,
                    EvalExamples   = evalEx,
                    EvalLoss       = loss,
                    Finished       = finished,
                    Minutes        = minutes,
                    Tier           = ReadTier(sub),
                    MergedReady    = Directory.Exists(System.IO.Path.Combine(sub, "merged"))
                                       && Directory.EnumerateFiles(System.IO.Path.Combine(sub, "merged")).Any(),
                    AbEvalReady    = File.Exists(System.IO.Path.Combine(sub, "ab_eval_full_wrapper.json")),
                });
            }
            catch { /* unreadable adapter — skip */ }
        }

        return adapters.OrderByDescending(a => a.Finished).ToList();
    }

    /// <summary>Hit Ollama /api/tags. Returns empty list if Ollama is down.</summary>
    public static async Task<List<OllamaModelInfo>> LoadModelsAsync(string ollamaHost, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync($"{ollamaHost.TrimEnd('/')}/api/tags", ct);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var arr)) return [];

            var list = new List<OllamaModelInfo>();
            foreach (var m in arr.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var size = m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                              ? s.GetInt64() / 1_073_741_824.0 : 0;
                var mod  = m.TryGetProperty("modified_at", out var ma) && ma.ValueKind == JsonValueKind.String
                              && DateTime.TryParse(ma.GetString(), out var dt) ? dt : DateTime.MinValue;
                if (name.Length > 0) list.Add(new OllamaModelInfo { Name = name, SizeGb = size, Modified = mod });
            }
            return list.OrderByDescending(m => m.Modified).ToList();
        }
        catch { return []; }
    }

    /// <summary>Scan .orc/swarm/review-staging for review_capture_*.json files
    /// produced by tools/review-capture.ps1. Each file is one paired
    /// (Codex verdict, TheOrc verdict) training example for the future
    /// theorc-reviewer adapter.</summary>
    public static ReviewCapturesInfo LoadReviewCaptures(string pitRoot)
    {
        var dir = System.IO.Path.Combine(pitRoot, ".orc", "swarm", "review-staging");
        if (!Directory.Exists(dir)) return new ReviewCapturesInfo();

        var files = Directory.GetFiles(dir, "review_capture_*.json");
        if (files.Length == 0) return new ReviewCapturesInfo();

        var latestFile = files.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTime).First();
        var summary = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(latestFile.FullName));
            var root = doc.RootElement;
            var stats = root.TryGetProperty("stats", out var s) ? s.GetString() ?? "" : "";
            var range = root.TryGetProperty("range", out var r) ? r.GetString() ?? "" : "";
            summary = range.Length > 0 ? $"{range} — {stats}" : stats;
        }
        catch { /* unreadable — leave summary blank */ }

        return new ReviewCapturesInfo
        {
            Count         = files.Length,
            LatestAt      = latestFile.LastWriteTime,
            LatestSummary = summary,
        };
    }

    // ── Trust tier sidecar ───────────────────────────────────────────────
    // tier.json lives next to training_summary.json. Missing file = Experimental.
    // Promotion is user-driven from the panel (UI in a later round).

    public static string ReadTier(string adapterDir)
    {
        var path = System.IO.Path.Combine(adapterDir, "tier.json");
        if (!File.Exists(path)) return "Experimental";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("tier", out var t) ? t.GetString() ?? "Experimental" : "Experimental";
        }
        catch { return "Experimental"; }
    }

    public static void WriteTier(string adapterDir, string tier)
    {
        var path = System.IO.Path.Combine(adapterDir, "tier.json");
        var json = JsonSerializer.Serialize(new { tier, updated = DateTime.Now.ToString("u") },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
