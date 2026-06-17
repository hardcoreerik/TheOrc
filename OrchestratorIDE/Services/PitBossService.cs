// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services;

/// <summary>
/// Drives the Pit Boss training wizard via a local Ollama model.
///
/// Architecture — host owns state, LLM generates content:
///   1. OpeningAsync        — greeting + question 1 (goal)
///   2. NextQuestionAsync   — question text for a specific slot (panel tracks _round)
///   3. SynthesizePlanAsync — two-phase: format:-constrained JSON call, fence fallback
///
/// The panel (PitBossPanel) owns _round and advances it each turn.
/// This service never decides which question to ask next — it only phrases it.
///
/// Offline fallback: if Ollama is unreachable, uses a built-in static question tree.
/// </summary>
public sealed class PitBossService
{
    // ── Static fallback question tree ────────────────────────────────────────
    private static readonly string[] _fallbackQuestions =
    [
        "What do you want your AI to become better at? (e.g. code review, delegation, a specific language, a persona)",
        "Do you have an existing dataset to train on, or should we generate one from scratch?",
        "Which programming language or domain is most important? (leave blank for any)",
        "How would you describe the ideal response style? (e.g. terse and technical, verbose and friendly, formal)",
        "Which base model should we fine-tune? (e.g. qwen2.5-coder:14b — press Enter to keep the default)",
        "How much training data? (quick ≈ 300  /  standard ≈ 800  /  thorough ≈ 2,000) — or which dataset file to use?",
        "How long are you willing to let training run? (quick ~1 h  /  overnight  /  weekend)",
        "Does this look right? Type 'yes' to generate the plan, or correct anything above.",
    ];

    // ── Question slot topics (LLM phrases these; host picks the slot index) ──
    // Slot 0 = opening (OpeningAsync). Slots 1–7 map to NextQuestionAsync roundIndex.
    private static readonly string[] _questionSlotTopics =
    [
        "what AI capability or skill they want to improve",
        "whether they have an existing dataset to train on, or need to generate one from scratch — if they mention a dataset name from the ENVIRONMENT block, the answer is 'existing'",
        "which programming languages or domains are most important (e.g. C#, Python, SQL, any)",
        "what the ideal response style looks like (terse/verbose, formal/casual, technical level)",
        "which base model to fine-tune — recommend ONLY models listed in the ENVIRONMENT block",
        "if mode is 'existing': which dataset file to use (reference names from ENVIRONMENT); if mode is 'generate': how many examples (quick ≈ 300 / standard ≈ 800 / thorough ≈ 2,000)",
        "preferred training duration — quick (~1 h / 1 epoch) / standard (~3 h / 3 epochs) / overnight (~6 h / 5 epochs)",
        "final confirmation — briefly summarize the key plan details and ask if everything looks right",
    ];

    // ── Interview system prompt ───────────────────────────────────────────────
    private static readonly string _systemPrompt = """
        You are TheOrc Pit Boss — a concise, expert AI training coach inside OrchestratorIDE's Training Pit.
        Your job is to guide the user through setting up a LoRA fine-tuning plan for a local language model.

        Rules:
        - Ask EXACTLY ONE question per turn. Never ask two questions at once. Never ask a follow-up on the same turn.
        - Keep every response under 80 words.
        - Be direct and encouraging — no disclaimers, no filler phrases like "Great choice!" or "Absolutely!".
        - You DO NOT produce JSON during the interview. A separate synthesis call handles that after all 8 questions.
        - If the user mentions an existing dataset by name, treat mode as "existing". Reference dataset names from the ENVIRONMENT block.
        - For base model recommendations, ONLY suggest models listed in the ENVIRONMENT block.

        TrainingPlan schema (for your awareness — the synthesizer produces this after the interview):
        {
          "goal":              "one-line plain-English goal",
          "persona":           "system prompt anchor for generated examples",
          "style":             "response style description",
          "languages":         ["csharp", "python", ...],
          "task_mix":          { "code_review": 0.6, "bugfix": 0.3, "docs": 0.1 },
          "mode":              "generate",        // "generate" | "existing"
          "dataset_target":    800,               // examples to generate (mode=generate only)
          "dataset_source":    "cerebras",        // "cerebras" | "ollama" | "manual" | "existing"
          "dataset_file":      "",                // train_KEY stem if mode=existing, e.g. "v2gold"
          "dataset_gen_model": "<model-from-ENVIRONMENT>",
          "base_model":        "<model-from-ENVIRONMENT>",
          "adapter_name":      "lora_network_eng_v1",
          "lora_rank":         16,
          "epochs":            3,
          "learning_rate":     0.0002,
          "est_dataset_hours": 0.0,              // 0 when mode=existing
          "est_train_hours":   1.5,
          "notes":             "free text"
        }

        Valid task_mix keys: code_review, bugfix, refactor, feature, tests, docs, integration, ui, delegation, persona, explanation
        Valid dataset_source: cerebras, ollama, manual, existing
        Valid mode: generate, existing
        """;

