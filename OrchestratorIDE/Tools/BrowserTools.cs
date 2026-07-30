// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Browser;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Tools;

/// <summary>
/// Interactive-surface browser automation tools (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §2.3,
/// Phase 1a). Same registration shape as <see cref="FileTools"/>/<see cref="ShellTools"/>: a
/// static <c>Register</c> producing <see cref="ToolDefinition"/>s with closure-captured state.
///
/// One <see cref="BrowserSession"/> per <c>Register</c> call (one per conversation/session, per
/// spec §2.2 -- no pooling), lazily launched on the FIRST actual browser tool call rather than at
/// registration time. Capability detection (is Playwright installed at all) runs once, in the
/// background, via a separate throwaway probe session -- kept deliberately distinct from the real
/// lazily-launched session so a conversation that never touches a browser tool never pays the cost
/// of a persistent browser process "just in case."
///
/// <b>Known deferred limitation</b> (documented, not silently skipped): <see cref="Register"/>
/// returns an <see cref="IAsyncDisposable"/> for the lazily-launched session specifically so a
/// caller CAN tear it down -- <c>MainWindow.RegisterAllTools</c> does, disposing the prior call's
/// handle before every re-registration (workspace switch), closing the most impactful leak (a
/// real, repeatedly-called production path). <c>SwarmSession</c>/<c>OrcChatToolCatalog</c>'s own
/// per-task/per-turn throwaway registrations still discard the handle -- acceptable for Phase 1a
/// (each is a short-lived registry, not repeatedly reused for the app's whole runtime the way
/// MainWindow's is), but genuine per-conversation lifecycle tied to those surfaces' own natural
/// end-of-scope points is real follow-up work, not solved here.
/// </summary>
public static class BrowserTools
{
    private const int MaxExtractedChars = 8_000;

