// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

public static class NativeRuntimeTestPrompt
{
    public const string PromptCategory = "settings_native_test";
    public const string PromptText =
        "Reply with exactly two lines:\n" +
        "native runtime ok\n" +
        "color=<one color>";

    public static IReadOnlyList<AgentMessage> BuildMessages(string promptText = PromptText) =>
    [
        new()
        {
            Role = MessageRole.System,
            Content = "You are a deterministic runtime smoke-test assistant. Follow the user's format exactly.",
            Status = MessageStatus.Complete,
        },
        new()
        {
            Role = MessageRole.User,
            Content = promptText,
            Status = MessageStatus.Complete,
        },
    ];
}

public enum NativeRuntimeTestOutcomeKind
{
    NativeSuccess,
    NativeFailedFallbackAcceptedOllamaSuccess,
    NativeFailedFallbackAcceptedOllamaFailed,
    NativeFailedFallbackDeclined,
}

public sealed record NativeRuntimeTestAttempt(
    string RuntimeName,
    string ModelRef,
    string PromptCategory,
    string PromptText,
    bool Success,
    string? Output,
    RuntimeHealth Health,
    RuntimeStats Stats,
    string? ErrorType = null,
    string? ErrorMessage = null);

public sealed record NativeRuntimeTestOutcome(
    NativeRuntimeTestOutcomeKind Kind,
    NativeRuntimeTestAttempt NativeAttempt,
    NativeRuntimeTestAttempt? FallbackAttempt = null);

public static class NativeRuntimeTestRunner
{
    public static async Task<NativeRuntimeTestAttempt> RunRoleAsync(
        IRoleRuntime runtime,
        RuntimeRole role,
        string modelRef,
        string promptCategory = NativeRuntimeTestPrompt.PromptCategory,
        string promptText = NativeRuntimeTestPrompt.PromptText,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        var messages = NativeRuntimeTestPrompt.BuildMessages(promptText);
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in runtime.StreamRoleCompletionAsync(
                               role,
                               messages,
                               temperature: 0.0,
                               maxTokens: 64,
                               ct: ct))
            {
                sb.Append(token);
                onToken?.Invoke(token);
            }

            var output = sb.ToString();
            var success = !string.IsNullOrWhiteSpace(output);
            var health = runtime.GetHealth(role);
            var stats = runtime.GetStats(role);

