// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

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