    /// <param name="onSandboxBypass">
    ///   Called when a screenshot/download destination resolves outside the workspace sandbox.
    ///   Signature matches <see cref="ShellTools.Register"/>'s own convention:
    ///   <c>(toolName, escapedPath, sandboxRoot, ct) → Task&lt;bool&gt;</c>.
    /// </param>
    /// <param name="requireApprovalForNavigateAndDownload">
    ///   Default true (matches ShellTools' own unconditional-approval posture) for callers with a
    ///   real approval-queue UI wired (MainWindow's <c>_approvals</c>). Pass false for a
    ///   throwaway/no-UI <see cref="ToolRegistry"/>+<see cref="ApprovalQueue"/> pair (e.g.
    ///   <c>OrcChatToolCatalog.CreateWorkspaceTools</c>'s own per-call registry, which has no
    ///   <c>ApprovalRequested</c> subscriber) -- <c>Guarded</c>, that queue's default trust level,
    ///   would otherwise leave <c>RequestApprovalAsync</c> awaiting a
    ///   <c>TaskCompletionSource</c> nothing ever resolves, hanging the tool call forever. Same
    ///   "null/false means proceed without gating in a no-UI context" precedent
    ///   <see cref="FileTools.Register"/>'s own <c>onDiffPreview</c> parameter already
    ///   established for this exact call site.
    /// </param>
    /// <returns>
    /// An <see cref="IAsyncDisposable"/> that disposes the lazily-launched session, if one was
    /// ever created, when called. Production callers (MainWindow, SwarmSession,
    /// OrcChatToolCatalog) can discard this today per this class's own "Known deferred
    /// limitation" note above -- but tests (BrowserToolsTests.cs) use it in their own TearDown to
    /// avoid leaking a real Chromium process per test run (grok-review MINOR: an undisposed
    /// session can also hold file handles open under a test's own temp-directory cleanup).
    /// </returns>
    public static IAsyncDisposable Register(ToolRegistry registry, string workspaceRoot,
        Func<string, string, string, CancellationToken, Task<bool>>? onSandboxBypass = null,
        bool requireApprovalForNavigateAndDownload = true)
    {
        var gate = new SemaphoreSlim(1, 1);
        BrowserSession? session = null;

        // Fire-and-forget, non-blocking (grok-review-informed design decision, see the spec's
        // Phase 0/1a traceability notes): a synchronous probe here would block whichever thread
        // calls Register() for the ~1-2s a real browser launch+dispose takes. The tools below
        // still carry RequiredCapability -- until this resolves, they simply aren't advertised
        // yet, which self-corrects within a couple of seconds of app/session startup rather than
        // ever silently misreporting availability.
        _ = DetectCapabilityInBackgroundAsync();

        registry.Register(new ToolDefinition
        {
            Name = "browser_navigate",
            Description = "Navigate the browser to a URL and return the resulting page title.",
            Parameters = new()
            {
                ["url"] = new("string", "The absolute URL to navigate to (must include a scheme, e.g. https://)."),
            },
            Required = ["url"],
            // Every navigation prompts (not just first-visit-to-a-new-origin) -- a deliberate
            // simplification vs. this spec's original §2.3 design (per-origin approval tracking
            // needs dynamic, argument-dependent approval decisions ToolRegistry.ExecuteAsync's
            // fixed pre-handler RequiresApproval check can't express without new plumbing this
            // round doesn't add). Conservative and safe by construction: over-prompting, not
            // under-prompting. Matches ShellTools' own "every call requires approval" precedent.
            RequiresApproval = requireApprovalForNavigateAndDownload,
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var url = StringArg(args, "url");
                if (string.IsNullOrWhiteSpace(url))
                    return "[ERROR] browser_navigate requires a non-empty 'url'.";
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
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_click",
            Description = "Click an element on the current page, identified by a CSS selector.",
            Parameters = new()
            {
                ["selector"] = new("string", "CSS selector of the element to click."),
            },
            Required = ["selector"],
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var selector = StringArg(args, "selector");
                if (string.IsNullOrWhiteSpace(selector))
                    return "[ERROR] browser_click requires a non-empty 'selector'.";
                var s = await GetOrLaunchSessionAsync(ct);
                await s.ClickAsync(selector, ct);
                return $"[OK] Clicked '{selector}'.";
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_type",
            Description = "Type text into an input element, identified by a CSS selector (replaces any existing value).",
            Parameters = new()
            {
                ["selector"] = new("string", "CSS selector of the input element."),
                ["text"]     = new("string", "The text to type."),
            },
            Required = ["selector", "text"],
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var selector = StringArg(args, "selector");
                var text     = StringArg(args, "text");
                if (string.IsNullOrWhiteSpace(selector))
                    return "[ERROR] browser_type requires a non-empty 'selector'.";
                var s = await GetOrLaunchSessionAsync(ct);
                await s.TypeAsync(selector, text, ct);
                return $"[OK] Typed into '{selector}'.";
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_wait",
            Description = "Wait for an element to become visible on the page, bounded by a timeout (default 10s, max 60s).",
            Parameters = new()
            {
                ["selector"]        = new("string", "CSS selector to wait for."),
                ["timeout_seconds"] = new("number", "Maximum seconds to wait (default 10, max 60)."),
            },
            Required = ["selector"],
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
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
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_extract",
            Description = "Extract visible text from the current page, optionally scoped to a CSS selector (omit for the whole page).",
            Parameters = new()
            {
                ["selector"] = new("string", "Optional CSS selector to scope extraction to; omit for the full page body."),
            },
            Required = [],
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var selector = args.TryGetValue("selector", out var sv) ? sv?.ToString() : null;
                var s = await GetOrLaunchSessionAsync(ct);
                var text = await s.ExtractTextAsync(string.IsNullOrWhiteSpace(selector) ? null : selector, ct);
                if (text.Length > MaxExtractedChars)
                    text = text[..MaxExtractedChars] + "\n… [truncated]";
                return text;
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_screenshot",
            Description = "Capture a full-page screenshot of the current page and save it inside the workspace.",
            Parameters = new()
            {
                ["path"] = new("string",
                    "Relative path inside the workspace to save the PNG " +
                    "(default: browser-screenshots/<timestamp>.png)."),
            },
            Required = [],
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var relPath = args.TryGetValue("path", out var pv) && !string.IsNullOrWhiteSpace(pv?.ToString())
                    ? pv!.ToString()!
                    : $"browser-screenshots/screenshot-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png";
                var resolved = ResolvePath(workspaceRoot, relPath);
                if (!PathSandbox.IsInsideSandbox(resolved, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("browser_screenshot", resolved, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] browser_screenshot: '{resolved}' is outside the workspace '{workspaceRoot}'.";
                }
                var s = await GetOrLaunchSessionAsync(ct);
                var written = await s.ScreenshotAsync(resolved, ct);
                return $"[OK] Screenshot saved to {Path.GetRelativePath(workspaceRoot, written)}";
            },
        });

        registry.Register(new ToolDefinition
        {
            Name = "browser_download",
            Description = "Click an element that triggers a download and save the resulting file inside the workspace.",
            Parameters = new()
            {
                ["trigger_selector"] = new("string", "CSS selector of the element that triggers the download when clicked."),
                ["directory"]        = new("string", "Relative directory inside the workspace to save into (default: downloads/)."),
            },
            Required = ["trigger_selector"],
            // Always requires approval -- same "conservative, over-prompting" posture as
            // browser_navigate above, and matches write_file's own caution around writing to disk.
            RequiresApproval = requireApprovalForNavigateAndDownload,
            RequiredCapability = NativeToolCapability.BrowserAutomation,
            Handler = async (args, ct) =>
            {
                var triggerSelector = StringArg(args, "trigger_selector");
                if (string.IsNullOrWhiteSpace(triggerSelector))
                    return "[ERROR] browser_download requires a non-empty 'trigger_selector'.";
                var relDir = args.TryGetValue("directory", out var dv) && !string.IsNullOrWhiteSpace(dv?.ToString())
                    ? dv!.ToString()!
                    : "downloads";
                var resolvedDir = ResolvePath(workspaceRoot, relDir);
                if (!PathSandbox.IsInsideSandbox(resolvedDir, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("browser_download", resolvedDir, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] browser_download: '{resolvedDir}' is outside the workspace '{workspaceRoot}'.";
                }
                var s = await GetOrLaunchSessionAsync(ct);
                // BrowserSession.DownloadAsync independently re-confirms the saved file stays
                // inside resolvedDir regardless of what the page's own suggested filename claims
                // (its own doc comment covers why) -- this sandbox check above covers the
                // OPERATOR-supplied 'directory' argument itself, a different trust boundary.
                var saved = await s.DownloadAsync(triggerSelector, resolvedDir, ct);
                return $"[OK] Downloaded to {Path.GetRelativePath(workspaceRoot, saved)}";
            },
        });

        return new SessionDisposable(() => session, s => session = s, gate);

        async Task DetectCapabilityInBackgroundAsync()
        {
            // Early-out when already confirmed available (grok-review MINOR): without this, every
            // Register() call (MainWindow's chat/agent registry, SwarmSession's per-task worker
            // registry, OrcChatToolCatalog's per-turn throwaway registry) independently launches
            // its OWN throwaway probe, wastefully re-launching a browser repeatedly AND risking a
            // later probe's transient failure overwriting an already-correct "available" state
            // with a spurious "unavailable" one (flapping). Does not fully close the narrower
            // "two truly simultaneous FIRST-EVER calls" race -- both would independently launch
            // one probe each -- but both would observe the same real environment and reach the
            // same conclusion, which isn't the flapping failure mode this guards against.
            if (NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation)) return;
            try
            {
                await using var probe = await BrowserSession.LaunchAsync();
                NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
            }
            catch (PlaywrightBrowsersNotInstalledException ex)
            {
                NativeToolCapabilities.MarkUnavailable(NativeToolCapability.BrowserAutomation, ex.Message);
            }
            catch (Exception ex)
            {
                NativeToolCapabilities.MarkUnavailable(NativeToolCapability.BrowserAutomation,
                    $"Browser automation failed to initialize: {ex.Message}");
            }
        }

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
    }

