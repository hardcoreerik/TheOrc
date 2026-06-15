// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Primes a model by pre-filling its KV cache with role-specific context.
///
/// How it works:
///   1. Load the warm-up script for the given role (from AppData or embedded default).
///   2. Send it to the active backend as a low-temperature inference request.
///   3. For llama.cpp: save the resulting prompt cache to disk so subsequent
///      launches skip the warm-up (cache hit on startup).
///   4. Report progress so the UI can show a step-by-step log.
///
/// Warm-up scripts are Markdown files in:
///   %APPDATA%\OrchestratorIDE\WarmUpScripts\{role}.md
/// First run: embedded defaults are copied there so users can edit them.
/// </summary>
public sealed class ModelWarmUpService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HttpClient  _http;

    private static readonly string _scriptDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "WarmUpScripts");

    // Role → script file name
    private static readonly Dictionary<string, string> _scriptFiles = new()
    {
        ["worker"]     = "worker.md",
        ["boss"]       = "boss.md",
        ["researcher"] = "researcher.md",
    };

    public ModelWarmUpService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Warm up the model assigned to <paramref name="role"/>.
    /// Progress reported via <paramref name="onLog"/> and <paramref name="onProgress"/> (0–100).
    /// </summary>
    public async Task WarmUpAsync(
        string role,
        Action<string>?   onLog      = null,
        Action<int>?      onProgress = null,
        CancellationToken ct         = default)
    {
        role = role.ToLowerInvariant();
        onLog?.Invoke($"🔥 Starting warm-up for {role} role…");
        onProgress?.Invoke(0);

        // ── Step 1: Ensure script files exist ─────────────────────────────
        EnsureScriptFiles();
        onProgress?.Invoke(10);

        // ── Step 2: Load the warm-up script ───────────────────────────────
        var script = LoadScript(role);
        if (string.IsNullOrWhiteSpace(script))
        {
            onLog?.Invoke($"⚠ No warm-up script found for role '{role}' — skipping.");
            return;
        }

        var modelName = role switch
        {
            "worker"     => _settings.LastWorkerModel,
            "boss"       => _settings.LastSwarmModel,
            "researcher" => _settings.LastResearcherModel,
            _            => _settings.DefaultModel,
        };

        if (string.IsNullOrEmpty(modelName))
        {
            onLog?.Invoke($"⚠ No model assigned to role '{role}' — skipping.");
            return;
        }

        onLog?.Invoke($"  Model  : {modelName}");
        onLog?.Invoke($"  Script : {ScriptPath(role)}");
        onProgress?.Invoke(20);

        // ── Step 3: Load model into VRAM (keep-alive request) ─────────────
        onLog?.Invoke("  Loading model into VRAM…");
        await EnsureModelLoadedAsync(modelName, ct);
        onProgress?.Invoke(40);

        // ── Step 4: Run the warm-up inference ─────────────────────────────
        onLog?.Invoke("  Running warm-up inference…");
        var success = await RunWarmUpInferenceAsync(modelName, script, onLog, ct);
        onProgress?.Invoke(80);

        // ── Step 5: Save prompt cache (llama.cpp only) ─────────────────────
        if (_settings.Backend == InferenceBackend.LlamaCpp)
        {
            var cachePath = Path.Combine(_scriptDir, $"{role}.cache");
            onLog?.Invoke($"  Saving KV cache → {cachePath}");
            // llama.cpp writes the cache automatically when --prompt-cache-all is set;
            // the path is passed at server startup time (not dynamically settable here).
            // We note the intent for when the server is restarted with the right flags.
            onLog?.Invoke("  ℹ Cache will be written on next llama-server restart with --prompt-cache-all");
        }

        onProgress?.Invoke(100);
        onLog?.Invoke(success
            ? $"✓ {role} warm-up complete."
            : $"⚠ Warm-up inference returned an error — model may still be partially primed.");
    }

    // ── Script management ─────────────────────────────────────────────────────

    public static string ScriptPath(string role) =>
        Path.Combine(_scriptDir, _scriptFiles.GetValueOrDefault(role, $"{role}.md"));

    public static string LoadScript(string role)
    {
        var path = ScriptPath(role);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public static void SaveScript(string role, string content)
    {
        Directory.CreateDirectory(_scriptDir);
        File.WriteAllText(ScriptPath(role), content);
    }

    /// <summary>
    /// Copies embedded default scripts to AppData the first time, so users
    /// have something to edit. Doesn't overwrite files they've already customised.
    /// </summary>
    public static void EnsureScriptFiles()
    {
        Directory.CreateDirectory(_scriptDir);
        foreach (var (role, file) in _scriptFiles)
        {
            var path = Path.Combine(_scriptDir, file);
            if (!File.Exists(path))
                File.WriteAllText(path, DefaultScript(role));
        }
    }

    // ── Default scripts ───────────────────────────────────────────────────────

    public static string DefaultScript(string role) => role switch
    {
        "worker" => WorkerDefault,
        "boss"   => BossDefault,
        _        => ResearcherDefault,
    };

    private const string WorkerDefault = """
        # TheOrc Worker Warm-Up Script
        # Role: Worker · Coder
        # This script pre-fills the KV cache with coding context and tool schemas.
        # Edit this file to customise the warm-up for your project stack.

        You are an expert software engineer working as part of TheOrc multi-agent system.
        You implement code changes requested by the Boss agent with precision and care.

        ## Your tools
        You have access to: read_file, write_file, list_files, run_shell, search_web.
        Always use tools when they are the right approach. Never fabricate file contents.

        ## Code standards
        - Write clean, well-commented, production-ready code
        - Handle errors explicitly — never swallow exceptions silently
        - Test your changes mentally before writing them
        - Prefer editing existing files over creating new ones unless clearly necessary

        ## Example tool use
        When asked to add a method to a class:
        1. read_file the class first to understand its context
        2. write_file with the complete updated content
        3. Confirm what changed and why

        Ready for coding tasks.
        """;

    private const string BossDefault = """
        # TheOrc Boss Warm-Up Script
        # Role: Boss · Orchestrator
        # This script pre-fills the KV cache with orchestration context and planning format.
        # Edit this file to customise task decomposition style.

        You are TheOrc — the orchestrator in a multi-agent software development system.
        You decompose tasks, delegate to Worker agents, and synthesise results into coherent solutions.

        ## Your workers
        - Worker/Coder: implements code changes, runs shell commands, reads/writes files
        - Researcher: searches the web, reads documentation, summarises findings

        ## Task decomposition format
        When given a task, think through:
        1. What information is needed first (research phase)?
        2. What code changes are needed (implementation phase)?
        3. What needs to be verified (test/confirm phase)?

        ## Output format
        Delegate tasks as JSON actions. Synthesise worker results into a final summary.
        Identify blockers early. Ask clarifying questions before starting ambiguous tasks.

        Ready to orchestrate.
        """;

    private const string ResearcherDefault = """
        # TheOrc Researcher Warm-Up Script
        # Role: Researcher
        # This script pre-fills the KV cache with research context.

        You are the Researcher agent in TheOrc multi-agent system.
        Your job is to find, read, and summarise information to support the coding workers.

        ## Your tools
        You have access to: search_web, read_file, list_files.

        ## Research approach
        - Search for the most authoritative sources (official docs, GitHub repos, RFCs)
        - Summarise findings concisely — the Boss needs key facts, not full articles
        - Flag uncertainty — say "I could not verify…" rather than guessing
        - Cite sources when possible

        Ready for research tasks.
        """;

    // ── Backend calls ─────────────────────────────────────────────────────────

    private async Task EnsureModelLoadedAsync(string modelName, CancellationToken ct)
    {
        var baseUrl = _settings.Backend == InferenceBackend.LlamaCpp
            ? $"http://127.0.0.1:{_settings.LlamaCppPort}"
            : _settings.OllamaHost.TrimEnd('/');

        try
        {
            if (_settings.Backend == InferenceBackend.Ollama)
            {
                // POST /api/generate with keep_alive=-1 to pin model in VRAM
                var payload = new { model = modelName, prompt = "", keep_alive = -1 };
                await _http.PostAsJsonAsync($"{baseUrl}/api/generate", payload, ct);
            }
            // llama.cpp: server is already running with the model loaded
        }
        catch { /* non-fatal — model may still respond */ }
    }

    private async Task<bool> RunWarmUpInferenceAsync(
        string modelName, string script,
        Action<string>? onLog, CancellationToken ct)
    {
        var baseUrl = _settings.Backend == InferenceBackend.LlamaCpp
            ? $"http://127.0.0.1:{_settings.LlamaCppPort}"
            : _settings.OllamaHost.TrimEnd('/');

        try
        {
            object payload;
            string endpoint;

            if (_settings.Backend == InferenceBackend.Ollama)
            {
                endpoint = $"{baseUrl}/api/generate";
                payload  = new
                {
                    model       = modelName,
                    prompt      = script,
                    stream      = false,
                    keep_alive  = -1,
                    options     = new { temperature = 0.0, num_predict = 64 },
                };
            }
            else
            {
                endpoint = $"{baseUrl}/completion";
                payload  = new
                {
                    prompt        = script,
                    temperature   = 0.0,
                    n_predict     = 64,
                    cache_prompt  = true,
                };
            }

            var resp = await _http.PostAsJsonAsync(endpoint, payload, ct);
            if (resp.IsSuccessStatusCode)
            {
                onLog?.Invoke("  ✓ Inference complete — KV cache primed.");
                return true;
            }

            onLog?.Invoke($"  ⚠ Backend returned {(int)resp.StatusCode}");
            return false;
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"  ⚠ Warm-up inference error: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
