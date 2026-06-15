// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services;

/// <summary>
/// Drives the Pit Boss training wizard via a local Ollama model.
///
/// Conversation flow:
///   1. OpeningAsync       — streams the opening question (goal)
///   2. NextQuestionAsync  — streams the follow-up given Q&A history (max 8 rounds)
///   3. SynthesizePlanAsync — asks the model to output a structured TrainingPlan JSON
///      from the full conversation, then parses and returns it.
///
/// If Ollama is unreachable the service falls back to a built-in static question tree
/// so the wizard always works (offline/no-model path produces a sane default plan).
/// </summary>
public sealed class PitBossService
{
    // ── Static fallback question tree ────────────────────────────────────────
    private static readonly string[] _fallbackQuestions =
    [
        "What do you want your AI to become better at? (e.g. code review, delegation, a specific language, a persona)",
        "Which programming language or domain is most important? (leave blank for any)",
        "How would you describe the ideal response style? (e.g. terse and technical, verbose and friendly, formal)",
        "How much training data should we generate? (quick ≈ 300  /  standard ≈ 800  /  thorough ≈ 2,000)",
        "Which base model should we fine-tune? (e.g. qwen2.5-coder:14b — press Enter to keep the default)",
        "How long are you willing to let training run? (quick 1 h  /  overnight  /  weekend)",
        "Anything the model should specifically avoid or handle differently from the base?",
        "Does this look right? Type 'yes' to generate the plan, or correct anything above.",
    ];

    private static readonly string _systemPrompt = """
        You are TheOrc Pit Boss — a concise, expert AI training coach embedded in the OrchestratorIDE desktop app.
        Your job is to guide the user through setting up a LoRA fine-tuning plan for a local language model.

        Rules:
        - Ask ONE focused question at a time. Never ask multiple questions in one turn.
        - Keep every response under 120 words.
        - Be encouraging but direct — no filler, no disclaimers.
        - When you have enough information (after ~8 exchanges), output ONLY a JSON block wrapped in
          ```json ... ``` containing the training plan schema described below.
          Do not write anything outside the JSON block when synthesizing.

        TrainingPlan JSON schema:
        {
          "goal":            "one-line plain-English goal",
          "persona":         "system prompt anchor for generated examples",
          "style":           "response style description",
          "languages":       ["csharp", "python", ...],
          "task_mix":        { "code_review": 0.6, "bugfix": 0.3, "docs": 0.1 },
          "dataset_target":  800,
          "dataset_source":  "cerebras",
          "dataset_gen_model": "qwen2.5-coder:14b",
          "base_model":      "qwen2.5-coder:14b",
          "adapter_name":    "lora_csharp_reviewer_v1",
          "lora_rank":       16,
          "epochs":          3,
          "learning_rate":   0.0002,
          "est_dataset_hours": 3.0,
          "est_train_hours":   2.0,
          "notes":           "free text summary"
        }

        Valid task_mix keys: code_review, bugfix, refactor, feature, tests, docs, integration, ui, delegation, persona, explanation
        Valid dataset_source values: cerebras, ollama, manual
        """;

    // ── HTTP ─────────────────────────────────────────────────────────────────
    private readonly HttpClient _http;
    private readonly string     _ollamaHost;
    private readonly string     _model;
    private          bool       _ollamaAvailable = true;

    public PitBossService(string ollamaHost = "http://localhost:11434",
                          string model      = "qwen2.5-coder:14b")
    {
        _ollamaHost = ollamaHost.TrimEnd('/');
        _model      = model;
        _http       = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Streams the opening greeting + first question.</summary>
    public async IAsyncEnumerable<string> OpeningAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        const string opening = "What do you want to train your AI to become better at?\n\n" +
                               "You can describe a skill, a language, a persona, or a goal — " +
                               "anything from \"better C# code reviewer\" to \"delegate tasks like a senior engineer\" " +
                               "to \"respond like a Socratic philosopher\".";

        if (!_ollamaAvailable)
        {
            yield return opening;
            yield break;
        }

        var prompt = "Greet the user warmly in one sentence (max 15 words), then ask question 1: " +
                     "what do they want to train their AI to be better at? " +
                     "Give 4-5 brief example goals as a bulleted list.";

        await foreach (var chunk in StreamAsync([], prompt, ct))
            yield return chunk;
    }

    // ── Follow-up question ────────────────────────────────────────────────────