    // ── Synthesis prompt (phase 2 — short, focused) ──────────────────────────
    private const string _synthesisSystemPrompt =
        "You are a JSON synthesizer. Given a training wizard interview transcript, produce a single valid JSON object. " +
        "Fill ALL fields from what the user said. " +
        "If mode is 'existing', set dataset_source='existing', dataset_file to the dataset stem mentioned (e.g. 'v2gold'), and est_dataset_hours=0. " +
        "Output ONLY the JSON object — no markdown, no explanation.";

    // ── JSON schema for format: constrained synthesis ────────────────────────
    // Flat schema (≤1 nesting level) for reliable output on ≤4B models.
    // Required fields listed first so they are generated before attention degrades.
    private const string _synthesisSchemaJson = """
        {
          "type": "object",
          "properties": {
            "goal":              { "type": "string" },
            "mode":              { "type": "string", "enum": ["generate", "existing"] },
            "adapter_name":      { "type": "string" },
            "base_model":        { "type": "string" },
            "dataset_source":    { "type": "string", "enum": ["cerebras", "ollama", "manual", "existing"] },
            "lora_rank":         { "type": "integer" },
            "epochs":            { "type": "integer" },
            "learning_rate":     { "type": "number" },
            "persona":           { "type": "string" },
            "style":             { "type": "string" },
            "languages":         { "type": "array", "items": { "type": "string" } },
            "task_mix":          { "type": "object" },
            "dataset_target":    { "type": "integer" },
            "dataset_file":      { "type": "string" },
            "dataset_gen_model": { "type": "string" },
            "est_dataset_hours": { "type": "number" },
            "est_train_hours":   { "type": "number" },
            "notes":             { "type": "string" }
          },
          "required": ["goal", "mode", "adapter_name", "base_model", "dataset_source", "lora_rank", "epochs", "learning_rate"]
        }
        """;

    // ── HTTP ─────────────────────────────────────────────────────────────────
    private readonly HttpClient _http;
    private readonly string     _ollamaHost;
    private readonly string     _model;
    private          bool       _ollamaAvailable = true;

    /// <summary>
    /// Live system state set by the panel before the first call.
    /// Appended to each interview request's system prompt so the model knows which
    /// datasets and Ollama models are actually installed.
    /// </summary>
    public string EnvironmentContext { get; set; } = "";

    public PitBossService(string ollamaHost = "http://localhost:11434",
                          string model      = "hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF:Q4_K_M")
    {
        _ollamaHost = ollamaHost.TrimEnd('/');
        _model      = model;
        _http       = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Streams the opening greeting + question 1 (goal).</summary>
    public async IAsyncEnumerable<string> OpeningAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        const string fallback = "What do you want to train your AI to become better at?\n\n" +
                                "You can describe a skill, a language, a persona, or a goal — " +
                                "anything from \"better C# code reviewer\" to \"delegate tasks like a senior engineer\" " +
                                "to \"respond like a Socratic philosopher\".";

        if (!_ollamaAvailable)
        {
            yield return fallback;
            yield break;
        }

        const string prompt = "Greet the user in one sentence (max 10 words), then ask question 1: " +
                              "what do they want to train their AI to be better at? " +
                              "Give 4-5 brief example goals as a bulleted list. Under 80 words total.";

        await foreach (var chunk in StreamAsync([], prompt, ct, fallback))
            yield return chunk;
    }

