using System.Net.Http;
using System.Text.Json;

namespace OrchestratorIDE.Services;

/// <summary>
/// Read-only registry over Training Pit assets: datasets on disk, trained
/// LoRA adapters, and Ollama models available on the host. Used by the
/// Training Pit panel to render the Datasets / Adapters / Models row.
/// </summary>
public static class TrainingPitRegistry
{
    // ── DTOs ─────────────────────────────────────────────────────────────

    public sealed class DatasetInfo
    {
        public string Name { get; init; } = "";       // e.g. "v1"
        public string Path { get; init; } = "";        // dir holding the JSONLs
        public int TrainCount { get; init; }
        public int EvalCount  { get; init; }
        public int NegCount   { get; init; }
        public DateTime LastModified { get; init; }
        public string Notes { get; init; } = "";       // free text description
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

    // ── Loaders ──────────────────────────────────────────────────────────

    /// <summary>Scan training_pit/datasets for *.jsonl files, group by
    /// version stem (train_v1 / eval_v1 / negative_v1 share "v1").</summary>
    public static List<DatasetInfo> LoadDatasets(string pitRoot)
    {
        var dir = System.IO.Path.Combine(pitRoot, "training_pit", "datasets");
        if (!Directory.Exists(dir)) return [];

        var jsonls = Directory.GetFiles(dir, "*.jsonl");
        var groups = new Dictionary<string, DatasetInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in jsonls)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(f);
            // Names look like train_v1, eval_v1, negative_v1 — version is the suffix
            var parts = name.Split('_', 2);
            var split = parts[0].ToLowerInvariant();
            var stem  = parts.Length > 1 ? parts[1] : name;

            var lines = 0;
            try { lines = File.ReadLines(f).Count(); } catch { }
            var last = File.GetLastWriteTime(f);

            if (!groups.TryGetValue(stem, out var info))
            {
                info = new DatasetInfo { Name = stem, Path = dir, LastModified = last };
                groups[stem] = info;
            }

            // We can't mutate init-only props after construction, so rebuild.
            groups[stem] = new DatasetInfo
            {
                Name         = info.Name,
                Path         = info.Path,
                TrainCount   = split == "train"    ? lines : info.TrainCount,
                EvalCount    = split == "eval"     ? lines : info.EvalCount,
                NegCount     = split == "negative" ? lines : info.NegCount,
                LastModified = last > info.LastModified ? last : info.LastModified,
                Notes        = info.Notes,
            };
        }

        return groups.Values.OrderByDescending(d => d.LastModified).ToList();
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
