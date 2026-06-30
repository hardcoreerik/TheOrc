// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Data;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.Daemon;

/// <summary>
/// Generic-Host service that boots the full HIVE swarm stack:
///   • HiveNodeServer  (port 7078) — peer identity, election, pairing, remote deploy
///   • HiveTaskQueue   (configurable, default 7079) — Warchief task queue + durable SQL history
///   • HiveMeshHeartbeat           — 15 s/30 s peer-liveness pulses (started inside NodeServer.Start)
///   • HiveElectionService         — Bully-style Warchief election (created inside NodeServer.Start)
///   • HiveWorkerAgent             — polls Warchief queue, executes tasks via model runtime (optional)
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
        if (string.IsNullOrEmpty(_cfg.NativeModelRoot))
            _cfg.NativeModelRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TheOrc", "models");
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
        HiveTaskQueue.CampaignRepository = new CampaignRepository(_db);
        _log.LogInformation("SQLite: {Path}", _db.DbPath);

        // ── Task queue (Warchief side) ────────────────────────────────────────
        _taskQueue = new HiveTaskQueue();
        _taskQueue.ArtifactStore = new ContentAddressedStore(
            Path.Combine(_cfg.WorkspaceRoot, ".orc", "campaign-artifacts"));
        _taskQueue.ModelStore = new ContentAddressedStore(
            _cfg.NativeModelRoot, fileExtension: ".gguf");
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

        var depot = ModelDepot.Scan(_cfg.NativeModelRoot);
        var nativeModels = depot.Assets
            .Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf)
            .Select(a => a.DisplayName)
            .ToArray();
        var nativeReady = nativeModels.Length > 0;
        var info = new HiveNodeInfo(
            Name:        _cfg.NodeName,
            OllamaUrl:   "",
            Models:      nativeModels,
            VramFreeMb:  checked((int)Math.Min(int.MaxValue, _cfg.NativeVramMb)),
            VramTotalMb: checked((int)Math.Min(int.MaxValue, _cfg.NativeVramMb)),
            Lanes:       nativeReady ? [.. _cfg.WorkerLanes] : []);
        _nodeServer.Start(info);   // starts listener on HiveNodeServer.ApiPort (7078)

        // Wire election/heartbeat logs after Start (services auto-created inside Start).
        if (_nodeServer.ElectionService is { } election)
            election.OnLog += msg => _log.LogInformation("[Election] {Msg}", msg);
        if (_nodeServer.MeshHeartbeat is { } hb)
            hb.OnLog += msg => _log.LogInformation("[Heartbeat] {Msg}", msg);

        _log.LogInformation("NodeServer listening on :{Port}", HiveNodeServer.ApiPort);

        // ── UDP beacon (multicast peer discovery) ─────────────────────────────
        _beacon = new HiveBeacon();
        _beacon.Start(_cfg.NodeName, "", nativeModels,
            vramFreeMb: checked((int)Math.Min(int.MaxValue, _cfg.NativeVramMb)));
        _log.LogInformation("Beacon started (UDP discovery)");

        // ── Worker agent (optional) ───────────────────────────────────────────
        if (_cfg.WorkerMode)
        {
            // _cfg.WarchiefUrl (Hive:WarchiefUrl) lets this node's worker poll a REMOTE
            // Warchief's queue instead of only ever its own -- previously hardcoded to
            // _taskQueue.BaseUrl with no way to configure otherwise. Same optional-empty-
            // string fallback shape as coderModel/researcherModel above.
            //
            // The self case points at loopback, NOT _taskQueue.BaseUrl (which is the LAN IP):
            // the worker polling its own queue is a same-machine call, and hitting 127.0.0.1
            // makes HttpListenerRequest.IsLocal reliably true regardless of how the OS routes a
            // host's connection to its own LAN IP -- which is what lets the queue's local-trust
            // exemption (HiveTaskQueue.HandleAsync) accept the self-poll that otherwise can't be
            // HMAC-signed (a node has no shared secret with itself). The wildcard "+" bind
            // already listens on loopback too, so this always reaches the same queue.
            var warchiefUrl = !string.IsNullOrWhiteSpace(_cfg.WarchiefUrl)
                ? _cfg.WarchiefUrl
                : $"http://127.0.0.1:{_cfg.TaskQueuePort}";

            IHiveNativeRoleExecutor? nativeExecutor = null;
            if (nativeReady)
            {
                var budget = _cfg.NativeVramMb > 0
                    ? new VramBudget(_cfg.NativeVramMb * 1024L * 1024L, ReservedBytes: 0)
                    : null;
                // ModelDepot.ResolveRole(role) alone (no workload kind) never consults
                // ModelAdmissionGate, so it can hand the Researcher role a reasoning-tuned model
                // (DeepSeek-R1-distill, Qwen3, etc.) whose <think> trace then consumes the whole
                // CF-6 reader response budget -- observed in production as "Model response
                // contained an unterminated JSON object." Pre-binding Researcher with the
                // workload-aware overload (same one ContextFabricBench already uses) routes
                // through EvaluateContextFabric's reasoning-tuned deprioritization instead.
                var roleBindings = new Dictionary<RuntimeRole, RuntimeRoleBinding>();
                if (depot.ResolveRole(RuntimeRole.Researcher, RuntimeWorkloadKind.ContextFabricReader) is { } researcherBinding)
                    roleBindings[RuntimeRole.Researcher] = researcherBinding;

                var nativeRuntime = new NativeRoleRuntime(
                    depot,
                    new RuntimeOptions(
                        ContextLength: Math.Max(512, _cfg.NativeContextSize),
                        GpuLayers: _cfg.NativeGpuLayers,
                        PreferGpu: _cfg.NativeGpuLayers != 0),
                    scheduler: budget is null ? null : new OrcScheduler(),
                    budgetProvider: budget is null ? null : () => budget,
                    roleBindings: roleBindings);

                nativeExecutor = new HiveNativeRoleExecutorAdapter(nativeRuntime, _cfg.WorkspaceRoot);
            }

            _worker = new HiveWorkerAgent
            {
                NativeRoleExecutor = nativeExecutor,
                Runtime           = null,
                WorkerId        = _cfg.NodeName,
                WorkerUrl       = $"native://{_cfg.NodeName}",
                Lanes           = [.. _cfg.WorkerLanes],
                WarchiefUrl     = warchiefUrl,
                WarchiefNodeId  = _cfg.WarchiefNodeId,
                ModelStore      = _taskQueue.ModelStore,
            };
            _worker.OnLog += msg => _log.LogInformation("[Worker] {Msg}", msg);
            var installedPacks = CampaignPackCatalog.ResolveInstalled(_cfg.AlienSearchImage);
            _worker.Capabilities = await WorkerCapabilityDetector.DetectAsync(
                _cfg.NodeName, depot, _cfg.NativeVramMb, _taskQueue.ArtifactStore,
                installedPacks, stoppingToken);
            if (_worker.Capabilities.ContainerEngine.Length > 0 &&
                installedPacks.Any(p => p.ExecutionKind == HiveExecutionKinds.ContainerPack))
            {
                _worker.ContainerRunner = new ContainerPackRunner(
                    _worker.Capabilities.ContainerEngine, _cfg.WorkspaceRoot, installedPacks);
            }
            _worker.Start();
            _log.LogInformation(
                "Worker started (lanes: {Lanes}, GGUF assets: {ModelCount}, model root: {ModelRoot}, warchief: {WarchiefUrl})",
                _cfg.WorkerLanes.Count > 0 ? string.Join(",", _cfg.WorkerLanes) : "all",
                nativeModels.Length,
                _cfg.NativeModelRoot,
                string.IsNullOrWhiteSpace(_cfg.WarchiefUrl) ? "self (loopback)" : warchiefUrl);
            if (!nativeReady)
                _log.LogWarning("No native model is admitted yet; native-agent leases remain ineligible while approved model sync stays active.");
        }

        _log.LogInformation("HIVE daemon ready. Press Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("HIVE daemon stopping…");
        try
        {
            if (_worker is not null)
                await _worker.ShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Worker shutdown timed out or was cancelled; continuing daemon teardown.");
            _worker?.Stop();
        }
        finally
        {
            _beacon?.Dispose();
            _nodeServer?.MeshHeartbeat?.Stop();
            _nodeServer?.Dispose();
            _taskQueue?.Dispose();
            // SqliteStore has no IDisposable — WAL and connection pool clean up on process exit.
            await base.StopAsync(ct);
            _log.LogInformation("HIVE daemon stopped.");
        }
    }

}