    // ── Follow-up questions ───────────────────────────────────────────────────

    /// <summary>
    /// Streams the question text for the given slot (0-based).
    /// The panel owns _round and calls this; the LLM only phrases the question.
    /// </summary>
    public async IAsyncEnumerable<string> NextQuestionAsync(
        List<(string role, string text)> history,
        int roundIndex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_ollamaAvailable)
        {
            yield return roundIndex < _fallbackQuestions.Length
                ? _fallbackQuestions[roundIndex]
                : "Does everything look right? Type 'yes' to generate the plan.";
            yield break;
        }

        var slotIndex = Math.Clamp(roundIndex, 0, _questionSlotTopics.Length - 1);
        var topic     = _questionSlotTopics[slotIndex];
        var context   = BuildHistoryContext(history);

        var prompt = roundIndex >= 7
            ? $"{context}\n\nFinal confirmation step: briefly summarize the key plan details (2-3 lines), " +
              "then ask if everything looks right or needs corrections. Under 80 words."
            : $"{context}\n\nQuestion slot {roundIndex + 1} of 8 — ask about: {topic}. " +
              "One sentence. Direct. Do not repeat anything already confirmed.";

        // Supply the slot-appropriate fallback so a mid-interview Ollama failure
        // asks the correct question rather than re-asking the opening goal.
        var fallback = roundIndex < _fallbackQuestions.Length
            ? _fallbackQuestions[roundIndex]
            : "Does everything look right? Type 'yes' to generate the plan.";

