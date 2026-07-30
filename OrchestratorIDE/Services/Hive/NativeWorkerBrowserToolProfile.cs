// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Browser;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Deny-by-default navigation policy for <see cref="NativeWorkerBrowserToolProfile"/>
/// (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §2.4, Phase 1b). Unlike the interactive surface
/// (<see cref="OrchestratorIDE.Tools.BrowserTools"/>), there is no approval-queue UI to prompt a
/// live operator on a Warband -- <see cref="HeadlessAgentLoop"/>'s own doc comment states the host
/// owns policy by choosing the supplied tools; this record IS that policy, decided by whoever
/// constructs the task (the Warchief/campaign definition), never by the running task itself.
/// </summary>
/// <param name="AllowedOrigins">
///   Origins ("scheme://host:port") this task's <c>browser_navigate</c> may target. Empty (the
///   default via <see cref="DenyAll"/>) means every navigation is policy-blocked -- a task must be
///   explicitly granted origins to browse anywhere at all, matching
///   <see cref="NativeWorkerToolProfile"/>'s own "no run_shell, no fetch_url" precedent of simply
///   not including capabilities a task wasn't scoped for.
/// </param>
/// <param name="MaxNavigations">Hard cap on navigate calls for this task/session (default 20) --
///   bounds a runaway loop's browsing footprint the same way <c>HeadlessAgentLimits.MaxSteps</c>
///   already bounds step count.</param>
/// <param name="DownloadsAllowed">Whether <c>browser_download</c> is permitted at all for this
///   task (default false -- writing arbitrary downloaded files is a materially different risk
///   than reading pages, and needs its own explicit grant).</param>
public sealed record HeadlessBrowserPolicy(
    IReadOnlyList<string>? AllowedOrigins = null,
    int MaxNavigations = 20,
    bool DownloadsAllowed = false)
{
    public static HeadlessBrowserPolicy DenyAll => new(AllowedOrigins: []);
}

/// <summary>
/// Headless-surface browser automation tools for HIVE native-agent workers
/// (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §2.4, Phase 1b). Same sandboxing discipline as
/// <see cref="NativeWorkerToolProfile"/>: confined to an isolated per-task output directory, no
/// approval queue (matches <see cref="HeadlessAgentLoop"/>'s own "the loop never prompts,
/// auto-approves, or invents shell access" design), policy baked in rather than requested live.
///
/// Deliberately returns STRING results for every policy violation rather than throwing (unlike
/// <see cref="NativeWorkerToolProfile.Resolve"/>'s own throw-on-escape convention): confirmed
/// while implementing this that neither <see cref="HeadlessAgentLoop.ExecuteAsync"/> nor
/// <c>HiveNativeRoleExecutorAdapter.ExecuteAgentAsync</c> wrap an individual
/// <c>HeadlessTool.ExecuteAsync</c> call in a try/catch -- an uncaught exception here would abort
/// the whole multi-step task (caught only much further up, in
/// <c>HiveWorkerAgent.ExecuteTaskAsync</c>'s own outer catch) rather than let the model see a
/// policy message and try something else. Pre-existing behavior in <see cref="NativeWorkerToolProfile"/>
/// too, not introduced here -- but not extended into this new code either.
/// </summary>
public static class NativeWorkerBrowserToolProfile
{
    /// <summary>
    /// Returns an EMPTY list, not a set of tools that fail at call time, when
    /// <see cref="NativeToolCapability.BrowserAutomation"/> isn't currently available (Playwright
    /// browsers not installed) -- matches the interactive surface's own
    /// <c>ToolRegistry.GetForProfile</c> filtering: don't advertise what's known not to work,
    /// rather than a Warband negotiating with the model over a capability the host already knows
    /// it can't satisfy.
    /// </summary>
    /// <returns>
    /// The tool list, plus an <see cref="IAsyncDisposable"/> that tears down the lazily-launched
    /// session (if one was ever created). Unlike the interactive surface's equivalent
    /// (<see cref="OrchestratorIDE.Tools.BrowserTools.Register"/>), a headless task has a natural,
    /// well-defined end-of-scope point -- <c>HiveNativeRoleExecutorAdapter.ExecuteAgentAsync</c>
    /// disposes this in a <c>finally</c> right after <see cref="HeadlessAgentLoop.ExecuteAsync"/>
    /// returns, so there's no equivalent "discarded, leaks until the caller happens to fix it"
    /// risk to accept here -- always dispose the returned handle.
    /// </returns>
    public static (IReadOnlyList<HeadlessTool> Tools, IAsyncDisposable Cleanup) Create(
        string outputDirectory, HeadlessBrowserPolicy? policy = null)
    {
        if (!NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation))
            return ([], NoopDisposable.Instance);

