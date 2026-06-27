// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.Hive;

public sealed record ContainerPackExecution(
    string Output,
    string OutputDirectory,
    int ExitCode,
    string ContainerDigest,
    IReadOnlyDictionary<string, string> InputDigests);

public interface IContainerPackRunner
{
    Task<ContainerPackExecution> RunAsync(HiveTaskBundle bundle, CancellationToken ct);
}

/// <summary>Runs only locally trusted pack manifests using a rootless Docker/Podman CLI.</summary>
public sealed class ContainerPackRunner : IContainerPackRunner
{
    private static readonly Regex ImageDigestPattern = new("@sha256:[a-f0-9]{64}$", RegexOptions.Compiled);
    private readonly string _engine;
    private readonly string _workspaceRoot;
    private readonly ContentAddressedStore _inputCache;
    private readonly IReadOnlyDictionary<string, PackManifest> _packs;

    public ContainerPackRunner(string engine, string workspaceRoot,
        IEnumerable<PackManifest> trustedPacks)
    {
        if (engine is not ("docker" or "podman"))
            throw new ArgumentException("Only docker or podman are supported.", nameof(engine));
        _engine = engine;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _inputCache = new ContentAddressedStore(Path.Combine(_workspaceRoot, ".orc", "campaign-inputs"));
        _packs = trustedPacks.Where(p => p.BuiltIn)
            .ToDictionary(p => $"{p.PackId}@{p.Version}", StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ContainerPackExecution> RunAsync(HiveTaskBundle bundle, CancellationToken ct)
    {
        if (!_packs.TryGetValue($"{bundle.PackId}@{bundle.PackVersion}", out var pack))
            throw new InvalidOperationException("Pack is not installed or trusted on this Warband.");
        if (!ImageDigestPattern.IsMatch(pack.ImageDigest))
            throw new InvalidOperationException("Trusted pack image must be pinned by a SHA-256 digest.");
        if (pack.NetworkDuringExecution)
            throw new InvalidOperationException("Phase 3B container packs may not use network during execution.");

        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in bundle.InputArtifacts)
        {
            await EnsureInputAsync(input, ct).ConfigureAwait(false);
            inputs[input.Name] = input.DigestSha256;
        }

        var campaign = SafeSegment(bundle.CampaignId);
        var unit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDir = Path.Combine(_workspaceRoot, ".orc", "container-work", campaign, unit, "output");
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo(_engine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspaceRoot,
        };
        Add(psi, "run", "--rm", "--network", "none", "--read-only", "--pids-limit", "256",
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--cpus", Math.Max(1, bundle.Requirements.MinCpuCores).ToString(),
            "--memory", $"{Math.Max(512, bundle.Requirements.MinMemoryMb)}m",
            "--mount", $"type=bind,src={outputDir},dst=/output");

        var inputIndex = 0;
        foreach (var input in bundle.InputArtifacts)
        {
            var source = _inputCache.GetPath(input.DigestSha256);
            Add(psi, "--mount", $"type=bind,src={source},dst=/input/{inputIndex++},readonly");
        }
        if (bundle.Requirements.MinVramMb > 0) Add(psi, "--gpus", "all");
        Add(psi, pack.ImageDigest);

        foreach (var (key, value) in bundle.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var flag = "--" + key.TrimStart('-');
            if (!pack.AllowedArguments.Contains(flag, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Pack parameter '{key}' is not allowlisted.");
            Add(psi, flag);
            if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                Add(psi, JsonScalar(value));
            else if (!value.GetBoolean())
                psi.ArgumentList.RemoveAt(psi.ArgumentList.Count - 1);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Container engine failed to start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(pack.MaxRuntimeSeconds,
            Math.Max(1, bundle.TimeoutMs / 1000))));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var totalOutput = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
            .Sum(p => new FileInfo(p).Length);
        if (totalOutput > pack.MaxOutputBytes)
            throw new InvalidDataException("Container pack exceeded its output quota.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Container pack failed with exit {process.ExitCode}: {Limit(stderr, 8000)}");

        return new ContainerPackExecution(
            Limit(stdout + (stderr.Length > 0 ? "\n[stderr]\n" + stderr : ""), 64_000),
            outputDir,
            process.ExitCode,
            pack.ImageDigest[(pack.ImageDigest.IndexOf("sha256:", StringComparison.Ordinal) + 7)..],
            inputs);
    }

    private async Task EnsureInputAsync(ArtifactRef input, CancellationToken ct)
    {
        if (_inputCache.Has(input.DigestSha256)) return;
        if (!Uri.TryCreate(input.SourceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Input '{input.Name}' requires an HTTPS source URI.");

        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        var offset = _inputCache.GetResumeOffset(input.DigestSha256);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var expectedResponseBytes = input.SizeBytes - offset;
        if (input.SizeBytes <= 0 || response.Content.Headers.ContentLength is { } length && length != expectedResponseBytes)
            throw new InvalidDataException("Input size does not match its signed manifest.");
        if (offset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            throw new InvalidDataException("Input server did not honor the resume range.");

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[ContentAddressedStore.MaxChunkBytes];
        while (offset < input.SizeBytes)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, input.SizeBytes - offset)), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Public input ended before declared size.");
            await _inputCache.WriteChunkAsync(input.DigestSha256, offset, input.SizeBytes,
                buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            offset += read;
        }
    }

    private static void Add(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }

    private static string JsonScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => throw new InvalidOperationException("Pack parameters must be scalar values."),
    };

    private static string SafeSegment(string value)
    {
        var safe = new string((value ?? "").Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(96).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "\n[truncated]";
    private static void TryKill(Process process) { try { process.Kill(entireProcessTree: true); } catch { } }
}