    /// <summary>
    /// Given the Q&amp;A history so far, streams the next most useful question.
    /// Returns null when enough information has been collected (≥8 rounds).
    /// </summary>
    public async IAsyncEnumerable<string> NextQuestionAsync(
        List<(string role, string text)> history,
        int roundIndex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_ollamaAvailable)
        {
            var fallback = roundIndex < _fallbackQuestions.Length
                ? _fallbackQuestions[roundIndex]
                : "Does everything look right? Type 'yes' to generate the plan.";
            yield return fallback;
            yield break;
        }

        var context = BuildHistoryContext(history);
        var prompt = roundIndex >= 7
            ? $"{context}\n\nYou have enough information. Ask the user to confirm the plan or make final corrections. Be brief (1 sentence)."
            : $"{context}\n\nRound {roundIndex + 1} of 8. Ask ONE clarifying question to fill the most important missing detail in the training plan. Be specific, be brief.";

        await foreach (var chunk in StreamAsync(history, prompt, ct))
            yield return chunk;
    }

    // ── Plan synthesis ────────────────────────────────────────────────────────

    /// <summary>
    /// Synthesizes a TrainingPlan from the full Q&amp;A history.
    /// Returns null on parse failure (caller should surface the raw JSON to the user).
    /// </summary>
    public async Task<(TrainingPlan? Plan, string RawJson)> SynthesizePlanAsync(
        List<(string role, string text)> history,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        if (!_ollamaAvailable)
            return (BuildDefaultPlan(history, workspaceRoot), "{}");

        var context = BuildHistoryContext(history);
        var synthesisPrompt = $"{context}\n\n" +
            "Now synthesize everything into a TrainingPlan JSON block. " +
            "Output ONLY the ```json ... ``` block — no other text.";

        var sb = new StringBuilder();
        await foreach (var chunk in StreamAsync(history, synthesisPrompt, ct))
            sb.Append(chunk);

        var raw = sb.ToString();
        var plan = ParsePlan(raw, workspaceRoot);
        return (plan, raw);
    }

    // ── Plan persistence ──────────────────────────────────────────────────────

    /// <summary>
    /// Optional SQL dual-write target (Phase 2 of the JSON→SQLite migration).
    /// Set once at app startup. When non-null, every SavePlan call also upserts a
    /// PlanRecord. Best-effort: a DB failure never affects the file write.
    /// </summary>
    public static Data.PlanRepository? PlanRepo { get; set; }

    public static void SavePlan(TrainingPlan plan, string pitRoot)
    {
        var dir = Path.Combine(pitRoot, "training_pit", "plans");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, plan.PlanFileName), json);

        // Phase 2 dual-write: mirror into SQL. File stays canonical.
        TryDualWritePlan(plan);
    }

    private static void TryDualWritePlan(TrainingPlan plan)
    {
        var repo = PlanRepo;
        if (repo is null) return;
        try
        {
            repo.Upsert(new Data.PlanRecord(
                PlanId:        plan.PlanId,
                CreatedAt:     plan.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Goal:          plan.Goal,
                Persona:       plan.Persona,
                Style:         plan.Style,
                LanguagesJson: plan.Languages.Count > 0
                                   ? JsonSerializer.Serialize(plan.Languages) : null,
                TaskMixJson:   plan.TaskMix.Count > 0
                                   ? JsonSerializer.Serialize(plan.TaskMix) : null,
                DatasetTarget: plan.DatasetTarget,
                DatasetSource: plan.DatasetSource,
                BaseModel:     plan.BaseModel,
                AdapterName:   plan.AdapterName,
                LoraRank:      plan.LoraRank,
                Epochs:        plan.Epochs,
                LearningRate:  plan.LearningRate,
                Phase:         plan.Phase.ToString(),
                DatasetFile:   plan.DatasetFile,
                AdapterPath:   plan.AdapterPath,
                HiveJson:      plan.Hive is null ? null : JsonSerializer.Serialize(plan.Hive),
                Notes:         plan.Notes));
        }
        catch { /* Best-effort — never propagate DB failures to the caller */ }
    }

    public static List<TrainingPlan> LoadPlans(string pitRoot)
    {
        var dir = Path.Combine(pitRoot, "training_pit", "plans");
        if (!Directory.Exists(dir)) return [];

        var plans = new List<TrainingPlan>();
        foreach (var f in Directory.GetFiles(dir, "plan_*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<TrainingPlan>(File.ReadAllText(f));
                if (p is not null) plans.Add(p);
            }
            catch { }
        }
        return plans.OrderByDescending(p => p.CreatedAt).ToList();
    }

    // ── Ollama streaming ──────────────────────────────────────────────────────

    private async IAsyncEnumerable<string> StreamAsync(
        List<(string role, string text)> history,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content = _systemPrompt } };
        foreach (var (role, text) in history)
            messages.Add(new { role, content = text });
        messages.Add(new { role = "user", content = userPrompt });

        var body = JsonSerializer.Serialize(new
        {
            model    = _model,
            messages,
            stream   = true,
            options  = new { temperature = 0.5, num_ctx = 8192 },
        });

        // Perform the HTTP call outside the iterator body so we can use try/catch.
        HttpResponseMessage? resp = await PostSafeAsync(body, ct);
        if (resp is null)
        {
            yield return _fallbackQuestions[0];
            yield break;
        }

        using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader     = new System.IO.StreamReader(httpStream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? chunk = ParseChunk(line);
            if (chunk is not null)
                yield return chunk;

            // Check "done" flag
            if (IsDone(line)) break;
        }
    }

    private async Task<HttpResponseMessage?> PostSafeAsync(string body, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsync(
                $"{_ollamaHost}/api/chat",
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);
            if (!resp.IsSuccessStatusCode) { _ollamaAvailable = false; return null; }
            return resp;
        }
        catch
        {
            _ollamaAvailable = false;
            return null;
        }
    }

    private static string? ParseChunk(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
        }
        catch { return null; }
    }

    private static bool IsDone(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("done", out var d) && d.GetBoolean();
        }
        catch { return false; }
    }

    // ── JSON plan parsing ─────────────────────────────────────────────────────

    private static TrainingPlan? ParsePlan(string raw, string workspaceRoot)
    {
        // Extract the ```json ... ``` block
        var start = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        var end   = start >= 0 ? raw.IndexOf("```", start + 7) : -1;
        var json  = start >= 0 && end > start ? raw[(start + 7)..end].Trim() : raw.Trim();

        try
        {
            using var doc   = JsonDocument.Parse(json);
            var root        = doc.RootElement;
            string Get(string key, string def = "")
                => root.TryGetProperty(key, out var v) ? v.GetString() ?? def : def;
            int GetI(string key, int def)
                => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
            double GetD(string key, double def)
                => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

            var langs = new List<string>();
            if (root.TryGetProperty("languages", out var la))
                foreach (var l in la.EnumerateArray())
                    langs.Add(l.GetString() ?? "");

            var mix = new Dictionary<string, double>();
            if (root.TryGetProperty("task_mix", out var tm))
                foreach (var kv in tm.EnumerateObject())
                    mix[kv.Name] = kv.Value.GetDouble();

            var adapterName = Get("adapter_name");
            if (string.IsNullOrWhiteSpace(adapterName))
                adapterName = $"lora_custom_{DateTime.Now:yyyyMMdd}";

            return new TrainingPlan
            {
                Goal            = Get("goal"),
                Persona         = Get("persona"),
                Style           = Get("style"),
                Languages       = langs,
                TaskMix         = mix,
                DatasetTarget   = GetI("dataset_target", 800),
                DatasetSource   = Get("dataset_source", "cerebras"),
                DatasetGenModel = Get("dataset_gen_model", "qwen2.5-coder:14b"),
                BaseModel       = Get("base_model", "qwen2.5-coder:14b"),
                AdapterName     = adapterName,
                LoraRank        = GetI("lora_rank", 16),
                Epochs          = GetI("epochs", 3),
                LearningRate    = GetD("learning_rate", 2e-4),
                EstDatasetHours = GetD("est_dataset_hours", 0),
                EstTrainHours   = GetD("est_train_hours",   0),
                Notes           = Get("notes"),
            };
        }
        catch { return null; }
    }

    private static string BuildHistoryContext(List<(string role, string text)> history)
    {
        var sb = new StringBuilder("Conversation so far:\n");
        foreach (var (role, text) in history)
            sb.AppendLine($"{(role == "user" ? "User" : "PitBoss")}: {text}");
        return sb.ToString();
    }

    private static TrainingPlan BuildDefaultPlan(
        List<(string role, string text)> history, string workspaceRoot)
    {
        var goal = history.FirstOrDefault(h => h.role == "user").text ?? "general improvement";
        return new TrainingPlan
        {
            Goal          = goal,
            Persona       = "A helpful, precise AI assistant",
            Style         = "concise, technical",
            DatasetTarget = 800,
            DatasetSource = "cerebras",
            BaseModel     = "qwen2.5-coder:14b",
            AdapterName   = $"lora_custom_{DateTime.Now:yyyyMMdd}",
            TaskMix       = new() { ["feature"] = 0.5, ["bugfix"] = 0.3, ["docs"] = 0.2 },
            EstDatasetHours = 3,
            EstTrainHours   = 2,
        };
    }
}