        policy ??= HeadlessBrowserPolicy.DenyAll;
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        var gate = new SemaphoreSlim(1, 1);
        BrowserSession? session = null;
        var navigationCount = 0;

        async Task<BrowserSession> GetOrLaunchSessionAsync(CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                session ??= await BrowserSession.LaunchAsync(ct: ct);
                return session;
            }
            finally
            {
                gate.Release();
            }
        }

        var tools = (IReadOnlyList<HeadlessTool>)
        [
            Tool("browser_navigate", "Navigate the browser to a URL (only origins this task was explicitly granted).",
                new { url = new { type = "string" } }, ["url"], async (args, ct) =>
                {
                    var url = StringArg(args, "url");
                    if (string.IsNullOrWhiteSpace(url))
                        return "[ERROR] browser_navigate requires a non-empty 'url'.";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                        return "[ERROR] browser_navigate requires an absolute URL (with a scheme).";

                    if (Interlocked.Increment(ref navigationCount) > policy.MaxNavigations)
                        return $"[POLICY BLOCKED] This task has exceeded its maximum of {policy.MaxNavigations} navigations.";

                    var origin = $"{parsed.Scheme}://{parsed.Authority}";
                    var allowed = policy.AllowedOrigins is { Count: > 0 } origins
                        && origins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
                    if (!allowed)
                        return $"[POLICY BLOCKED] Origin '{origin}' is not in this task's allowed navigation list.";

                    try
                    {
                        var s = await GetOrLaunchSessionAsync(ct);
                        var title = await s.NavigateAsync(url, ct);
                        return $"[OK] Navigated to {url} -- page title: \"{title}\"";
                    }
                    catch (PlaywrightBrowsersNotInstalledException ex)
                    {
                        NativeToolCapabilities.MarkUnavailable(NativeToolCapability.BrowserAutomation, ex.Message);
                        return $"[UNAVAILABLE] {ex.Message}";
                    }
                }),

            Tool("browser_click", "Click an element on the current page, identified by a CSS selector.",
                new { selector = new { type = "string" } }, ["selector"], async (args, ct) =>
                {
                    var selector = StringArg(args, "selector");
                    if (string.IsNullOrWhiteSpace(selector))
                        return "[ERROR] browser_click requires a non-empty 'selector'.";
                    var s = await GetOrLaunchSessionAsync(ct);
                    await s.ClickAsync(selector, ct);
                    return $"[OK] Clicked '{selector}'.";
                }),

            Tool("browser_type", "Type text into an input element, identified by a CSS selector (replaces any existing value).",
                new { selector = new { type = "string" }, text = new { type = "string" } },
                ["selector", "text"], async (args, ct) =>
                {
                    var selector = StringArg(args, "selector");
                    var text = StringArg(args, "text");
                    if (string.IsNullOrWhiteSpace(selector))
                        return "[ERROR] browser_type requires a non-empty 'selector'.";
                    var s = await GetOrLaunchSessionAsync(ct);
                    await s.TypeAsync(selector, text, ct);
                    return $"[OK] Typed into '{selector}'.";
                }),

            Tool("browser_wait", "Wait for an element to become visible, bounded by a timeout (default 10s, max 60s).",
                new { selector = new { type = "string" }, timeout_seconds = new { type = "number" } },
                ["selector"], async (args, ct) =>
                {
                    var selector = StringArg(args, "selector");
                    if (string.IsNullOrWhiteSpace(selector))
                        return "[ERROR] browser_wait requires a non-empty 'selector'.";
                    var timeoutSeconds = Math.Clamp(NumberArg(args, "timeout_seconds", 10), 1, 60);
                    var s = await GetOrLaunchSessionAsync(ct);
                    var found = await s.WaitForAsync(selector, TimeSpan.FromSeconds(timeoutSeconds), ct);
                    return found
                        ? $"[OK] '{selector}' appeared."
                        : $"[TIMEOUT] '{selector}' did not appear within {timeoutSeconds}s.";
                }),

            Tool("browser_extract", "Extract visible text from the current page, optionally scoped to a CSS selector.",
                new { selector = new { type = "string" } }, [], async (args, ct) =>
                {
                    var selector = args.TryGetValue("selector", out var sv) ? sv?.ToString() : null;
                    var s = await GetOrLaunchSessionAsync(ct);
                    var text = await s.ExtractTextAsync(string.IsNullOrWhiteSpace(selector) ? null : selector, ct);
                    const int maxChars = 8_000;
                    if (text.Length > maxChars) text = text[..maxChars] + "\n… [truncated]";
                    return text;
                }),

            Tool("browser_screenshot", "Capture a full-page screenshot and save it inside the isolated work area.",
                new { path = new { type = "string" } }, [], async (args, ct) =>
                {
                    var relPath = args.TryGetValue("path", out var pv) && !string.IsNullOrWhiteSpace(pv?.ToString())
                        ? pv!.ToString()!
                        : $"browser-screenshots/screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png";
                    var resolved = Path.GetFullPath(Path.Combine(root, relPath));
                    if (!PathSandbox.IsInsideSandbox(resolved, root))
                        return "[POLICY BLOCKED] browser_screenshot: path escapes the isolated work area.";
                    var s = await GetOrLaunchSessionAsync(ct);
                    var written = await s.ScreenshotAsync(resolved, ct);
                    return $"[OK] Screenshot saved to {Path.GetRelativePath(root, written)}";
                }),

            Tool("browser_download", "Click an element that triggers a download and save the file inside the isolated work area.",
                new { trigger_selector = new { type = "string" }, directory = new { type = "string" } },
                ["trigger_selector"], async (args, ct) =>
                {
                    if (!policy.DownloadsAllowed)
                        return "[POLICY BLOCKED] Downloads are not permitted for this task.";
                    var triggerSelector = StringArg(args, "trigger_selector");
                    if (string.IsNullOrWhiteSpace(triggerSelector))
                        return "[ERROR] browser_download requires a non-empty 'trigger_selector'.";
                    var relDir = args.TryGetValue("directory", out var dv) && !string.IsNullOrWhiteSpace(dv?.ToString())
                        ? dv!.ToString()!
                        : "downloads";
                    var resolvedDir = Path.GetFullPath(Path.Combine(root, relDir));
                    if (!PathSandbox.IsInsideSandbox(resolvedDir, root))
                        return "[POLICY BLOCKED] browser_download: directory escapes the isolated work area.";
                    var s = await GetOrLaunchSessionAsync(ct);
                    var saved = await s.DownloadAsync(triggerSelector, resolvedDir, ct);
                    return $"[OK] Downloaded to {Path.GetRelativePath(root, saved)}";
                }),
        ];

        return (tools, new SessionCleanup(() => session, s => session = s));
    }

    /// <summary>Disposes whichever <see cref="BrowserSession"/> this <see cref="Create"/> call's
    /// closure ends up lazily launching, if any. Unlike
    /// <see cref="OrchestratorIDE.Tools.BrowserTools"/>'s own equivalent disposable, this does NOT
    /// acquire <c>gate</c> before disposing -- safe only because of how this type is actually
    /// used: one <see cref="Create"/> call feeds exactly one, fully-awaited
    /// <see cref="HeadlessAgentLoop.ExecuteAsync"/> run (which processes tool calls sequentially,
    /// never concurrently) before <c>HiveNativeRoleExecutorAdapter.ExecuteAgentAsync</c> disposes
    /// this in its own <c>finally</c>, so there is structurally no in-flight tool call left when
    /// disposal runs. If a future caller ever shares one <see cref="Create"/> result across
    /// multiple concurrent loop runs, this reasoning would need revisiting.</summary>
    private sealed class SessionCleanup(Func<BrowserSession?> getSession, Action<BrowserSession?> setSession)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (getSession() is { } session)
            {
                await session.DisposeAsync();
                setSession(null);
            }
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static HeadlessTool Tool(string name, string description, object properties,
        IReadOnlyList<string> required,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> execute) =>
        new(name, HeadlessAgentLoop.BuildToolSchema(name, description,
            properties.GetType().GetProperties().ToDictionary(p => p.Name, p => (object)p.GetValue(properties)!), required), execute);

    private static string StringArg(IReadOnlyDictionary<string, object?> args, string name, string fallback = "") =>
        args.TryGetValue(name, out var value) ? value?.ToString() ?? fallback : fallback;

    private static double NumberArg(IReadOnlyDictionary<string, object?> args, string name, double fallback)
    {
        if (!args.TryGetValue(name, out var v) || v is null) return fallback;
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            System.Text.Json.JsonElement je when je.TryGetDouble(out var jd) => jd,
            string s when double.TryParse(s, out var sd) => sd,
            _ => fallback,
        };
    }
}
