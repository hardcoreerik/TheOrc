// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchestratorIDE.Services.Data;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.Daemon;

/// <summary>
/// Generic-Host service that boots the full HIVE swarm stack:
///   • HiveNodeServer  (port 7078) — peer identity, election, pairing, remote deploy
///   • HiveTaskQueue   (configurable, default 7079) — Warchief task queue + durable SQL history
///   • HiveMeshHeartbeat           — 15 s/30 s peer-liveness pulses (started inside NodeServer.Start)
///   • HiveElectionService         — Bully-style Warchief election (created inside NodeServer.Start)
///   • HiveWorkerAgent             — polls Warchief queue, executes tasks via Ollama (optional)
///   • HiveBeacon      (UDP)       — multicast peer discovery
/// </summary>
public sealed class HiveService : BackgroundService
{
    private static readonly string DefaultWorkspaceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", "daemon-workspace");

    private readonly DaemonConfig         _cfg;
    private readonly ILogger<HiveService> _log;

    private HiveNodeServer?  _nodeServer;
    private HiveTaskQueue?   _taskQueue;
    private HiveWorkerAgent? _worker;
    private HiveBeacon?      _beacon;
    private SqliteStore?     _db;

    public HiveService(IOptions<DaemonConfig> cfg, ILogger<HiveService> log)
    {
        _cfg = cfg.Value;
        _log = log;

        // Resolve defaults that must not be empty at runtime — appsettings.json may
        // omit these keys (letting C# defaults apply) but cannot set them to "".
        if (string.IsNullOrEmpty(_cfg.NodeName))
            _cfg.NodeName = Environment.MachineName;
        if (string.IsNullOrEmpty(_cfg.WorkspaceRoot))
            _cfg.WorkspaceRoot = DefaultWorkspaceRoot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("TheOrc HIVE daemon starting on node '{Name}'", _cfg.NodeName);

        // ── Secret protector (AES-256-GCM) ────────────────────────────────────
        SecretProtection.Initialize(new AesGcmSecretProtector(MachineKey.Load()));
        _log.LogInformation("Secrets: AES-256-GCM ({Src})",
            Environment.GetEnvironmentVariable("THEORC_SECRET_KEY") is not null
                ? "THEORC_SECRET_KEY" : "machine.key");

        // ── SQLite (durable HIVE history) ─────────────────────────────────────
        Directory.CreateDirectory(_cfg.WorkspaceRoot);
        _db = new SqliteStore(_cfg.WorkspaceRoot);
        _db.Initialize();
        HiveTaskQueue.Repository = new HiveRepository(_db);
        _log.LogInformation("SQLite: {Path}", _db.DbPath);

        // ── Task queue (Warchief side) ────────────────────────────────────────
        _taskQueue = new HiveTaskQueue();
        _taskQueue.OnLog += msg => _log.LogInformation("[TaskQueue] {Msg}", msg);
        var sessionCtx = new HiveSessionContext { SessionId = Guid.NewGuid().ToString("N")[..12] };
        _taskQueue.Start(sessionCtx, _cfg.TaskQueuePort);

        // ── Node server (peer API, election, heartbeat) ───────────────────────
        // HiveNodeServer.Start() auto-creates ElectionService and MeshHeartbeat
        // if they weren't pre-injected. Wire ShutdownCallback before Start.
        _nodeServer = new HiveNodeServer();
        _nodeServer.ShutdownCallback = () =>
        {
            _log.LogWarning("Remote /hive/update/deploy received — stopping daemon.");
            Environment.Exit(0);
        };

        var ollamaModels = await TryGetOllamaModelsAsync(_cfg.OllamaUrl);
        var info = new HiveNodeInfo(
            Name:        _cfg.NodeName,
            OllamaUrl:   _cfg.OllamaUrl,
            Models:      ollamaModels,
            VramFreeMb:  0,
            VramTotalMb: 0,
            Lanes:       [.. _cfg.WorkerLanes]);
        _nodeServer.Start(info);   // starts listener on HiveNodeServer.ApiPort (7078)

        // Wire election/heartbeat logs after Start (services auto-created inside Start).
        if (_nodeServer.ElectionService is { } election)
            election.OnLog += msg => _log.LogInformation("[Election] {Msg}", msg);
        if (_nodeServer.MeshHeartbeat is { } hb)
            hb.OnLog += msg => _log.LogInformation("[Heartbeat] {Msg}", msg);

        _log.LogInformation("NodeServer listening on :{Port}", HiveNodeServer.ApiPort);

        // ── UDP beacon (multicast peer discovery) ─────────────────────────────
        _beacon = new HiveBeacon();
        _beacon.Start(_cfg.NodeName, _cfg.OllamaUrl, ollamaModels, vramFreeMb: 0);
        _log.LogInformation("Beacon started (UDP discovery)");

        // ── Worker agent (optional) ───────────────────────────────────────────
        if (_cfg.WorkerMode)
        {
            var settings = Core.AppSettings.Load();
            var ollama   = new Core.OllamaClient(_cfg.OllamaUrl, settings.Backend);
            _worker = new HiveWorkerAgent
            {
                Ollama       = ollama,
                WorkerId     = _cfg.NodeName,
                Lanes        = [.. _cfg.WorkerLanes],
                WarchiefUrl  = _taskQueue.BaseUrl,
            };
            _worker.Start();
            _log.LogInformation("Worker agent started (lanes: {Lanes})",
                _cfg.WorkerLanes.Count > 0 ? string.Join(",", _cfg.WorkerLanes) : "all");
        }

        _log.LogInformation("HIVE daemon ready. Press Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("HIVE daemon stopping…");
        _worker?.Dispose();
        _beacon?.Dispose();
        _nodeServer?.MeshHeartbeat?.Stop();
        _nodeServer?.Dispose();
        _taskQueue?.Dispose();
        // SqliteStore has no IDisposable — WAL and connection pool clean up on process exit.
        await base.StopAsync(ct);
        _log.LogInformation("HIVE daemon stopped.");
    }

    private static async Task<string[]> TryGetOllamaModelsAsync(string baseUrl)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr))
                return [.. arr.EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(n => n.Length > 0)];
        }
        catch { /* Ollama absent or offline — no models to advertise */ }
        return [];
    }
}
