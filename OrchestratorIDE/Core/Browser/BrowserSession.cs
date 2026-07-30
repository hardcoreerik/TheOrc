// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Playwright;

namespace OrchestratorIDE.Core.Browser;

/// <summary>Options for <see cref="BrowserSession.LaunchAsync"/>. Headless is the only supported
/// mode for Phase 1 (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §4 open question 4) -- required for
/// Warband/daemon boxes with no display, and deliberately not exposed as a toggle yet on the
/// interactive desktop either (a visible, model-controlled window is a real UX/security surface
/// of its own, not a Phase 1 decision).</summary>
public sealed record BrowserSessionOptions(TimeSpan? DefaultTimeout = null)
{
    public TimeSpan EffectiveDefaultTimeout => DefaultTimeout ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Owns one Playwright <see cref="IBrowser"/> + <see cref="IBrowserContext"/> + current
/// <see cref="IPage"/> for the lifetime of one task/turn (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md
/// §2.2 -- no session pooling in v1). Headless-only, cross-platform (Playwright resolves its own
/// per-OS Chromium binary from the local cache <c>playwright install</c> populates).
///
/// Lifecycle discipline mirrors <see cref="LlamaServerManager"/>'s established pattern for
/// external-process ownership in this codebase: bounded operations (every call accepts a
/// <see cref="CancellationToken"/>), guaranteed cleanup via <see cref="DisposeAsync"/> even when a
/// step above it already faulted, and a clear failure message when the runtime dependency (here,
/// downloaded browser binaries) isn't present instead of a bare unhandled exception.
///
/// <b>Cancellation caveat</b> (found while writing this class's own tests): Playwright's
/// <c>IPage</c> methods don't accept a <see cref="CancellationToken"/> natively -- every method
/// here wraps its call in <c>Task.WaitAsync(ct)</c>, which stops AWAITING once <paramref
/// name="ct"/> fires but does not itself abort the real in-flight browser operation underneath.
/// A cancelled call returns control to the caller promptly (verified by
/// <c>BrowserSessionTests.NavigateAsync_WithAlreadyCancelledToken_ThrowsPromptly_NotHangs</c>),
/// but the page can still be mid-navigation/mid-action afterward. Calling another method on the
/// SAME session immediately after a cancellation is not guaranteed to observe a clean page state
/// -- if a hard abort is genuinely required, dispose the whole session rather than continue using
/// it.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly BrowserSessionOptions _options;
    private IPage _page;
    private bool _disposed;
    private bool _faulted;

