// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime v2.0 Phase D (docs/NATIVE_RUNTIME_V2_SPEC.md §3.3): "the run transcript,
/// resolved binding, admission decisions, telemetry snapshots (before/mid/after), tok/s + TTFT,
/// and a pass/fail summary -- same evidence-grade discipline as the CF-7 gate runs." Same JSON
/// evidence-record convention as <see cref="NativeRuntimeFallbackEvidenceStore"/> and
/// <see cref="NativeRuntimeComparisonReportStore"/> (schema_version + timestamp_utc + app_version,
/// written under `.orc/&lt;category&gt;/`) -- a sibling store, not a fork of the pattern, since this
/// records the SUCCESSFUL full-lifecycle case those two don't cover (the fallback store only
/// persists non-success outcomes; the comparison store is native-vs-Ollama parity, a different
/// question).
/// </summary>
public sealed record NativeE2ELaneTelemetrySnapshot(
    string Stage,
    long? TotalBytes,
    long? ReservedBytes,
    long? AvailableBytes,
    int? ResidentActiveCount,
    string? ResidencyStatus);

public sealed record NativeE2ELaneRunResult(
    bool Success,
    string RuntimeName,
    string ResolvedRole,
    string ResolvedBaseModel,
    string? ResolvedAdapter,
    IReadOnlyList<NativeE2ELaneTelemetrySnapshot> TelemetrySnapshots,
    double? TokensPerSecond,
    TimeSpan? TimeToFirstToken,
    long? EstimatedVramBytes,
    string? Output,
    string? ErrorType,
    string? ErrorMessage);

public static class NativeE2ELaneEvidenceStore
{
    private const int OutputLimit = 4096;
    private const int ErrorLimit = 2048;

    private static readonly Regex _controlChars = new(
        "[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F]",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>
    /// Retains evidence for BOTH pass and fail -- unlike <see cref="NativeRuntimeFallbackEvidenceStore"/>,
    /// which only persists non-success (the point of an E2E proof lane is a green run with a
    /// retained artifact, so a successful run's evidence is the primary deliverable, not noise
    /// to discard).
    /// </summary>
    public static string ResolveDirectory(string? workspaceRoot)
    {
        var root = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.Combine(workspaceRoot, ".orc", "native-e2e-lane")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", ".orc", "native-e2e-lane");

        Directory.CreateDirectory(root);
        return root;
    }

    public static async Task<string> WriteAsync(
        NativeE2ELaneRunResult result,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var root = ResolveDirectory(workspaceRoot);
        // One captured instant for both the filename and the record -- using DateTime.Now for
        // the filename while the record's own timestamp_utc used UtcNow meant the two could
        // disagree (and sort inconsistently across machine timezones), CodeRabbit finding.
        var capturedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(root, $"native_e2e_lane_{capturedAt:yyyyMMdd_HHmmss_fff}.json");
        var record = BuildRecord(result, workspaceRoot, capturedAt);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, _json), ct).ConfigureAwait(false);
        return path;
    }

    private static object BuildRecord(NativeE2ELaneRunResult result, string? workspaceRoot, DateTimeOffset capturedAt) => new
    {
        schema_version = "1",
        timestamp_utc = capturedAt.ToString("O"),
        app_version = CurrentAppVersion(),
        workspace_root = workspaceRoot,
        pass = result.Success,
        runtime_name = result.RuntimeName,
        resolved_role = result.ResolvedRole,
        resolved_base_model = result.ResolvedBaseModel,
        resolved_adapter = result.ResolvedAdapter,
        telemetry_snapshots = result.TelemetrySnapshots.Select(s => new
        {
            s.Stage,
            s.TotalBytes,
            s.ReservedBytes,
            s.AvailableBytes,
            s.ResidentActiveCount,
            s.ResidencyStatus,
        }),
        tokens_per_second = result.TokensPerSecond,
        time_to_first_token_ms = result.TimeToFirstToken?.TotalMilliseconds,
        estimated_vram_bytes = result.EstimatedVramBytes,
        output = Sanitize(result.Output, OutputLimit),
        error_type = result.ErrorType,
        error_message = Sanitize(result.ErrorMessage, ErrorLimit),
    };

    private static string CurrentAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(NativeE2ELaneEvidenceStore).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static string? Sanitize(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var cleaned = _controlChars.Replace(value, " ").Trim();
        return cleaned.Length <= maxChars
            ? cleaned
            : cleaned[..maxChars] + "…[truncated]";
    }
}
