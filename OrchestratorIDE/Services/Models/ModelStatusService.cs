// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Timers;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Polls the active inference backend (Ollama or llama.cpp) for live
/// model metrics and exposes them as an event.
/// Used to populate the status bar model widget.
/// </summary>
public sealed class ModelStatusService : IDisposable
{
    private readonly HttpClient _http;
    private System.Timers.Timer? _timer;
    private AppSettings _settings;
    private bool _disposed;

    public ModelStatusService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    // ── Public events / snapshot ──────────────────────────────────────────────

    public event Action<ModelStatusSnapshot>? OnUpdate;

    public ModelStatusSnapshot? LastSnapshot { get; private set; }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    public void Start(TimeSpan interval)
    {
        _timer?.Dispose();
        _timer = new System.Timers.Timer(interval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await PollAsync();
        _timer.Start();
    }

    public void Stop() { _timer?.Stop(); }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    // ── Poll ──────────────────────────────────────────────────────────────────

    private async Task PollAsync()
    {
        if (_disposed) return;
        try
        {
            var snap = _settings.Backend switch
            {
                InferenceBackend.Ollama   => await PollOllamaAsync(),
                InferenceBackend.LlamaCpp => await PollLlamaCppAsync(),
                _                        => null,
            };

            if (snap is not null)
            {
                LastSnapshot = snap;
                OnUpdate?.Invoke(snap);
            }
        }
        catch { /* non-fatal — status bar just shows stale data */ }
    }

    private async Task<ModelStatusSnapshot?> PollOllamaAsync()
    {
        var baseUrl = _settings.OllamaHost.TrimEnd('/');

        // GET /api/ps — returns currently loaded models
        try
        {
            var ps = await _http.GetFromJsonAsync<OllamaPs>($"{baseUrl}/api/ps");
            if (ps?.Models is null || ps.Models.Count == 0)
                return new ModelStatusSnapshot { IsRunning = false };

            // Map Ollama loaded models to our role fields
            var workerTag     = _settings.LastWorkerModel;
            var bossTag       = _settings.LastSwarmModel;
            var researcherTag = _settings.LastResearcherModel;

            var workerLoaded     = ps.Models.Any(m => m.Name.StartsWith(workerTag,
                                       StringComparison.OrdinalIgnoreCase));
            var bossLoaded       = !string.IsNullOrEmpty(bossTag) &&
                                   ps.Models.Any(m => m.Name.StartsWith(bossTag,
                                       StringComparison.OrdinalIgnoreCase));

            // Token speed: Ollama /api/ps doesn't expose t/s directly;
            // we approximate from last generate response header if available.
            // For now we just report what's loaded.
            return new ModelStatusSnapshot
            {
                IsRunning        = true,
                BackendLabel     = "Ollama",
                WorkerModel      = workerTag,
                BossModel        = bossTag,
                ResearcherModel  = researcherTag,
                WorkerLoaded     = workerLoaded,
                BossLoaded       = bossLoaded,
                TotalVramMb      = (int)ps.Models.Sum(m => m.SizeVram / 1_048_576),
            };
        }
        catch { return null; }
    }

    private async Task<ModelStatusSnapshot?> PollLlamaCppAsync()
    {
        var baseUrl = $"http://127.0.0.1:{_settings.LlamaCppPort}";
        try
        {
            var props = await _http.GetFromJsonAsync<LlamaCppProps>($"{baseUrl}/props");
            if (props is null) return null;

            return new ModelStatusSnapshot
            {
                IsRunning    = true,
                BackendLabel = "llama.cpp",
                WorkerModel  = Path.GetFileNameWithoutExtension(_settings.LlamaCppModelPath),
                BossModel    = "",
                WorkerLoaded = true,
                BossLoaded   = false,
                TokensPerSec = await ReadPredictedTokensPerSecondAsync(baseUrl),
                TotalVramMb  = ReadGpuVramUsedMb(),
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the real generation speed of the last completion from llama-server's
    /// Prometheus /metrics endpoint (requires --metrics, see LlamaServerManager.BuildArgs).
    /// Returns 0 if the endpoint is unavailable or no generation has happened yet.
    /// </summary>
    private async Task<double> ReadPredictedTokensPerSecondAsync(string baseUrl)
    {
        try
        {
            var text = await _http.GetStringAsync($"{baseUrl}/metrics");
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("llamacpp:predicted_tokens_seconds", StringComparison.Ordinal))
                    continue;
                var spaceIdx = line.LastIndexOf(' ');
                if (spaceIdx < 0) continue;
                if (double.TryParse(line[(spaceIdx + 1)..].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var tps))
                    return tps;
            }
        }
        catch { /* /metrics not enabled or server not up yet — report 0, not a guess */ }
        return 0;
    }

    /// <summary>
    /// Real VRAM-in-use via nvidia-smi, same source TrainingPitPanel's VRAM meter uses.
    /// Reports total GPU memory in use system-wide (not just this process) since llama-server
    /// doesn't expose its own VRAM footprint — closest equivalent to Ollama's per-model figure.
    /// </summary>
    private static int ReadGpuVramUsedMb()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "nvidia-smi",
                Arguments              = "--query-gpu=memory.used --format=csv,noheader,nounits",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var line = p?.StandardOutput.ReadLine();
            p?.WaitForExit(3000);
            if (line is not null &&
                int.TryParse(line.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var usedMb))
                return usedMb;
        }
        catch { /* no nvidia-smi (no NVIDIA GPU, or not on PATH) — report 0 */ }
        return 0;
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private class OllamaPs
    {
        [JsonPropertyName("models")]
        public List<OllamaPsModel>? Models { get; set; }
    }

    private class OllamaPsModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size_vram")]
        public long SizeVram { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private class LlamaCppProps
    {
        [JsonPropertyName("total_slots")]
        public int TotalSlots { get; set; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _http.Dispose();
    }
}

/// <summary>
/// Snapshot of current model status, published by <see cref="ModelStatusService"/>.
/// </summary>
public sealed class ModelStatusSnapshot
{
    public bool   IsRunning        { get; set; }
    public string BackendLabel     { get; set; } = "";
    public string WorkerModel      { get; set; } = "";
    public string BossModel        { get; set; } = "";
    public string ResearcherModel  { get; set; } = "";
    public bool   WorkerLoaded     { get; set; }
    public bool   BossLoaded       { get; set; }
    public int    TotalVramMb      { get; set; }
    public double TokensPerSec     { get; set; }

    // ── Display helpers ───────────────────────────────────────────────────────

    public string ShortStatusLine
    {
        get
        {
            if (!IsRunning) return "⬡ No model loaded";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(WorkerModel))
                parts.Add($"W: {ShortName(WorkerModel)}");
            if (!string.IsNullOrEmpty(BossModel))
                parts.Add($"B: {ShortName(BossModel)}");
            return "🧠 " + (parts.Count > 0 ? string.Join("  ·  ", parts) : BackendLabel);
        }
    }

    public string VramDisplay =>
        TotalVramMb > 0 ? $"  {TotalVramMb / 1024.0:F1} GB VRAM" : "";

    public string SpeedDisplay =>
        TokensPerSec > 0 ? $"  {TokensPerSec:F0} t/s" : "";

    private static string ShortName(string model)
    {
        // e.g. "qwen2.5-coder:7b-instruct-q5_k_m" → "Qwen2.5-C 7B"
        if (string.IsNullOrEmpty(model)) return "";
        var tag = model.Split(':').LastOrDefault() ?? model;
        var size = System.Text.RegularExpressions.Regex.Match(tag, @"\d+b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
        return size.Length > 0 ? $"{model.Split(':')[0].Split('/').Last().Split('-')[0]}:{size}" : model.Split('/').Last();
    }
}
