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

        // Enable BEFORE Start so the listener never accepts a pairing request while the window
        // is still closed -- closes a race where an early re-pair would be forced onto the
        // manual-approval path instead of the intended headless one.
        if (_cfg.DevAutoApproveMinutes > 0)
        {
            _nodeServer.EnableDevAutoApprove(TimeSpan.FromMinutes(_cfg.DevAutoApproveMinutes));
            _log.LogWarning(
                "DEV: HIVE re-sync auto-approve ON for {Minutes} min -- incoming re-pairing " +
                "requests from ALREADY-TRUSTED workers will be auto-trusted until it expires " +
                "(new/unknown peers still require manual fingerprint approval).", _cfg.DevAutoApproveMinutes);
        }

        _nodeServer.Start(info);   // starts listener on HiveNodeServer.ApiPort (7078)

        // Wire election/heartbeat logs after Start (services auto-created inside Start).
        if (_nodeServer.ElectionService is { } election)
        {
            election.OnLog += msg => _log.LogInformation("[Election] {Msg}", msg);

            // Discovered live (2026-07-29) verifying the new /hive/roles/degrade and
            // /hive/tasks/cancel endpoints: HiveElectionService.WarchiefNodeId is ONLY ever set
            // via live election-protocol messages (SetWarchief's own doc comment: "Must be
            // called by the app when a HIVE session starts... we've received a session context
            // identifying who is"). Nothing here ever called it for a statically-configured
            // worker, even though _cfg.WarchiefNodeId is already known from config -- so every
            // Warchief-only mutation gate (this one, /hive/roles/degrade, /hive/tasks/cancel, and
            // the PRE-EXISTING /hive/update/deploy) rejected the fleet's real, correctly-signed
            // Warchief with a 403 on every one of them, on every worker in this deployment shape.
            // A worker that already knows its Warchief via config doesn't need to wait for
            // election traffic to learn what it was already told.
            if (!string.IsNullOrWhiteSpace(_cfg.WarchiefNodeId))
                election.SetWarchief(_cfg.WarchiefNodeId);
        }
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
            if (nativeReady && _cfg.NativeVramMb <= 0)
            {
                // Native Runtime v2.0 Phase A (docs/NATIVE_RUNTIME_V2_SPEC.md §1.2 Gap 2):
                // RuntimeOrchestrator now fails CLOSED when no scheduler/budget is configured
                // instead of silently loading unadmitted -- so skip building the native runtime
                // entirely rather than constructing one whose every conversation would throw.
                // This is the least-attended surface in the fleet (a HIVE worker runs
                // unattended), so it gets the strictest treatment: no opt-out here, configure
                // Hive:NativeVramMb to enable native role execution.
                _log.LogWarning(
                    "Native role execution requested (WorkerMode + native ready) but " +
                    "Hive:NativeVramMb is not configured, so a VRAM budget cannot be derived and " +
                    "admission cannot be evaluated. Skipping native role execution for this " +
                    "worker; configure Hive:NativeVramMb to enable it.");
            }
            else if (nativeReady)
            {
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

                // Pin CUDA-preferring backend selection BEFORE any native load; ongoing native
                // log lines (model-load progress) go to Debug. A CUDA-capable GPU landing on the
                // CPU backend is a Warning with the full selection log — never silent.
                var backend = NativeBackendBootstrap.EnsureConfigured(
                    line => _log.LogDebug("[llama-native] {Line}", line));
                if (backend.CudaCapableGpu && !backend.SelectedCuda)
                {
                    _log.LogWarning("Native backend: {Verdict}", backend.Verdict);
                    foreach (var line in backend.Log)
                        _log.LogWarning("[backend-selection] {Line}", line);
                }
                else
                {
                    _log.LogInformation("Native backend: {Verdict}", backend.Verdict);
                }

                var nativeRuntime = new NativeRoleRuntime(
                    depot,
                    new RuntimeOptions(
                        ContextLength: Math.Max(512, _cfg.NativeContextSize),
                        GpuLayers: _cfg.NativeGpuLayers,
                        PreferGpu: _cfg.NativeGpuLayers != 0),
                    scheduler: new OrcScheduler(),
                    // Native Runtime v2.0 Phase B (docs/NATIVE_RUNTIME_V2_SPEC.md Phase B): the
                    // method itself, not a closed-over snapshot -- called fresh on every
                    // admission by RuntimeOrchestrator.EnsureAdmitted. This is the
                    // least-attended surface in the fleet (an unattended HIVE worker), so a
                    // live read matters even more here than in MainWindow.
                    budgetProvider: BuildNativeHiveVramBudget,
                    roleBindings: roleBindings);

                nativeExecutor = new HiveNativeRoleExecutorAdapter(nativeRuntime, _cfg.WorkspaceRoot);
                // HV-2 (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): RejectedAdmissionCount/
                // LastRejectionReason existed in-process (RuntimeOrchestrator) but had no remote
                // observability surface on a headless worker -- only the Avalonia GUI's own
                // diagnostics panel could see them. Exposed read-only over GET /hive/native-telemetry.
                // HV-3 adds residency alongside reservation. The two are deliberately decoupled
                // in the runtime (a reservation persists while the model stays loaded; residency
                // drops as soon as the conversation is disposed), and HV-3's whole assertion is
                // that ActiveCount returns to baseline BETWEEN jobs while the reservation does
                // not -- which is unobservable on a headless worker with reservation alone.
                //
                // AdapterRoleResidency.Binding is projected down to display names rather than
                // serialized whole: it carries absolute GGUF/adapter paths, and this endpoint is
                // unauthenticated (same posture as /hive/info). Role, counts and status are what
                // the phase actually asserts on; the local filesystem layout is not.
                // Residency is added as an ADDITIVE key: the reservation fields stay exactly where
                // they are at the top level. Tools/Hv2SchedulingRunner deserializes
                // RejectedAdmissionCount/LastRejectionReason from there, and HV-6 requires the
                // whole HV-1..HV-5 campaign to re-run 3x unattended -- nesting them to make this
                // response tidier would break a driver the campaign depends on, for no gain.
                _nodeServer.NativeTelemetryProvider = () =>
                {
                    var reservation = nativeRuntime.GetReservationSnapshot();
                    return new
                    {
                        reservation?.Reservations,
                        // Defaulted to 0 rather than left null: Tools/Hv3LifecycleRunner's
                        // NativeTelemetry DTO declares these as non-nullable long, so a JSON null
                        // here (whenever there's no scheduler/budget provider, e.g. before the
                        // native runtime finishes initializing) fails deserialization and the
                        // driver reports the whole worker unreachable rather than "not yet
                        // admitting" — a false negative on exactly the signal HV-3/HV-5 sweep for.
                        TotalBytes             = reservation?.TotalBytes ?? 0,
                        ReservedBytes          = reservation?.ReservedBytes ?? 0,
                        AvailableBytes         = reservation?.AvailableBytes ?? 0,
                        RejectedAdmissionCount = reservation?.RejectedAdmissionCount ?? 0,
                        LastRejectionReason    = reservation?.LastRejectionReason,
                        Residency = nativeRuntime.GetResidencySnapshot().Select(r => new
                        {
                            Role      = r.Role.ToString(),
                            BaseModel = r.Binding.BaseModel.DisplayName,
                            Adapter   = r.Binding.Adapter?.DisplayName,
                            r.ActiveCount,
                            r.ConversationsCreated,
                            Status    = r.Status.ToString(),
                        }).ToList(),
                    };
                };

                // HV-3 item 3 (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): MarkRoleDegraded was
                // previously reachable only from the runtime's own internal NoKvSlot handling —
                // this wires the long-deferred remote trigger (POST /hive/roles/degrade) to the
                // same NativeRoleRuntime instance NativeTelemetryProvider above already closes
                // over.
                _nodeServer.MarkRoleDegradedHandler = role => nativeRuntime.MarkRoleDegraded(role);
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
            // HV-4 item 1: a campaign cancel previously only marked the task cancelled on the
            // Warchief while the worker kept generating to completion — no remote trigger existed
            // to actually interrupt it. POST /hive/tasks/cancel now reaches this worker's
            // per-task cancellation registry directly.
            _nodeServer.CancelTaskHandler = taskId => _worker.TryCancelTask(taskId);
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

    /// <summary>
    /// Native Runtime v2.0 Phase B (docs/NATIVE_RUNTIME_V2_SPEC.md Phase B): prefers a LIVE
    /// nvidia-smi read over the operator-configured static <see cref="DaemonConfig.NativeVramMb"/>
    /// total -- <see cref="VramBudget.ReservedBytes"/> then reflects whatever else is actually
    /// using the GPU right now, not a static zero. Falls back to the pre-Phase-B
    /// config-only budget when the live probe is unavailable (non-NVIDIA GPU, nvidia-smi
    /// missing, etc.), so this is never worse than what WorkerMode already required
    /// (Hive:NativeVramMb configured and positive) to reach this call site.
    /// </summary>
    private VramBudget BuildNativeHiveVramBudget() =>
        NativeVramProbe.TryQueryLiveNvidiaBudget()
        ?? new VramBudget(_cfg.NativeVramMb * 1024L * 1024L, ReservedBytes: 0);
}