            return new NativeRuntimeTestAttempt(
                runtime.RuntimeName,
                modelRef,
                promptCategory,
                promptText,
                Success: success,
                Output: output,
                Health: health,
                Stats: stats,
                ErrorType: success ? null : "EmptyOutput",
                ErrorMessage: success ? null : "Runtime completed without emitting any output.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NativeRuntimeTestAttempt(
                runtime.RuntimeName,
                modelRef,
                promptCategory,
                promptText,
                Success: false,
                Output: null,
                Health: SafeRoleHealth(runtime, role),
                Stats: SafeRoleStats(runtime, role),
                ErrorType: ex.GetType().Name,
                ErrorMessage: ex.Message);
        }
    }

    public static async Task<NativeRuntimeTestAttempt> RunLocalAsync(
        string baseGgufPath,
        string promptCategory = NativeRuntimeTestPrompt.PromptCategory,
        string promptText = NativeRuntimeTestPrompt.PromptText,
        RuntimeOptions? options = null,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        await using var runtime = new LLamaSharpRuntime();
        var load = await runtime.LoadModelAsync(baseGgufPath, options: options, ct: ct);
        if (!load.Success)
        {
            var health = runtime.GetHealth();
            var stats = runtime.GetStats();
            return new NativeRuntimeTestAttempt(
                runtime.RuntimeName,
                baseGgufPath,
                promptCategory,
                promptText,
                Success: false,
                Output: null,
                Health: health,
                Stats: stats,
                ErrorType: "LoadModelFailed",
                ErrorMessage: load.Message ?? "Model load failed.");
        }

        return await RunRuntimeAsync(
            runtime,
            load.ModelRef,
            promptCategory,
            promptText,
            onToken,
            ct);
    }

    public static async Task<NativeRuntimeTestAttempt> RunRuntimeAsync(
        IModelRuntime runtime,
        string model,
        string promptCategory = NativeRuntimeTestPrompt.PromptCategory,
        string promptText = NativeRuntimeTestPrompt.PromptText,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        var messages = NativeRuntimeTestPrompt.BuildMessages(promptText);
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in runtime.StreamCompletionAsync(model, messages, temperature: 0.0, maxTokens: 64, ct: ct))
            {
                sb.Append(token);
                onToken?.Invoke(token);
            }

            var output = sb.ToString();
            var success = !string.IsNullOrWhiteSpace(output);
            var health = runtime.GetHealth();
            var stats = runtime.GetStats();

            return new NativeRuntimeTestAttempt(
                runtime.RuntimeName,
                model,
                promptCategory,
                promptText,
                Success: success,
                Output: output,
                Health: health,
                Stats: stats,
                ErrorType: success ? null : "EmptyOutput",
                ErrorMessage: success ? null : "Runtime completed without emitting any output.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new NativeRuntimeTestAttempt(
                runtime.RuntimeName,
                model,
                promptCategory,
                promptText,
                Success: false,
                Output: null,
                Health: SafeHealth(runtime),
                Stats: SafeStats(runtime),
                ErrorType: ex.GetType().Name,
                ErrorMessage: ex.Message);
        }
    }

    private static RuntimeHealth SafeHealth(IModelRuntime runtime)
    {
        try { return runtime.GetHealth(); }
        catch
        {
            return new RuntimeHealth(false, runtime.RuntimeName, Message: "Health probe failed after runtime exception.");
        }
    }

    private static RuntimeStats SafeStats(IModelRuntime runtime)
    {
        try { return runtime.GetStats(); }
        catch
        {
            return new RuntimeStats(runtime.RuntimeName);
        }
    }

    private static RuntimeHealth SafeRoleHealth(IRoleRuntime runtime, RuntimeRole role)
    {
        try { return runtime.GetHealth(role); }
        catch
        {
            return new RuntimeHealth(false, runtime.RuntimeName, Message: "Health probe failed after role-runtime exception.");
        }
    }

    private static RuntimeStats SafeRoleStats(IRoleRuntime runtime, RuntimeRole role)
    {
        try { return runtime.GetStats(role); }
        catch
        {
            return new RuntimeStats(runtime.RuntimeName);
        }
    }
}

public static class NativeRuntimeFallbackCoordinator
{
    public static async Task<NativeRuntimeTestOutcome> ExecuteAsync(
        Func<CancellationToken, Task<NativeRuntimeTestAttempt>> runNativeAsync,
        Func<NativeRuntimeTestAttempt, CancellationToken, Task<bool>> confirmFallbackAsync,
        Func<CancellationToken, Task<NativeRuntimeTestAttempt>> runFallbackAsync,
        CancellationToken ct = default)
    {
        var nativeAttempt = await runNativeAsync(ct);
        if (nativeAttempt.Success)
            return new NativeRuntimeTestOutcome(NativeRuntimeTestOutcomeKind.NativeSuccess, nativeAttempt);

        if (!await confirmFallbackAsync(nativeAttempt, ct))
            return new NativeRuntimeTestOutcome(NativeRuntimeTestOutcomeKind.NativeFailedFallbackDeclined, nativeAttempt);

        var fallbackAttempt = await runFallbackAsync(ct);
        return new NativeRuntimeTestOutcome(
            fallbackAttempt.Success
                ? NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaSuccess
                : NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaFailed,
            nativeAttempt,
            fallbackAttempt);
    }
}

public static class NativeRuntimeFallbackEvidenceStore
{
    private const int PromptLimit = 4096;
    private const int OutputLimit = 8192;
    private const int ErrorLimit = 2048;

