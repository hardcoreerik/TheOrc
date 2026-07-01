// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND Phase 3 — Worker-side polling agent.
///
/// When HiveWorkerMode is enabled this agent runs alongside the existing
/// HiveNodeServer. It polls the Warchief's HiveTaskQueue (port 7079) for
/// available tasks, claims them, executes them locally using IModelRuntime,
/// and POSTs results back so the Warchief's SwarmSession can continue.
///
/// Execution model (Phase 3A): single-pass LLM call — the worker builds a
/// system + user message from the bundle and streams a full response.
/// Workers produce ### FILE: markers that the Warchief extracts as usual.
/// Multi-step tool calling on remote workers is Phase 3B.
///
/// Lanes: a worker advertises which task roles it accepts
/// (e.g. "researcher", "coder"). Empty lanes = accept all roles.
/// </summary>
public sealed class HiveWorkerAgent : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultDisposeWaitTimeout = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _cts;
    private Task? _runLoopTask;
    private Task? _shutdownTask;
    private int _disposeState;
    private HiveNativeAgentExecution? _lastAgentExecution;
    private ContainerPackExecution? _lastContainerExecution;
    private int _readerEvidenceCount;
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
    };

    // ── Configuration (set before Start) ─────────────────────────────────────

    /// <summary>This machine's display name reported in task results.</summary>
    public string WorkerId    { get; set; } = Environment.MachineName;

    /// <summary>This machine's Ollama base URL (e.g. "http://localhost:11434").</summary>
    public string WorkerUrl   { get; set; } = "http://localhost:11434";

    /// <summary>Warchief's HiveTaskQueue URL (e.g. "http://192.168.1.10:7079").</summary>
    public string WarchiefUrl { get; set; } = "";

    /// <summary>
    /// Warchief's NodeId (hex-SHA256 of signing key). When set, peer lookup for
    /// HMAC signing uses NodeId directly instead of IP/host matching — immune to
    /// hostname vs IP mismatches when the worker was configured by hostname.
    /// </summary>
    public string WarchiefNodeId { get; set; } = "";

    /// <summary>Task roles this worker accepts. Empty = all roles.</summary>
    public string[] Lanes     { get; set; } = [];

    /// <summary>
    /// When true, a persistent HTTP 401 from the Warchief triggers an automatic re-sync
    /// (<see cref="TryResyncWithWarchiefAsync"/>) instead of just being logged — the worker
    /// half of headless fleet re-sync. The manual "Re-sync now" button works regardless of
    /// this flag; this only governs the automatic-on-401 behaviour. Set from
    /// AppSettings.HiveDevAutoResyncEnabled.
    /// </summary>
    public bool AutoResyncEnabled { get; set; }
    private DateTime _lastResyncAttempt = DateTime.MinValue;
    private static readonly TimeSpan ResyncCooldown = TimeSpan.FromSeconds(60);
    public WorkerCapabilities Capabilities { get; set; } = new();
    public IContainerPackRunner? ContainerRunner { get; set; }
    public ContentAddressedStore? ModelStore { get; set; }

    // ── Inference configuration ───────────────────────────────────────────────

    private IHiveNativeRoleExecutor? _nativeRoleExecutor;

    public IModelRuntime? Runtime       { get; set; }

    /// <summary>
    /// Optional native role-runtime hook. Left null on lightweight hosts (e.g. the
    /// headless Daemon) which only use <see cref="Runtime"/> (IModelRuntime/Ollama).
    /// </summary>
    public IHiveNativeRoleExecutor? NativeRoleExecutor
    {
        get => _nativeRoleExecutor;
        set => _nativeRoleExecutor = value;
    }
    public string        CoderModel     { get; set; } = "";
    public string        ResearcherModel { get; set; } = "";
    public TimeSpan DisposeWaitTimeout  { get; set; } = DefaultDisposeWaitTimeout;

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<string>?       OnLog;
    public event Action<bool>?         OnStatusChanged;   // true = running, false = stopped
    public event Action<string, string>? OnTaskActivity;  // taskId, message

    // ── State ─────────────────────────────────────────────────────────────────

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            throw new ObjectDisposedException(nameof(HiveWorkerAgent));

        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _runLoopTask = RunLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* best-effort shutdown */ }
        _cts = null;
    }

    // ── Main polling loop ─────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        Log($"🐝 Worker agent started (id={WorkerId}) — polling {WarchiefUrl}");
        OnStatusChanged?.Invoke(true);
        var nextModelSync = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ModelStore is not null && DateTimeOffset.UtcNow >= nextModelSync)
                {
                    await SyncApprovedModelsAsync(ct).ConfigureAwait(false);
                    nextModelSync = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                var lease = await PollLeaseAsync(ct).ConfigureAwait(false);
                if (lease is null)
                {
                    await Task.Delay(3_000, ct).ConfigureAwait(false);
                    continue;
                }

                var bundle = lease.Bundle;
                Log($"🐝 Leased [{bundle.Role}] '{bundle.Title}' from Warchief");
                await ClaimAndExecuteAsync(bundle, ct, lease.ClaimToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"⚠ Worker loop error: {ex.Message}");
                try { await Task.Delay(5_000, ct).ConfigureAwait(false); } catch { break; }
            }
        }

        Log("🐝 Worker agent stopped.");
        OnStatusChanged?.Invoke(false);
    }

    private async Task SyncApprovedModelsAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var catalogRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{WarchiefUrl.TrimEnd('/')}/hive/models");
        SignIfPaired(catalogRequest, []);
        using var catalogResponse = await http.SendAsync(catalogRequest, ct).ConfigureAwait(false);
        if (!catalogResponse.IsSuccessStatusCode)
        {
            Log($"⚠ Approved-model catalog rejected by Warchief: HTTP {(int)catalogResponse.StatusCode}");
            // A 401 here is the canonical "my trust for the Warchief went stale" signal (this
            // runs once a minute, so it's a natural self-heal checkpoint). Auto-resync when the
            // operator has opted in and the cooldown has elapsed — completes headlessly only if
            // the Warchief's dev auto-approve window is open, otherwise it waits for a manual OK.
            if (catalogResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && AutoResyncEnabled
                && DateTime.UtcNow - _lastResyncAttempt > ResyncCooldown)
            {
                _lastResyncAttempt = DateTime.UtcNow;
                await TryResyncWithWarchiefAsync(ct).ConfigureAwait(false);
            }
            return;
        }
        var json = await catalogResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var approved = JsonSerializer.Deserialize<ApprovedModelAsset[]>(json, _json) ?? [];

        foreach (var model in approved.Where(m => !ModelStore!.Has(m.DigestSha256)))
        {
            var offset = ModelStore!.GetResumeOffset(model.DigestSha256);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{WarchiefUrl.TrimEnd('/')}/hive/models/{model.DigestSha256}");
            if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
            SignIfPaired(request, []);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (offset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new InvalidDataException("Warchief did not honor the model resume range.");

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[ContentAddressedStore.MaxChunkBytes];
            while (offset < model.SizeBytes)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, model.SizeBytes - offset)), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Model transfer ended before its declared size.");
                await ModelStore.WriteChunkAsync(model.DigestSha256, offset, model.SizeBytes,
                    buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                offset += read;
            }
            Log($"🐝 Native model synced and verified: {model.DigestSha256[..12]}… Restart for native preflight.");
        }
    }

    // ── Self-heal (headless fleet re-sync) ────────────────────────────────────

    /// <summary>
    /// Worker-side self-heal for the "HTTP 401 after the Warchief's identity rotated" failure
    /// that otherwise needs a hand-edit of both machines' hive-peers.json plus a restart of
    /// both apps. Discovers the Warchief's CURRENT NodeId via its unauthenticated
    /// <c>/hive/update/version</c> endpoint (no trust required), and if this node doesn't
    /// already hold a usable shared secret for that exact NodeId, re-initiates pairing. The
    /// re-pair completes headlessly only while the Warchief has its dev auto-approve window
    /// open (<see cref="HiveNodeServer.EnableDevAutoApprove"/>); otherwise it waits for a human
    /// approve there. On success the fresh secret replaces the stale one, PruneSuperseded drops
    /// the dead-identity duplicates, and <see cref="WarchiefNodeId"/> is pinned to the live id
    /// so signing stops resolving to a stale entry by URL. Returns true when trust is healthy
    /// (already, or after a completed re-pair). Never throws (except on cancellation).
    /// </summary>
    public async Task<bool> TryResyncWithWarchiefAsync(CancellationToken ct = default)
    {
        try
        {
            var host = Uri.TryCreate(WarchiefUrl, UriKind.Absolute, out var u) ? u.Host : "";
            if (string.IsNullOrEmpty(host))
            {
                Log($"⚠ Re-sync: could not parse a host from Warchief URL '{WarchiefUrl}'.");
                return false;
            }

            // 1. Discover the Warchief's live identity (unauthenticated — always answers even
            //    when every signed request is being 401'd).
            string liveNodeId;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var versionUrl = $"http://{host}:{HiveNodeServer.ApiPort}/hive/update/version";
                var json = await http.GetStringAsync(versionUrl, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                liveNodeId = doc.RootElement.TryGetProperty("nodeId", out var n) ? n.GetString() ?? "" : "";
            }
            if (string.IsNullOrEmpty(liveNodeId))
            {
                Log("⚠ Re-sync: Warchief did not report a NodeId — is its HIVE node server running?");
                return false;
            }

            // 2. Already hold a usable secret for the live identity? Then the 401 is something
            //    else; just make sure we TARGET that identity when signing and stop here.
            var (trusted, secret) = HivePeerStore.Default.GetTrustedSecret(liveNodeId);
            if (trusted && secret is not null)
            {
                WarchiefNodeId = liveNodeId;
                Log("🔄 Re-sync: trust is already current for the live Warchief identity.");
                return true;
            }

            // 3. Stale/missing trust → re-initiate pairing against the live host.
            Log($"🔄 Re-sync: Warchief identity is {liveNodeId[..12]}… with no matching trust — re-pairing.");
            var result = await HivePairingClient.PairAsync(host, timeoutSec: 60, ct: ct).ConfigureAwait(false);
            if (result.Outcome != HivePairingClient.Outcome.Approved || result.Pending is null)
            {
                Log($"⚠ Re-sync pairing did not complete: {result.Outcome}" +
                    (result.Message is null ? "" : $" — {result.Message}") +
                    (result.Outcome == HivePairingClient.Outcome.TimedOut
                        ? " (open the Warchief's \"Accept re-sync\" window and retry)" : ""));
                return false;
            }

            // Defense against an on-path surprise: the identity we just paired with MUST be the
            // one the version endpoint advertised. PairAsync already binds NodeId to the signing
            // key; this additionally ties it to what we set out to trust.
            if (!string.Equals(result.Pending.Peer.NodeId, liveNodeId, StringComparison.OrdinalIgnoreCase))
            {
                Log("⚠ Re-sync: the paired identity did not match the advertised Warchief NodeId — aborting, trusting nothing.");
                return false;
            }

            HivePairingClient.ConfirmAndTrust(result.Pending);
            WarchiefNodeId = liveNodeId;
            Log($"✅ Re-sync complete — now trusting Warchief {liveNodeId[..12]}… (stale entries pruned).");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"⚠ Re-sync failed: {ex.Message}");
            return false;
        }
    }

    // ── Poll ─────────────────────────────────────────────────────────────────

    private async Task<HiveTaskBundle?> PollNextTaskAsync(CancellationToken ct)
    {
        var lanes = Lanes.Length > 0
            ? string.Join(",", Lanes)
            : "researcher,coder,uideveloper,tester";

        var url  = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/next?lanes={lanes}&workerId={WorkerId}";
        using var req  = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        SignIfPaired(req, []);

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HiveTaskBundle>(json, _json);
    }

    private async Task<HiveLeaseResponse?> PollLeaseAsync(CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HiveLeaseRequest
        {
            WorkerId = WorkerId,
            WorkerUrl = WorkerUrl,
            Lanes = Lanes,
            Capabilities = Capabilities with { WorkerId = WorkerId },
        }, _json));
        var url = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/lease";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        SignIfPaired(req, body);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var legacy = await PollNextTaskAsync(ct).ConfigureAwait(false);
            if (legacy is null) return null;
            var token = await ClaimTaskAsync(legacy, ct).ConfigureAwait(false);
            return token is null ? null : new HiveLeaseResponse { Bundle = legacy, ClaimToken = token };
        }
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HiveLeaseResponse>(json, _json);
    }

    // ── Claim → Execute → Complete ────────────────────────────────────────────

    private async Task ClaimAndExecuteAsync(HiveTaskBundle bundle, CancellationToken ct, string? leasedToken = null)
    {
        var claimToken = leasedToken ?? await ClaimTaskAsync(bundle, ct).ConfigureAwait(false);
        if (claimToken is null)
        {
            Log($"🐝 [{bundle.Role}] '{bundle.Title}' — already claimed by another worker");
            return;
        }

        var startedAt = DateTime.UtcNow;
        _lastAgentExecution = null;
        _lastContainerExecution = null;
        _readerEvidenceCount = 0;
        string? result   = null;
        string  status   = "completed";
        string? errorMsg = null;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask      = HeartbeatLoopAsync(bundle, claimToken ?? "", heartbeatCts.Token);

        try
        {
            result = await ExecuteTaskAsync(bundle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            status   = "failed";
            errorMsg = ex.Message;
            Log($"⚠ [{bundle.Role}] '{bundle.Title}' — execution failed: {ex.Message}");
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
        }

        var taskResult = new HiveTaskResult
        {
            TaskId     = bundle.TaskId,
            WorkerId   = WorkerId,
            WorkerUrl  = WorkerUrl,
            Result     = result ?? "",
            Status     = status,
            ErrorMsg   = errorMsg,
            DurationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            ClaimToken = claimToken,
            Attempt    = bundle.Attempt,
        };
        if (_lastAgentExecution is { } execution)
        {
            taskResult.Metrics["steps"] = execution.Steps;
            taskResult.Metrics["prompt_tokens"] = execution.PromptTokens;
            taskResult.Metrics["completion_tokens"] = execution.CompletionTokens;
            if (_readerEvidenceCount > 0) taskResult.Metrics["evidence_count"] = _readerEvidenceCount;
            taskResult.Attestation = new ExecutionAttestation
            {
                RuntimeName = "NativeRoleRuntime",
                Backend = Capabilities.NativeBackend,
                ModelHash = bundle.Requirements.NativeModelHash,
                AdapterHash = bundle.Requirements.NativeAdapterHash,
                ToolTraceDigest = execution.TraceDigest,
                InputDigests = bundle.InputArtifacts.ToDictionary(a => a.Name, a => a.DigestSha256),
            };
            taskResult.OutputArtifacts.AddRange(
                await UploadOutputArtifactsAsync(bundle, execution.OutputDirectory, ct).ConfigureAwait(false));
        }
        if (_lastContainerExecution is { } container)
        {
            taskResult.Attestation = new ExecutionAttestation
            {
                RuntimeName = "ContainerPackRunner",
                Backend = Capabilities.ContainerEngine,
                ContainerDigest = container.ContainerDigest,
                InputDigests = new Dictionary<string, string>(container.InputDigests),
            };
            taskResult.OutputArtifacts.AddRange(
                await UploadOutputArtifactsAsync(bundle, container.OutputDirectory, ct).ConfigureAwait(false));
        }

        var action = status == "completed" ? "complete" : "fail";
        await PostResultAsync(bundle.TaskId, action, taskResult, ct).ConfigureAwait(false);
        TaskActivity(bundle.TaskId,
            status == "completed"
                ? $"✅ Sent to Warchief ({taskResult.DurationMs / 1000.0:F1}s, {result?.Length ?? 0} chars)"
                : $"⚠ Failure reported to Warchief: {errorMsg}");
    }

    private async Task<string?> ClaimTaskAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var url  = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{bundle.TaskId}/claim";
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HiveClaimRequest
        {
            WorkerId  = WorkerId,
            WorkerUrl = WorkerUrl,
            Lanes     = Lanes,
        }, _json));

        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
            { Content = new System.Net.Http.ByteArrayContent(body) };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        SignIfPaired(req, body);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claimToken", out var tok))
                return tok.GetString();
        }
        catch { }
        return "";
    }

    private async Task HeartbeatLoopAsync(HiveTaskBundle bundle, string claimToken, CancellationToken ct)
    {
        var hbBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HiveHeartbeatRequest
        {
            WorkerId   = WorkerId,
            ClaimToken = claimToken,
        }, _json));

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(10_000, ct).ConfigureAwait(false); } catch { break; }
            if (ct.IsCancellationRequested) break;

            try
            {
                var url = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{bundle.TaskId}/heartbeat";
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                    { Content = new System.Net.Http.ByteArrayContent(hbBytes) };
                req.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                SignIfPaired(req, hbBytes);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                await http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }
        }
    }

    private async Task PostResultAsync(string taskId, string action, HiveTaskResult result, CancellationToken ct)
    {
        var url   = $"{WarchiefUrl.TrimEnd('/')}/hive/tasks/{taskId}/{action}";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, _json));

        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
            { Content = new System.Net.Http.ByteArrayContent(bytes) };
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        SignIfPaired(req, bytes);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        await http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ArtifactRef>> UploadOutputArtifactsAsync(
        HiveTaskBundle bundle, string outputDirectory, CancellationToken ct)
    {
        var artifacts = new List<ArtifactRef>();
        if (!Directory.Exists(outputDirectory)) return artifacts;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length == 0) continue;
            var digest = await ContentAddressedStore.ComputeSha256Async(path, ct).ConfigureAwait(false);
            var relative = Path.GetRelativePath(outputDirectory, path).Replace('\\', '/');
            var url = $"{WarchiefUrl.TrimEnd('/')}/hive/artifacts/{digest}";

            long offset = 0;
            using (var head = new HttpRequestMessage(HttpMethod.Head, url))
            {
                SignIfPaired(head, []);
                using var response = await http.SendAsync(head, ct).ConfigureAwait(false);
                if (response.Headers.TryGetValues("X-Hive-Complete", out var complete) &&
                    complete.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    offset = info.Length;
                else if (response.Headers.TryGetValues("X-Hive-Stored-Bytes", out var stored) &&
                         long.TryParse(stored.FirstOrDefault(), out var parsed))
                    offset = parsed;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                ContentAddressedStore.MaxChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = offset;
            var buffer = new byte[ContentAddressedStore.MaxChunkBytes];
            while (offset < info.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, info.Length - offset)), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException($"Unexpected EOF reading {relative}.");
                var bytes = buffer.AsSpan(0, read).ToArray();
                using var put = new HttpRequestMessage(HttpMethod.Put, url)
                    { Content = new ByteArrayContent(bytes) };
                put.Headers.TryAddWithoutValidation("X-Hive-Offset", offset.ToString());
                put.Headers.TryAddWithoutValidation("X-Hive-Total-Bytes", info.Length.ToString());
                put.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                SignIfPaired(put, bytes);
                using var response = await http.SendAsync(put, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                offset += read;
            }

            artifacts.Add(new ArtifactRef
            {
                DigestSha256 = digest,
                Name = relative,
                SizeBytes = info.Length,
                MediaType = GuessMediaType(path),
                Kind = "output",
            });
        }
        return artifacts;
    }

    /// <summary>
    /// CF-6: downloads the staged single-segment corpus for a Context Fabric reader task from the
    /// Warchief's content-addressed store (GET /hive/artifacts/{digest}) and rebuilds the FabricCorpus.
    /// The digest is re-verified locally so a corrupt or substituted artifact fails closed before the
    /// reader ever sees untrusted bytes.
    /// </summary>
    private async Task<FabricCorpus> FetchReaderCorpusAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var input = bundle.InputArtifacts.FirstOrDefault(a =>
                        a.Name.EndsWith("corpus.json", StringComparison.OrdinalIgnoreCase))
                    ?? bundle.InputArtifacts.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "Context Fabric reader task has no input corpus artifact.");
        if (string.IsNullOrWhiteSpace(input.DigestSha256))
            throw new InvalidOperationException("Context Fabric reader input artifact has no digest.");

        var url = $"{WarchiefUrl.TrimEnd('/')}/hive/artifacts/{input.DigestSha256}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SignIfPaired(req, []);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var actual = ContentAddressedStore.ComputeSha256(bytes);
        if (!string.Equals(actual, input.DigestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Context Fabric reader corpus digest mismatch: expected {input.DigestSha256}, got {actual}.");

        var corpus = JsonSerializer.Deserialize<FabricCorpus>(Encoding.UTF8.GetString(bytes), FabricJson.Options)
            ?? throw new InvalidOperationException("Context Fabric reader corpus artifact deserialized to null.");
        if (corpus.Segments.Count != 1)
            throw new InvalidOperationException(
                $"Context Fabric reader expects a single-segment corpus, got {corpus.Segments.Count}.");
        return corpus;
    }

    /// <summary>
    /// CF-6: downloads and deserializes the reducer's inputs from the Warchief artifact store.
    /// The first artifact named "corpus-meta.json" supplies structural metadata (CorpusId, DocumentId,
    /// GenerationId); every remaining artifact named "*.evidence-card.json" is a reader output card.
    private async Task<(FabricCorpus Left, FabricCorpus Right)>
        FetchStitcherInputsAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var corpora = bundle.InputArtifacts
            .Where(a => a.Name.EndsWith(".corpus.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (corpora.Length < 2)
            throw new InvalidOperationException(
                $"Context Fabric stitcher task requires exactly 2 corpus artifacts, got {corpora.Length}.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var left = await FetchAndVerifyJsonAsync<FabricCorpus>(http, corpora[0], ct).ConfigureAwait(false);
        var right = await FetchAndVerifyJsonAsync<FabricCorpus>(http, corpora[1], ct).ConfigureAwait(false);
        return (left, right);
    }

    private async Task<(FabricEvidenceCard Card, FabricCorpus SourceCorpus)>
        FetchVerifierInputsAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var cardArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.EndsWith(".evidence-card.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Verifier task has no '.evidence-card.json' input artifact.");
        var corpusArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.EndsWith(".corpus.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Verifier task has no '.corpus.json' input artifact.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var card = await FetchAndVerifyJsonAsync<FabricEvidenceCard>(http, cardArtifact, ct).ConfigureAwait(false);
        var corpus = await FetchAndVerifyJsonAsync<FabricCorpus>(http, corpusArtifact, ct).ConfigureAwait(false);
        return (card, corpus);
    }

    private async Task<(string QuestionId, string QuestionText, FabricCorpus Corpus, FabricEvidenceCard? Card)>
        FetchQueryInputsAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        var questionArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.StartsWith("question-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Query task has no 'question-*.json' input artifact.");
        var corpusArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.EndsWith(".corpus.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Query task has no '.corpus.json' input artifact.");
        var cardArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.EndsWith(".evidence-card.json", StringComparison.OrdinalIgnoreCase));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var question = await FetchAndVerifyJsonAsync<FabricQueryQuestion>(http, questionArtifact, ct).ConfigureAwait(false);
        var corpus = await FetchAndVerifyJsonAsync<FabricCorpus>(http, corpusArtifact, ct).ConfigureAwait(false);
        FabricEvidenceCard? card = null;
        if (cardArtifact is not null)
            card = await FetchAndVerifyJsonAsync<FabricEvidenceCard>(http, cardArtifact, ct).ConfigureAwait(false);
        return (question.QuestionId, question.QuestionText, corpus, card);
    }

    /// All digests are re-verified locally before deserialization.
    /// </summary>
    private async Task<(FabricCorpus CorpusMeta, IReadOnlyList<FabricEvidenceCard> Cards)>
        FetchReducerInputsAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        if (bundle.InputArtifacts.Count == 0)
            throw new InvalidOperationException("Context Fabric reducer task has no input artifacts.");

        var metaArtifact = bundle.InputArtifacts.FirstOrDefault(a =>
            a.Name.EndsWith("corpus-meta.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Context Fabric reducer task has no 'corpus-meta.json' input artifact.");
        var cardArtifacts = bundle.InputArtifacts
            .Where(a => a.Name.EndsWith(".evidence-card.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (cardArtifacts.Length == 0)
            throw new InvalidOperationException(
                "Context Fabric reducer task has no evidence-card input artifacts.");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var corpusMeta = await FetchAndVerifyJsonAsync<FabricCorpus>(http, metaArtifact, ct).ConfigureAwait(false);
        if (corpusMeta.Segments.Count != 0)
            throw new InvalidOperationException(
                "Reducer corpus-meta artifact must have empty Segments; include only structural metadata.");

        var cards = new List<FabricEvidenceCard>(cardArtifacts.Length);
        foreach (var artifact in cardArtifacts)
            cards.Add(await FetchAndVerifyJsonAsync<FabricEvidenceCard>(http, artifact, ct).ConfigureAwait(false));
        return (corpusMeta, cards);
    }

    private async Task<T> FetchAndVerifyJsonAsync<T>(HttpClient http, ArtifactRef artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.DigestSha256))
            throw new InvalidOperationException($"Artifact '{artifact.Name}' has no digest.");
        var url = $"{WarchiefUrl.TrimEnd('/')}/hive/artifacts/{artifact.DigestSha256}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SignIfPaired(req, []);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var actual = ContentAddressedStore.ComputeSha256(bytes);
        if (!string.Equals(actual, artifact.DigestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Artifact '{artifact.Name}' digest mismatch: expected {artifact.DigestSha256}, got {actual}.");
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes), FabricJson.Options)
            ?? throw new InvalidOperationException($"Artifact '{artifact.Name}' deserialized to null.");
    }

    private static string GuessMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".jsonl" => "application/x-ndjson",
        ".csv" => "text/csv",
        ".md" => "text/markdown",
        ".txt" or ".log" => "text/plain",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    // ── Auth signing helper ───────────────────────────────────────────────────

    private void SignIfPaired(System.Net.Http.HttpRequestMessage req, byte[] body)
    {
        try
        {
            var identity = HiveIdentity.Load();

            // Prefer NodeId-based lookup (immune to hostname vs IP mismatches).
            // Fall back to host-string matching from WarchiefUrl when WarchiefNodeId is unset.
            HivePeer? warchief;
            if (!string.IsNullOrEmpty(WarchiefNodeId))
            {
                warchief = HivePeerStore.Default.Find(WarchiefNodeId);
            }
            else
            {
                var resolvedNodeId = HivePeerStore.Default.ResolveNodeIdForUrl(WarchiefUrl);
                warchief = string.IsNullOrEmpty(resolvedNodeId)
                    ? null
                    : HivePeerStore.Default.Find(resolvedNodeId);
            }
            if (warchief is null) return;

            var secret = HivePeerStore.Default.GetSharedSecret(warchief.NodeId);
            if (secret is null) return;

            HiveAuthMiddleware.SignRequest(req, body, identity.NodeId, secret);
        }
        catch { /* non-fatal — auth is advisory during grace period */ }
    }

    // ── Task execution (single-pass LLM call) ─────────────────────────────────

    private async Task<string> ExecuteTaskAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        if (bundle.ExecutionKind == HiveExecutionKinds.ContainerPack)
        {
            if (ContainerRunner is null)
                throw new InvalidOperationException("Worker: trusted container-pack runner not configured");
            _lastContainerExecution = await ContainerRunner.RunAsync(bundle, ct).ConfigureAwait(false);
            return _lastContainerExecution.Output;
        }

        if (Runtime is null && NativeRoleExecutor is null)
            throw new InvalidOperationException("Worker: model runtime not configured");

        // CF reader pack requires the native role executor — it has no generic-LLM fallback path.
        if (string.Equals(bundle.PackId, CampaignPackCatalog.ContextFabricPackId, StringComparison.OrdinalIgnoreCase)
            && NativeRoleExecutor is null)
            throw new InvalidOperationException(
                "Worker: Context Fabric reader tasks require a native role executor — this host has none configured.");

        var sysPrompt = BuildSystemPrompt(bundle.Role);
        var userMsg   = BuildUserMessage(bundle);

        var messages = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = sysPrompt,  Status = MessageStatus.Complete },
            new() { Role = MessageRole.User,   Content = userMsg,    Status = MessageStatus.Complete },
        };

        if (NativeRoleExecutor is not null)
        {
            try
            {
                return await ExecuteNativeRoleTaskAsync(bundle, messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (HiveNativeRoleAdmissionDeniedException ex)
            {
                var telemetry = DescribeNativeRuntimeTelemetry(bundle.Role);
                var failClosed = bundle.ExecutionKind != HiveExecutionKinds.LegacyAgent || Runtime is null;
                Log($"⚠ [{bundle.Role}] '{bundle.Title}' — native role runtime admission denied: {ex.Message}{telemetry}");
                TaskActivity(bundle.TaskId, $"⚠ Native runtime admission denied: {ex.Message}");
                await PostEventAsync("task_warning",
                    $"⚠ {WorkerId} · [{bundle.Role}] native runtime admission denied: {ex.Message}",
                    bundle.TaskId,
                    ct).ConfigureAwait(false);

                if (failClosed)
                    throw new InvalidOperationException(
                        "Worker: native role runtime admission was denied. Phase 3B does not fall back.",
                        ex);
            }
            catch (Exception ex)
            {
                var telemetry = DescribeNativeRuntimeTelemetry(bundle.Role);
                var failClosed = bundle.ExecutionKind != HiveExecutionKinds.LegacyAgent || Runtime is null;
                Log($"⚠ [{bundle.Role}] '{bundle.Title}' — native role runtime failed: {ex.Message}{telemetry}");
                TaskActivity(bundle.TaskId, $"⚠ Native runtime failed: {ex.Message}");
                await PostEventAsync("task_warning",
                    $"⚠ {WorkerId} · [{bundle.Role}] native runtime failed: {ex.Message}",
                    bundle.TaskId,
                    ct).ConfigureAwait(false);

                if (failClosed)
                    throw new InvalidOperationException(
                        "Worker: native role runtime failed. Phase 3B does not fall back.",
                        ex);
            }
        }

        // Choose model: use hint from Warchief, fall back to local config
        var model = bundle.Role.ToLower() == "researcher"
            ? (string.IsNullOrWhiteSpace(ResearcherModel) ? CoderModel : ResearcherModel)
            : CoderModel;

        if (string.IsNullOrWhiteSpace(model))
            model = bundle.ModelHint;

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Worker: no model configured — set CoderModel in HIVE settings");

        Log($"🐝 Executing [{bundle.Role}] '{bundle.Title}' with {model}…");
        TaskActivity(bundle.TaskId, $"⚡ Running on {WorkerId} with {model}");
        await PostEventAsync("task_executing",
            $"⚡ {WorkerId} · [{bundle.Role}] {bundle.Title} · {model}", bundle.TaskId, ct).ConfigureAwait(false);

        var result = new StringBuilder();
        var fallbackRuntime = Runtime
            ?? throw new InvalidOperationException("Worker: fallback model runtime not configured");

        await foreach (var token in fallbackRuntime.StreamCompletionAsync(model, messages, ct: ct).ConfigureAwait(false))
            result.Append(token);

        Log($"🐝 [{bundle.Role}] '{bundle.Title}' — done ({result.Length} chars)");
        return result.ToString();
    }

    private async Task<string> ExecuteNativeRoleTaskAsync(
        HiveTaskBundle bundle,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        if (NativeRoleExecutor is null)
            throw new InvalidOperationException("Worker: native role runtime not configured");

        Log($"🐝 Executing [{bundle.Role}] '{bundle.Title}' with native role runtime…");
        TaskActivity(bundle.TaskId, $"⚡ Running on {WorkerId} with native role runtime");
        await PostEventAsync("task_executing",
            $"⚡ {WorkerId} · [{bundle.Role}] {bundle.Title} · NativeRoleRuntime",
            bundle.TaskId,
            ct).ConfigureAwait(false);

        // CF-6: the Context Fabric pack bypasses the generic agent/tool-call loop.
        // Dispatch key: NativeRole discriminates reducer / stitcher / verifier / query / reader.
        if (string.Equals(bundle.PackId, CampaignPackCatalog.ContextFabricPackId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(bundle.NativeRole, CampaignPackCatalog.ContextFabricReducerRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                var (corpusMeta, cards) = await FetchReducerInputsAsync(bundle, ct).ConfigureAwait(false);
                _lastAgentExecution = await NativeRoleExecutor.ExecuteContextFabricReducerAsync(
                    bundle, corpusMeta, cards, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(_lastAgentExecution.Output))
                    throw new InvalidOperationException("Context Fabric reducer emitted no reduction nodes.");
                Log($"🐝 [{bundle.Role}] '{bundle.Title}' — Context Fabric reducer built {_lastAgentExecution.Steps} node(s){DescribeNativeRuntimeTelemetry(bundle.Role)}");
                return _lastAgentExecution.Output;
            }

            if (string.Equals(bundle.NativeRole, CampaignPackCatalog.ContextFabricStitcherRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                var (leftCorpus, rightCorpus) = await FetchStitcherInputsAsync(bundle, ct).ConfigureAwait(false);
                _lastAgentExecution = await NativeRoleExecutor.ExecuteContextFabricStitcherAsync(
                    bundle, leftCorpus, rightCorpus, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(_lastAgentExecution.Output))
                    throw new InvalidOperationException("Context Fabric stitcher emitted no stitch result.");
                Log($"🐝 [{bundle.Role}] '{bundle.Title}' — Context Fabric stitcher completed{DescribeNativeRuntimeTelemetry(bundle.Role)}");
                return _lastAgentExecution.Output;
            }

            if (string.Equals(bundle.NativeRole, CampaignPackCatalog.ContextFabricVerifierRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                var (card, sourceCorpus) = await FetchVerifierInputsAsync(bundle, ct).ConfigureAwait(false);
                _lastAgentExecution = await NativeRoleExecutor.ExecuteContextFabricVerifierAsync(
                    bundle, card, sourceCorpus, ct).ConfigureAwait(false);
                Log($"🐝 [{bundle.Role}] '{bundle.Title}' — Context Fabric verifier checked {_lastAgentExecution.Steps} claim(s){DescribeNativeRuntimeTelemetry(bundle.Role)}");
                return _lastAgentExecution.Output;
            }

            if (string.Equals(bundle.NativeRole, CampaignPackCatalog.ContextFabricQueryRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                var (questionId, questionText, queryCorpus, evidenceCard) = await FetchQueryInputsAsync(bundle, ct).ConfigureAwait(false);
                _lastAgentExecution = await NativeRoleExecutor.ExecuteContextFabricQueryAsync(
                    bundle, questionId, questionText, queryCorpus, evidenceCard, ct).ConfigureAwait(false);
                Log($"🐝 [{bundle.Role}] '{bundle.Title}' — Context Fabric query {(_lastAgentExecution.Steps > 0 ? "found evidence" : "no evidence")}{DescribeNativeRuntimeTelemetry(bundle.Role)}");
                return _lastAgentExecution.Output;
            }

            var corpus = await FetchReaderCorpusAsync(bundle, ct).ConfigureAwait(false);
            _lastAgentExecution = await NativeRoleExecutor.ExecuteContextFabricReaderAsync(bundle, corpus, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(_lastAgentExecution.Output))
                throw new InvalidOperationException("Context Fabric reader emitted no evidence card.");

            // Per-node telemetry: record the number of accepted claims so the Warchief's
            // campaign dashboard can show evidence coverage without re-downloading the artifact.
            try
            {
                var card = JsonSerializer.Deserialize<FabricEvidenceCard>(
                    _lastAgentExecution.Output, FabricJson.Options);
                if (card is not null)
                {
                    _readerEvidenceCount = card.Claims.Count;
                }
            }
            catch { /* non-fatal: telemetry must not break task completion */ }

            Log($"🐝 [{bundle.Role}] '{bundle.Title}' — Context Fabric reader accepted segment{DescribeNativeRuntimeTelemetry(bundle.Role)}");
            return _lastAgentExecution.Output;
        }

        if (bundle.ExecutionKind == HiveExecutionKinds.NativeAgent)
        {
            _lastAgentExecution = await NativeRoleExecutor.ExecuteAgentAsync(bundle, messages, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(_lastAgentExecution.Output))
                throw new InvalidOperationException("Native agent loop emitted no final output.");
            Log($"🐝 [{bundle.Role}] '{bundle.Title}' — native agent completed in {_lastAgentExecution.Steps} steps{DescribeNativeRuntimeTelemetry(bundle.Role)}");
            return _lastAgentExecution.Output;
        }

        var result = new StringBuilder();
        await foreach (var token in NativeRoleExecutor.StreamRoleCompletionAsync(bundle.Role, messages, ct).ConfigureAwait(false))
            result.Append(token);

        if (string.IsNullOrWhiteSpace(result.ToString()))
            throw new InvalidOperationException("Native role runtime emitted no output.");

        Log($"🐝 [{bundle.Role}] '{bundle.Title}' — native runtime done ({result.Length} chars){DescribeNativeRuntimeTelemetry(bundle.Role)}");
        return result.ToString();
    }

    private static string BuildSystemPrompt(string role) => role.ToLower() switch
    {
        "researcher" => """
            You are a RESEARCHER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Your job: investigate, discover, and document. Do NOT write final production code.
            Return: relevant library names and versions, key API methods with example usage,
            specific recommendations for the coder, known gotchas and constraints.
            Be thorough — the coder depends entirely on your findings.
            Format your output clearly with headers and bullet points.
            """,

        "coder" or "uideveloper" => """
            You are a CODER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Write production-ready, complete implementation code.
            Use the research findings provided to inform all technology choices.

            Output complete files using this EXACT format:
            ### FILE: path/to/file.ext
            ```language
            (complete file contents here)
            ```

            Include proper error handling. Write ONLY in the language specified in the task.
            Do NOT write snippets — write complete, runnable files.
            """,

        "tester" => """
            You are a TESTER in a distributed multi-agent AI coding system (TheOrc HIVE MIND).
            Review the provided code and deliverable against the task requirements.
            Write a structured test verdict:
              PASS: code is correct, complete, and meets requirements.
              FAIL: identify specific issues, what was expected vs. actual.
            Be precise and actionable — your verdict drives the next iteration.
            """,

        _ => $"You are a specialist agent in TheOrc HIVE MIND. Complete the assigned task thoroughly.",
    };

    private static string BuildUserMessage(HiveTaskBundle bundle)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(bundle.TargetLanguage))
            sb.AppendLine($"⚠ LANGUAGE LOCK: ALL code MUST be written in {bundle.TargetLanguage}. " +
                          $"Do NOT use any other language unless the task explicitly requires it.\n");

        if (bundle.UpstreamArtifacts.Count > 0)
        {
            sb.AppendLine("## Research findings from the Researcher:");
            foreach (var artifact in bundle.UpstreamArtifacts)
            {
                sb.AppendLine($"### {artifact.Source}");
                sb.AppendLine(artifact.Content);
                sb.AppendLine();
            }
            sb.AppendLine("---\n");
        }

        sb.AppendLine("## Your task:");
        sb.AppendLine(bundle.Spec);

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // ── Event side-channel ────────────────────────────────────────────────────

    /// <summary>
    /// POSTs a lifecycle event to the Warchief's /hive/events endpoint.
    /// Non-fatal: network errors are swallowed (the main task channel is the
    /// source of truth; events are best-effort observability only).
    /// "Zero idle chatter": only call this for meaningful state transitions,
    /// never for heartbeats or polling no-ops.
    /// </summary>
    private async Task PostEventAsync(string type, string msg, string taskId, CancellationToken ct)
    {
        try
        {
            var url   = $"{WarchiefUrl.TrimEnd('/')}/hive/events";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HiveEventPost
            {
                Type     = type,
                Msg      = HiveAuthMiddleware.Clamp(msg, 2048) ?? "",  // cap event messages at 2KB
                TaskId   = taskId,
                WorkerId = WorkerId,
            }, _json));

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                { Content = new System.Net.Http.ByteArrayContent(bytes) };
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            SignIfPaired(req, bytes);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { /* non-fatal — observability must not break execution */ }
    }

    private void Log(string msg)          => OnLog?.Invoke(msg);
    private void TaskActivity(string id, string msg) => OnTaskActivity?.Invoke(id, msg);

    private string DescribeNativeRuntimeTelemetry(string hiveRole)
    {
        if (NativeRoleExecutor is null)
            return "";

        try
        {
            return NativeRoleExecutor.DescribeTelemetry(hiveRole);
        }
        catch
        {
            return "";
        }
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        var shutdownTask = Volatile.Read(ref _shutdownTask);
        if (shutdownTask is null)
        {
            var created = ShutdownCoreAsync();
            shutdownTask = Interlocked.CompareExchange(ref _shutdownTask, created, null) ?? created;
        }

        if (ct.CanBeCanceled)
            await shutdownTask.WaitAsync(ct).ConfigureAwait(false);
        else
            await shutdownTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            ShutdownAsync().WaitAsync(DisposeWaitTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Log($"⚠ Worker shutdown timed out after {DisposeWaitTimeout.TotalSeconds:F0}s; continuing close.");
        }
        catch (Exception ex)
        {
            Log($"⚠ Worker shutdown cleanup failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    private async Task ShutdownCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        var runLoopTask = _runLoopTask;
        Stop();

        if (runLoopTask is not null)
        {
            try
            {
                await runLoopTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"⚠ Worker shutdown observed loop failure: {ex.Message}");
            }
            finally
            {
                _runLoopTask = null;
            }
        }

        var nativeRoleExecutor = Interlocked.Exchange(ref _nativeRoleExecutor, null);
        if (nativeRoleExecutor is not null)
        {
            try
            {
                await nativeRoleExecutor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"⚠ Worker shutdown cleanup failed: {ex.Message}");
            }
        }

        Interlocked.Exchange(ref _disposeState, 2);
        GC.SuppressFinalize(this);
    }
}
