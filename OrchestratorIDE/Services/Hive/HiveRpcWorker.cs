// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND C2 — llama.cpp RPC worker process manager.
///
/// When this machine joins the hive as a worker it can expose its GPU VRAM to
/// a remote coordinator via the llama.cpp RPC backend. The coordinator's
/// llama-server process uses <c>--rpc &lt;this-ip&gt;:50052</c> to offload layers
/// onto this machine's GPU, effectively pooling VRAM across two machines for a
/// single large model that neither could run alone.
///
/// Coordinator side (Machine A, 8 GB):
///   llama-server --model mixtral-70b.gguf --rpc 192.168.1.20:50052 --ngl 99999
///
/// Worker side (this machine, Machine B, 24 GB) — managed by this class:
///   llama-rpc-server --host 0.0.0.0 --port 50052
///
/// Security: port 50052 should be firewalled to Private profile (HiveEnroller does
/// this). On Tailscale, the WireGuard mesh provides mutual authentication.
/// The RPC protocol has no built-in auth — trust is established at network level only.
/// </summary>
public sealed class HiveRpcWorker : IDisposable
{
    public const int  DefaultPort    = 50052;
    public const string ExeName      = "llama-rpc-server.exe";

    private Process? _process;

    /// <summary>Fired with log lines from the RPC server process.</summary>
    public event Action<string>? OnLog;

    /// <summary>Fired when the server starts (true) or stops (false).</summary>
    public event Action<bool>? OnStatusChanged;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>Directory containing llama-rpc-server.exe and CUDA DLLs.</summary>
    public string RuntimePath { get; set; } = "";

    /// <summary>Port to listen on. Default: 50052.</summary>
    public int Port { get; set; } = DefaultPort;

    // ── State ─────────────────────────────────────────────────────────────────

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Returns true if llama-rpc-server.exe exists in <see cref="RuntimePath"/>.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(LocateRpcExe());

    // ── Start ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts llama-rpc-server.exe bound to 0.0.0.0:<see cref="Port"/>.
    /// Returns false if the exe is not found or the process fails to start.
    /// </summary>
    public bool Start()
    {
        if (IsRunning) { Log("RPC server already running."); return true; }

        var exePath = LocateRpcExe();
        if (exePath is null)
        {
            Log($"llama-rpc-server.exe not found in: {RuntimePath}");
            return false;
        }

        var args = $"--host 0.0.0.0 --port {Port}";
        Log($"Launching RPC worker: {Path.GetFileName(exePath)} {args}");

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

            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[rpc] {e.Data}"); };
            _process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) Log($"[rpc] {e.Data}"); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            OnStatusChanged?.Invoke(true);
            Log($"RPC worker listening on port {Port}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to start RPC server: {ex.Message}");
            return false;
        }
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3_000);
                Log("RPC server stopped.");
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

    // ── Static helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Probes whether port <paramref name="port"/> is open and accepting connections
    /// on <paramref name="host"/>. Used by the coordinator to verify a worker is up.
    /// </summary>
    public static async Task<bool> IsListeningAsync(
        string host, int port = DefaultPort, int timeoutMs = 1500)
    {
        try
        {
            using var tcp = new TcpClient();
            var conn = tcp.ConnectAsync(host, port);
            await Task.WhenAny(conn, Task.Delay(timeoutMs));
            return tcp.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns all local IPv4 addresses (non-loopback) so the coordinator can pick
    /// the right one to use in its --rpc flag. Returns empty list on failure.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                          && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString())
                .ToList();
        }
        catch { return []; }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private string? LocateRpcExe()
    {
        if (string.IsNullOrWhiteSpace(RuntimePath)) return null;
        var path = Path.Combine(RuntimePath, ExeName);
        return File.Exists(path) ? path : null;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose() => Stop();
}