        await foreach (var chunk in StreamAsync(history, prompt, ct, fallback))
            yield return chunk;
    }

    // ── Plan synthesis (two-phase) ────────────────────────────────────────────

    /// <summary>
    /// Synthesizes a TrainingPlan from the full Q&amp;A history.
    /// Phase 2a: format:-constrained JSON call (Ollama 0.5+, deterministic).
    /// Phase 2b: fallback to ```json fence extraction for older Ollama builds.
    /// </summary>
    public async Task<(TrainingPlan? Plan, string RawJson)> SynthesizePlanAsync(
        List<(string role, string text)> history,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        if (!_ollamaAvailable)
            return (BuildDefaultPlan(history, _model), "{}");

        var (plan, raw) = await SynthesizeWithFormatAsync(history, ct);
        if (plan is not null)
            return (plan, raw);

        return await SynthesizeWithFenceAsync(history, ct);
    }

    private async Task<(TrainingPlan? Plan, string Raw)> SynthesizeWithFormatAsync(
        List<(string role, string text)> history,
        CancellationToken ct)
    {
        var context  = BuildHistoryContext(history);
        var messages = new List<object>
        {
            new { role = "system", content = _synthesisSystemPrompt },
            new { role = "user",   content = $"Interview transcript:\n\n{context}\n\nSynthesize into a training plan." }
        };

        JsonElement schema;
        try { schema = JsonSerializer.Deserialize<JsonElement>(_synthesisSchemaJson); }
        catch { return (null, ""); }

        var body = JsonSerializer.Serialize(new
        {
            model   = _model,
            messages,
            stream  = false,
            format  = schema,
            options = new { temperature = 0, num_ctx = 8192 },
        });

        using var resp = await PostSafeAsync(body, ct);
        if (resp is null) return (null, "");

        try
        {
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            using var doc    = JsonDocument.Parse(responseBody);
            var content      = doc.RootElement
                                   .GetProperty("message")
                                   .GetProperty("content")
                                   .GetString() ?? "";
            return (ParsePlanCore(content.Trim(), _model), content);
        }
        catch { return (null, ""); }
    }

    private async Task<(TrainingPlan? Plan, string Raw)> SynthesizeWithFenceAsync(
        List<(string role, string text)> history,
        CancellationToken ct)
    {
        var context = BuildHistoryContext(history);
        var prompt  = $"{context}\n\nSynthesize everything into a TrainingPlan. " +
                      "Output ONLY a ```json ... ``` block — no other text.";

        var sb = new StringBuilder();
        await foreach (var chunk in StreamAsync(history, prompt, ct))
            sb.Append(chunk);

        var raw  = sb.ToString();
        var plan = ParseWithFence(raw);
        return (plan, raw);
    }

    // ── Plan persistence ──────────────────────────────────────────────────────

    public static Data.PlanRepository? PlanRepo { get; set; }

    public static void SavePlan(TrainingPlan plan, string pitRoot)
    {
        var dir = Path.Combine(pitRoot, "training_pit", "plans");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, plan.PlanFileName), json);
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
        catch { }
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

    // ── Ollama streaming (interview phase) ───────────────────────────────────

    private async IAsyncEnumerable<string> StreamAsync(
        List<(string role, string text)> history,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct,
        string? fallbackOnError = null)
    {
        // Combine static system prompt with live environment context injected by the panel.
        var systemContent = string.IsNullOrWhiteSpace(EnvironmentContext)
            ? _systemPrompt
            : _systemPrompt + "\n\n" + EnvironmentContext;

        var messages = new List<object> { new { role = "system", content = systemContent } };
        foreach (var (role, text) in history)
            messages.Add(new { role, content = text });
        messages.Add(new { role = "user", content = userPrompt });

        var body = JsonSerializer.Serialize(new
        {
            model    = _model,
            messages,
            stream   = true,
            options  = new { temperature = 0.3, num_ctx = 8192 },
        });

        using var resp = await PostSafeAsync(body, ct);
        if (resp is null)
        {
            if (fallbackOnError is not null) yield return fallbackOnError;
            yield break;
        }

        // Buffer chunks into a list so yield return never appears inside a try/catch.
        // For the Pit Boss wizard, responses are short enough that buffering is fine.
        var chunks = new List<string>();
        try
        {
            using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader     = new System.IO.StreamReader(httpStream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                string? chunk = ParseChunk(line);
                if (chunk is not null) chunks.Add(chunk);
                if (IsDone(line)) break;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* mid-stream disconnect */ }

        if (chunks.Count == 0 && fallbackOnError is not null)
        {
            yield return fallbackOnError;
            yield break;
        }

        foreach (var chunk in chunks)
            yield return chunk;
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

    // Used by format:-constrained synthesis (raw JSON, no fences).
    private static TrainingPlan? ParsePlanCore(string json, string defaultModel = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;

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

            var mode          = Get("mode", "generate");
            var datasetSource = Get("dataset_source", mode == "existing" ? "existing" : "cerebras");

            return new TrainingPlan
            {
                Goal            = Get("goal"),
                Persona         = Get("persona"),
                Style           = Get("style"),
                Languages       = langs,
                TaskMix         = mix,
                Mode            = mode,
                DatasetTarget   = mode == "existing" ? 0 : GetI("dataset_target", 800),
                DatasetSource   = datasetSource,
                DatasetFile     = Get("dataset_file"),
                // Default to empty string — the GGUF chat model (defaultModel) is not
                // a valid training target; Forge will prompt the user to pick a model.
                // For ollama source, fall back to base_model if synthesis omits gen model.
                DatasetGenModel = Get("dataset_gen_model") is { Length: > 0 } gm ? gm
                                : datasetSource == "ollama" ? Get("base_model")
                                : "",
                BaseModel       = Get("base_model"),
                AdapterName     = adapterName,
                LoraRank        = GetI("lora_rank", 16),
                Epochs          = GetI("epochs", 3),
                LearningRate    = GetD("learning_rate", 2e-4),
                EstDatasetHours = mode == "existing" ? 0 : GetD("est_dataset_hours", 0),
                EstTrainHours   = GetD("est_train_hours", 0),
                Notes           = Get("notes"),
            };
        }
        catch { return null; }
    }

    // Used by the fence-extraction fallback path.
    private TrainingPlan? ParseWithFence(string raw)
    {
        var start = raw.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        var end   = start >= 0 ? raw.IndexOf("```", start + 7) : -1;
        var json  = start >= 0 && end > start ? raw[(start + 7)..end].Trim() : raw.Trim();
        return ParsePlanCore(json, _model);
    }

    private static string BuildHistoryContext(List<(string role, string text)> history)
    {
        var sb = new StringBuilder("Conversation so far:\n");
        foreach (var (role, text) in history)
            sb.AppendLine($"{(role == "user" ? "User" : "PitBoss")}: {text}");
        return sb.ToString();
    }

    private static TrainingPlan BuildDefaultPlan(List<(string role, string text)> history, string model)
    {
        var goal = history.FirstOrDefault(h => h.role == "user").text ?? "general improvement";
        return new TrainingPlan
        {
            Goal            = goal,
            Mode            = "generate",
            Persona         = "A helpful, precise AI assistant",
            Style           = "concise, technical",
            DatasetTarget   = 800,
            DatasetSource   = "cerebras",
            BaseModel       = "",   // empty — Forge will prompt the user to select a training-capable model
            AdapterName     = $"lora_custom_{DateTime.Now:yyyyMMdd}",
            TaskMix         = new() { ["feature"] = 0.5, ["bugfix"] = 0.3, ["docs"] = 0.2 },
            EstDatasetHours = 3,
            EstTrainHours   = 2,
        };
    }

    // ── Environment context (called by both WPF and Avalonia panels) ──────────

    /// <summary>
    /// Builds and injects the live &lt;ENVIRONMENT&gt; block into <see cref="EnvironmentContext"/>.
    /// Silently no-ops on failure so the wizard still starts offline.
    /// </summary>
    public async Task BuildEnvironmentContextAsync(string workspaceRoot)
    {
        // Clear stale context first so a failed refresh doesn't leak a previous inventory.
        EnvironmentContext = "";
        try
        {
            var datasets = TrainingPitRegistry.LoadDatasets(workspaceRoot);
            var models   = await TrainingPitRegistry.LoadModelsAsync(_ollamaHost);

            var dsLines = datasets
                .Where(d => !d.InProgress && d.TrainCount > 0)
                .OrderByDescending(d => d.LastModified)
                .Take(8)
                .Select(d => $"  {d.Name} ({d.TrainCount:N0} train examples)");

            // Exclude hf.co/ GGUF quants — Forge cannot map these to a trainable HF repo.
            // Standard Ollama tags (e.g. qwen2.5-coder:14b) pass through.
            var trainableModels = models
                .Where(m => !m.Name.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) &&
                            !m.Name.Contains("gguf", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            var modelLines = trainableModels.Count > 0
                ? trainableModels.Select(m => $"  {m.Name} ({m.SizeGb:F1} GB)")
                : new[] { "  (none suitable for LoRA training — install a standard Ollama model first, e.g. qwen2.5-coder:14b)" };

            EnvironmentContext =
                "<ENVIRONMENT>\n" +
                "Available training datasets:\n" +
                string.Join("\n", dsLines.DefaultIfEmpty("  (none found)")) + "\n\n" +
                "Installed Ollama models (trainable — GGUF/hf.co quants excluded):\n" +
                string.Join("\n", modelLines) + "\n\n" +
                $"Training hardware: {DetectVramDescription()}.\n" +
                "</ENVIRONMENT>";
        }
        catch { }
    }

    private static string DetectVramDescription()
    {
        // nvidia-smi is available on Windows and Linux CUDA installs; skip probe on other platforms.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return "local GPU (VRAM unknown)";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "local GPU (VRAM unknown)";
            var readTask = Task.Run(() => proc.StandardOutput.ReadLine()?.Trim());
            if (!proc.WaitForExit(2000) || !readTask.Wait(2500))
            {
                // Observe task before kill so stream-close exception is swallowed cleanly.
                _ = readTask.ContinueWith(static _ => { }, TaskContinuationOptions.OnlyOnFaulted);
                try { proc.Kill(); } catch { }
                return "local GPU (VRAM unknown)";
            }
            var line = readTask.IsCompletedSuccessfully ? readTask.Result : null;
            if (!string.IsNullOrEmpty(line))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var mb))
                    return $"{parts[0].Trim()}, {mb / 1024} GB VRAM";
            }
        }
        catch { }
        return "local GPU (VRAM unknown)";
    }
}