    private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context,
        IPage page, BrowserSessionOptions options)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        _page = page;
        _options = options;
    }

    /// <summary>
    /// Launches a new headless Chromium session. Throws <see cref="PlaywrightBrowsersNotInstalledException"/>
    /// (translated from Playwright's own "Executable doesn't exist" error) when browser binaries
    /// haven't been downloaded yet -- callers should catch this specifically to surface the
    /// §4 open-question-2 manual "run playwright install" guidance rather than a raw stack trace.
    /// </summary>
    public static async Task<BrowserSession> LaunchAsync(BrowserSessionOptions? options = null, CancellationToken ct = default)
    {
        options ??= new BrowserSessionOptions();
        ct.ThrowIfCancellationRequested();

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        try
        {
            playwright = await Playwright.CreateAsync().WaitAsync(ct);
            try
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                }).WaitAsync(ct);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            {
                throw new PlaywrightBrowsersNotInstalledException(
                    "Playwright browser binaries are not installed. Run `playwright install chromium` " +
                    "(the driver script is generated at build output, e.g. bin/Debug/net10.0/playwright.ps1 " +
                    "install chromium) and try again.", ex);
            }

            context = await browser.NewContextAsync().WaitAsync(ct);
            context.SetDefaultTimeout((float)options.EffectiveDefaultTimeout.TotalMilliseconds);
            var page = await context.NewPageAsync().WaitAsync(ct);

            return new BrowserSession(playwright, browser, context, page, options);
        }
        catch
        {
            // Guaranteed teardown of whatever was already constructed before the failure point --
            // same "don't orphan a real process on a partial-construction throw" discipline this
            // session's own HIVE worker lifecycle fixes established (grok-review round 5/9).
            if (context is not null) await context.CloseAsync();
            if (browser is not null) await browser.CloseAsync();
            playwright?.Dispose();
            throw;
        }
    }

    public Task<string> NavigateAsync(string url, CancellationToken ct) => GuardedAsync(async () =>
    {
        await _page.GotoAsync(url).WaitAsync(ct);
        return await _page.TitleAsync().WaitAsync(ct);
    });

    public Task ClickAsync(string selector, CancellationToken ct) => GuardedAsync(async () =>
        await _page.ClickAsync(selector).WaitAsync(ct));

    public Task TypeAsync(string selector, string text, CancellationToken ct) => GuardedAsync(async () =>
        await _page.FillAsync(selector, text).WaitAsync(ct));

    /// <summary>Waits for a selector to appear, bounded by <paramref name="timeout"/>. Returns
    /// false rather than throwing on timeout -- a "did not appear in time" outcome is a normal,
    /// expected result for this call, not an exceptional one (docs §3 Phase 1a verify: "Timeout
    /// test ... does not hang the whole tool call indefinitely"). Not routed through
    /// <see cref="GuardedAsync{T}"/>: a genuine element-not-found timeout is expected, recoverable
    /// usage, not the kind of abandoned-mid-operation state that method exists to flag.</summary>
    public async Task<bool> WaitForAsync(string selector, TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfUnusable();
        try
        {
            await _page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                Timeout = (float)timeout.TotalMilliseconds,
            }).WaitAsync(ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // Unlike the two catches above (a normal "didn't appear" outcome), an externally
            // cancelled wait genuinely can leave the underlying Playwright wait abandoned
            // mid-flight -- same risk GuardedAsync exists to flag, so still mark faulted here.
            _faulted = true;
            throw;
        }
    }

    public Task<string> ExtractTextAsync(string? selector, CancellationToken ct) => GuardedAsync(async () =>
        selector is null
            ? await _page.InnerTextAsync("body").WaitAsync(ct)
            : await _page.InnerTextAsync(selector).WaitAsync(ct));

    /// <summary>Captures a full-page screenshot to <paramref name="outputPath"/>. The caller is
    /// responsible for sandbox confinement of that path -- this method does not itself validate
    /// it (matches <see cref="ArtifactToolResult"/>'s own documented expectation).</summary>
    public Task<string> ScreenshotAsync(string outputPath, CancellationToken ct) => GuardedAsync(async () =>
    {
        // grok-review BLOCKER: Path.GetDirectoryName returns "" (not null) for a bare filename
        // with no directory component (e.g. "screenshot.png") -- Directory.CreateDirectory("")
        // throws ArgumentException. Only create a directory when there's genuinely one to create.
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            FullPage = true,
        }).WaitAsync(ct);
        return outputPath;
    });

    /// <summary>Clicks <paramref name="triggerSelector"/> and saves the resulting download into
    /// <paramref name="outputDirectory"/> under its suggested filename. Caller is responsible for
    /// sandbox confinement of <paramref name="outputDirectory"/> itself; this method independently
    /// confines the SAVED FILE to stay inside it regardless of what the page's own suggested
    /// filename claims (grok-review MINOR: a page's Content-Disposition filename is untrusted
    /// input -- <c>Path.GetFileName</c> strips any directory/traversal component from it before
    /// combining, and the final resolved path is re-checked against the directory afterward, same
    /// "don't trust, verify the result" posture <c>Trust/PathSandbox</c> already uses elsewhere).
    /// </summary>
    public Task<string> DownloadAsync(string triggerSelector, string outputDirectory, CancellationToken ct) => GuardedAsync(async () =>
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var download = await _page.RunAndWaitForDownloadAsync(
            async () => await _page.ClickAsync(triggerSelector).WaitAsync(ct)).WaitAsync(ct);
        var safeFilename = Path.GetFileName(download.SuggestedFilename);
        if (string.IsNullOrWhiteSpace(safeFilename)) safeFilename = "download";
        var destPath = Path.GetFullPath(Path.Combine(root, safeFilename));
        if (!destPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Resolved download path '{destPath}' escapes the confined directory '{root}'.");
        await download.SaveAsAsync(destPath).WaitAsync(ct);
        return destPath;
    });

    /// <summary>Runs <paramref name="operation"/>, marking this session <see cref="_faulted"/> if
    /// it's cancelled mid-flight (grok-review BLOCKER, Phase 1a: <c>Task.WaitAsync(ct)</c> stops
    /// AWAITING but never aborts the real in-flight Playwright call underneath -- a session that
    /// experienced this is not safe to keep using, per this class's own cancellation-caveat doc
    /// comment). Every subsequent call throws via <see cref="ThrowIfUnusable"/> instead of risking
    /// the "Navigation ... is interrupted by another navigation"-style corruption a poisoned
    /// session can produce.</summary>
    private async Task<T> GuardedAsync<T>(Func<Task<T>> operation)
    {
        ThrowIfUnusable();
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            _faulted = true;
            throw;
        }
    }

    private async Task GuardedAsync(Func<Task> operation)
    {
        ThrowIfUnusable();
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            _faulted = true;
            throw;
        }
    }

    private void ThrowIfUnusable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserSession));
        if (_faulted) throw new InvalidOperationException(
            "This BrowserSession had an operation cancelled mid-flight and may be in an " +
            "inconsistent state (Playwright doesn't support truly aborting an in-flight call -- " +
            "see this class's own cancellation-caveat doc comment). Dispose it and launch a new " +
            "session instead of continuing to use this one.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Best-effort, ordered teardown -- a failure closing one layer must not skip the others,
        // same discipline as LlamaServerManager.Stop()'s try/finally.
        try { await _context.CloseAsync(); } catch { /* best-effort */ }
        try { await _browser.CloseAsync(); } catch { /* best-effort */ }
        try { _playwright.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>Thrown by <see cref="BrowserSession.LaunchAsync"/> when Playwright's own browser
/// binaries haven't been downloaded via <c>playwright install</c> yet -- a distinct, catchable
/// type so callers (BrowserTools' capability detection, Settings-panel wiring) can show the
/// specific "run this command" guidance instead of a generic launch-failure message.</summary>
public sealed class PlaywrightBrowsersNotInstalledException(string message, Exception inner)
    : Exception(message, inner);