    /// <summary>Disposes whichever <see cref="BrowserSession"/> <see cref="Register"/>'s closure
    /// ends up lazily launching, if any -- <paramref name="getSession"/> is evaluated at dispose
    /// time (not capture time) since the session may not exist yet when <see cref="Register"/>
    /// returns.
    ///
    /// <b>Known residual race</b> (grok-review BLOCKER, partially mitigated, not fully closed):
    /// acquiring <paramref name="gate"/> before disposing serializes against
    /// <c>GetOrLaunchSessionAsync</c>'s own session-ACQUISITION moment (the two can no longer
    /// interleave at that specific point), but does NOT protect a call already past acquisition
    /// and mid-flight inside a real, possibly multi-second Playwright operation (e.g. a slow
    /// <c>NavigateAsync</c>) at the exact moment a caller (<c>MainWindow.RegisterAllTools</c>, on
    /// a workspace switch) disposes. A full fix needs reference-counting in-flight calls, real
    /// added scope this round doesn't take on. The bounded failure mode if this is hit: the
    /// in-flight call's underlying Playwright objects get closed out from under it, surfacing as
    /// a clean, caught exception -> a normal <c>[ERROR]</c> tool result via
    /// <see cref="ToolRegistry.ExecuteAsync"/>'s own top-level catch, not a crash or silent
    /// corruption -- narrow and ungraceful, not unsafe.
    /// </summary>
    private sealed class SessionDisposable(
        Func<BrowserSession?> getSession, Action<BrowserSession?> setSession, SemaphoreSlim gate)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync();
            try
            {
                // grok-review MINOR: nulling the closure's session reference (not just disposing
                // it) means a subsequent GetOrLaunchSessionAsync call correctly launches a FRESH
                // session via its own `session ??= ...` rather than returning a now-disposed,
                // permanently unusable stale reference.
                if (getSession() is { } session)
                {
                    await session.DisposeAsync();
                    setSession(null);
                }
            }
            finally
            {
                gate.Release();
            }
            // Deliberately NOT gate.Dispose()-ing here (grok-review follow-up: an earlier version
            // did, and got caught in review for a worse race than the one it fixed): a
            // GetOrLaunchSessionAsync call that acquires the gate in the window between this
            // Release() and a Dispose() would launch a brand-new real Chromium process, then throw
            // ObjectDisposedException on its own gate.Release(), orphaning that session with
            // nothing left to ever dispose it. A never-disposed SemaphoreSlim is a small, bounded,
            // GC-reclaimable managed object (this type holds no unmanaged handle unless
            // .AvailableWaitHandle is ever touched, which nothing here does) -- a strictly better
            // tradeoff than a race that can leak a whole browser process.
        }
    }

    private static string ResolvePath(string workspaceRoot, string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute)
            ? Path.GetFullPath(relativeOrAbsolute)
            : Path.GetFullPath(Path.Combine(workspaceRoot, relativeOrAbsolute));

    private static string StringArg(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static double NumberArg(Dictionary<string, object?> args, string key, double defaultValue)
    {
        if (!args.TryGetValue(key, out var v) || v is null) return defaultValue;
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            System.Text.Json.JsonElement je when je.TryGetDouble(out var jd) => jd,
            string s when double.TryParse(s, out var sd) => sd,
            _ => defaultValue,
        };
    }
}