    private static readonly Regex _controlChars = new(
        "[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F]",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static bool ShouldPersist(NativeRuntimeTestOutcome outcome) =>
        outcome.Kind != NativeRuntimeTestOutcomeKind.NativeSuccess;

    public static string ResolveDirectory(string? workspaceRoot)
    {
        var root = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.Combine(workspaceRoot, ".orc", "runtime-fallback")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", ".orc", "runtime-fallback");

        Directory.CreateDirectory(root);
        return root;
    }

    public static async Task<string?> WriteAsync(
        NativeRuntimeTestOutcome outcome,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        if (!ShouldPersist(outcome))
            return null;

        var root = ResolveDirectory(workspaceRoot);
        var path = Path.Combine(root, $"runtime_fallback_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
        var record = BuildRecord(outcome, workspaceRoot);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, _json), ct);
        return path;
    }

    private static object BuildRecord(NativeRuntimeTestOutcome outcome, string? workspaceRoot) => new
    {
        schema_version = "1",
        timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        app_version = CurrentAppVersion(),
        workspace_root = workspaceRoot,
        outcome = outcome.Kind.ToString(),
        prompt_category = outcome.NativeAttempt.PromptCategory,
        prompt_text = Sanitize(outcome.NativeAttempt.PromptText, PromptLimit),
        attempted_runtime = outcome.NativeAttempt.RuntimeName,
        fallback_runtime = outcome.FallbackAttempt?.RuntimeName,
        model_ref = outcome.NativeAttempt.ModelRef,
        native = new
        {
            success = outcome.NativeAttempt.Success,
            output = Sanitize(outcome.NativeAttempt.Output, OutputLimit),
            error_type = Sanitize(outcome.NativeAttempt.ErrorType, ErrorLimit),
            error_message = Sanitize(outcome.NativeAttempt.ErrorMessage, ErrorLimit),
            health = new
            {
                outcome.NativeAttempt.Health.IsAvailable,
                outcome.NativeAttempt.Health.RuntimeName,
                outcome.NativeAttempt.Health.ActiveModel,
                Message = Sanitize(outcome.NativeAttempt.Health.Message, ErrorLimit),
            },
            stats = new
            {
                outcome.NativeAttempt.Stats.RuntimeName,
                outcome.NativeAttempt.Stats.ActiveModel,
                outcome.NativeAttempt.Stats.TokensPerSecond,
                last_time_to_first_token_ms = outcome.NativeAttempt.Stats.LastTimeToFirstToken?.TotalMilliseconds,
                outcome.NativeAttempt.Stats.EstimatedVramBytes,
            },
        },
        fallback = outcome.FallbackAttempt is null ? null : new
        {
            success = outcome.FallbackAttempt.Success,
            output = Sanitize(outcome.FallbackAttempt.Output, OutputLimit),
            error_type = Sanitize(outcome.FallbackAttempt.ErrorType, ErrorLimit),
            error_message = Sanitize(outcome.FallbackAttempt.ErrorMessage, ErrorLimit),
            health = new
            {
                outcome.FallbackAttempt.Health.IsAvailable,
                outcome.FallbackAttempt.Health.RuntimeName,
                outcome.FallbackAttempt.Health.ActiveModel,
                Message = Sanitize(outcome.FallbackAttempt.Health.Message, ErrorLimit),
            },
            stats = new
            {
                outcome.FallbackAttempt.Stats.RuntimeName,
                outcome.FallbackAttempt.Stats.ActiveModel,
                outcome.FallbackAttempt.Stats.TokensPerSecond,
                last_time_to_first_token_ms = outcome.FallbackAttempt.Stats.LastTimeToFirstToken?.TotalMilliseconds,
                outcome.FallbackAttempt.Stats.EstimatedVramBytes,
            },
        },
    };

    private static string CurrentAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(NativeRuntimeFallbackEvidenceStore).Assembly;
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
