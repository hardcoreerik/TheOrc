// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace OrchestratorIDE.Core;

/// <summary>
/// Manages the llama-server.exe process lifecycle for the LlamaCpp backend.
///
/// Usage flow:
///   1. Set RuntimePath, ModelPath, Port, GpuLayers, ContextSize from AppSettings.
///   2. Call StartAsync() — waits until /health returns 200 (model loaded).
///   3. OllamaClient.Host is pointed at http://127.0.0.1:{Port}
///      and OllamaClient.Backend = LlamaCpp.
///   4. Call Stop() on app exit (or Dispose()).
///
/// Thread safety: StartAsync/Stop are not re-entrant — call only from one thread
/// (the UI thread in MainWindow is fine).
/// </summary>
public sealed class LlamaServerManager : IDisposable
{
    private Process? _process;

    // Short-lived client just for health polling — separate from the main OllamaClient
    private readonly HttpClient _health = new() { Timeout = TimeSpan.FromSeconds(3) };

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>Fired with log lines from the server process + internal messages.</summary>
    public event Action<string>? OnLog;

    /// <summary>Fired when the server starts (true) or stops (false).</summary>
    public event Action<bool>? OnStatusChanged;

    // ── Configuration (set before StartAsync) ────────────────────────────────

    /// <summary>Directory that contains llama-server.exe and its runtime DLLs.</summary>
    public string RuntimePath  { get; set; } = "";

    /// <summary>Absolute path to the GGUF model file to load on startup.</summary>
    public string ModelPath    { get; set; } = "";

    /// <summary>TCP port for the HTTP server. Default: 8080.</summary>
    public int    Port         { get; set; } = 8080;

    /// <summary>
    /// GPU layers to offload:
    ///  -1  → all layers (recommended — uses full VRAM)
    ///   0  → CPU-only (no GPU)
    ///  N>0 → exactly N layers on GPU
    /// </summary>
    public int    GpuLayers    { get; set; } = -1;

    /// <summary>Context window in tokens. Default: 8192.</summary>
    public int    ContextSize  { get; set; } = 8192;

    /// <summary>CPU inference threads. 0 = auto-detect (recommended).</summary>
    public int    Threads      { get; set; } = 0;

    /// <summary>
    /// HIVE MIND C2 — llama.cpp RPC worker endpoints to offload layers to.
    /// Format: "ip:port" (e.g. "192.168.1.20:50052"). Empty = run locally only.
    /// Each entry appends <c>--rpc ip:port</c> to the server arguments.
    /// The coordinator's GPU handles the first N layers (up to its VRAM limit);
    /// remaining layers are distributed across these remote workers in order.
    /// </summary>
    public List<string> RpcEndpoints { get; set; } = [];

    // ── State ────────────────────────────────────────────────────────────────

    public bool   IsRunning  => _process is { HasExited: false };
    public string BaseUrl    => $"http://127.0.0.1:{Port}";

    // ── Start ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches llama-server.exe and waits until it reports healthy.
    /// Large models (14B+) can take 30–60 s to load — the default timeout is 3 min.
    /// Returns true when ready, false if timed out or the process crashed.
    /// </summary>
    public async Task<bool> StartAsync(
        TimeSpan? loadTimeout     = null,
        CancellationToken ct      = default)
    {
        if (IsRunning) { Log("Server already running."); return true; }

        var exePath = LocateServerExe();
        if (exePath is null)
        {
            Log($"llama-server.exe not found in: {RuntimePath}");
            return false;
        }
        if (!File.Exists(ModelPath))
        {
            Log($"Model file not found: {ModelPath}");
            return false;
        }

        var args = BuildArgs();
        Log($"Launching: {Path.GetFileName(exePath)} {args}");

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            Arguments              = args,
            WorkingDirectory       = RuntimePath,
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        try
        {
            _process = Process.Start(psi);
            if (_process is null) { Log("Process.Start returned null."); return false; }

            // Forward server stdout/stderr → OnLog
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[srv] {e.Data}"); };
            _process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log($"[srv] {e.Data}"); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Log($"Failed to start process: {ex.Message}");
            return false;
        }

        // Poll /health until the model is loaded or timeout
        var deadline = DateTime.UtcNow + (loadTimeout ?? TimeSpan.FromMinutes(3));
        Log("Waiting for model to load…");

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                Log($"Server exited unexpectedly (code {_process.ExitCode}).");
                return false;
            }

            if (await IsHealthyAsync(ct))
            {
                Log($"Server ready → {BaseUrl}");
                OnStatusChanged?.Invoke(true);
                return true;
            }

            await Task.Delay(1_500, ct);
        }

        Log("Timed out waiting for server.");
        Stop();
        return false;
    }

    // ── Stop ─────────────────────────────────────────────────────────────────

    /// <summary>Kills the server process. Safe to call if not running.</summary>
    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3_000);
                Log("Server stopped.");
            }
        }
        catch (Exception ex) { Log($"Stop: {ex.Message}"); }
        finally
        {
            _process.Dispose();
            _process = null;
            OnStatusChanged?.Invoke(false);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _health.GetAsync($"{BaseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string? LocateServerExe()
    {
        if (string.IsNullOrWhiteSpace(RuntimePath)) return null;
        foreach (var name in new[] { "llama-server.exe", "server.exe" })
        {
            var path = Path.Combine(RuntimePath, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private string BuildArgs()
    {
        var sb = new StringBuilder();

        // Core flags
        sb.Append($"--model \"{ModelPath}\"");
        sb.Append($" --host 127.0.0.1");
        sb.Append($" --port {Port}");
        sb.Append($" --ctx-size {ContextSize}");

        // GPU offload
        var gpuLayers = GpuLayers == -1 ? 99_999 : GpuLayers;
        sb.Append($" --n-gpu-layers {gpuLayers}");

        // CPU threads (0 = omit flag → llama.cpp auto-detects)
        if (Threads > 0)
            sb.Append($" --threads {Threads}");

        // Disable memory-mapping for broader Windows compat
        sb.Append(" --no-mmap");

        // HIVE MIND C2: chain remote RPC workers for VRAM expansion.
        // Each --rpc endpoint receives overflow layers when the local GPU runs out.
        foreach (var ep in RpcEndpoints)
            if (!string.IsNullOrWhiteSpace(ep))
                sb.Append($" --rpc \"{ep}\"");

        // Suppress verbose token logs (health endpoint still works)
        sb.Append(" --log-disable");

        return sb.ToString();
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        _health.Dispose();
    }
}
