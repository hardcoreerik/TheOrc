// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core.Browser;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Real, non-mocked end-to-end coverage of BrowserSession (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md
/// §3 Phase 1a verify) -- a real headless Chromium instance driven against a real fixture page
/// served by a local HttpListener (same "no external network dependency, bare HttpListener not a
/// full server" pattern HiveWorkerAgentTests already established for its own fake-Warchief tests).
///
/// Requires Playwright's Chromium binaries to already be downloaded (`playwright install
/// chromium`). This is an environment prerequisite, not something CI/every dev machine has by
/// default (docs §4 open question 2 -- manual install, not yet automated) -- OneTimeSetUp probes
/// for it and Assert.Ignore()s the whole fixture with a clear reason if missing, matching this
/// repo's established THEORC_TEST_GGUF-gated-lane convention rather than hard-failing every
/// machine that hasn't run the install step.
/// </summary>
[TestFixture]
public sealed class BrowserSessionTests
{
    private HttpListener? _listener;
    private Task? _serverLoop;
    private BrowserSession? _session;
    private string _baseUrl = "";

    private const string FixtureHtml = """
        <!doctype html>
        <html>
        <head><title>BrowserSession Fixture</title></head>
        <body>
          <h1 id="heading">Fixture Page</h1>
          <button id="reveal-btn" onclick="document.getElementById('hidden-div').style.display='block'">Reveal</button>
          <div id="hidden-div" style="display:none">Revealed content</div>
          <input id="name-input" type="text" />
          <button id="greet-btn" onclick="document.getElementById('greet-result').innerText = 'Hello, ' + document.getElementById('name-input').value">Greet</button>
          <div id="greet-result"></div>
          <a id="malicious-download-link" href="/malicious-download">Download</a>
        </body>
        </html>
        """;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"{_baseUrl}/");
        _listener.Start();
        _serverLoop = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (ctx.Request.Url?.AbsolutePath == "/malicious-download")
                    {
                        // A server response is untrusted input from BrowserSession's own
                        // perspective -- this fixture simulates a page whose Content-Disposition
                        // filename tries to escape the confined output directory, exercising the
                        // path-traversal fix DownloadAsync's own doc comment describes.
                        var payload = Encoding.UTF8.GetBytes("malicious payload");
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.AddHeader("Content-Disposition",
                            "attachment; filename=\"../../../evil.txt\"");
                        ctx.Response.ContentLength64 = payload.Length;
                        await ctx.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                        ctx.Response.Close();
                        continue;
                    }

                    var bytes = Encoding.UTF8.GetBytes(FixtureHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
            }
            catch (HttpListenerException) { /* Stop() during shutdown */ }
            catch (ObjectDisposedException) { /* Stop() during shutdown */ }
        });

        try
        {
            _session = await BrowserSession.LaunchAsync();
        }
        catch (PlaywrightBrowsersNotInstalledException ex)
        {
            Assert.Ignore($"Playwright Chromium is not installed on this machine: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_session is not null) await _session.DisposeAsync();
        _listener?.Stop();
        _listener?.Close();
        if (_serverLoop is not null) { try { await _serverLoop; } catch { } }
    }

    [SetUp]
    public async Task SetUp()
    {
        // Reset page state before every test rather than relaunching the whole browser per test
        // (a fresh Chromium launch is ~1-2s; a navigate is milliseconds) -- each test still starts
        // from a known-clean DOM.
        await _session!.NavigateAsync(_baseUrl, CancellationToken.None);
    }

    [Test]
    public async Task NavigateAsync_ReturnsRealPageTitle()
    {
        var title = await _session!.NavigateAsync(_baseUrl, CancellationToken.None);
        Assert.That(title, Is.EqualTo("BrowserSession Fixture"));
    }

    [Test]
    public async Task ExtractTextAsync_WithSelector_ReturnsRealDomText()
    {
        var text = await _session!.ExtractTextAsync("#heading", CancellationToken.None);
        Assert.That(text, Is.EqualTo("Fixture Page"));
    }

    [Test]
    public async Task ExtractTextAsync_NoSelector_ReturnsFullBodyText()
    {
        var text = await _session!.ExtractTextAsync(null, CancellationToken.None);
        Assert.That(text, Does.Contain("Fixture Page"));
    }

    [Test]
    public async Task ClickAsync_ThenWaitForAsync_ObservesRealDomMutation()
    {
        // #hidden-div starts display:none -- WaitForSelectorAsync's default Visible state means
        // this genuinely proves the click ran and the browser's own JS executed, not just that
        // the element exists in markup.
        await _session!.ClickAsync("#reveal-btn", CancellationToken.None);
        var appeared = await _session.WaitForAsync("#hidden-div", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.That(appeared, Is.True);

        var revealedText = await _session.ExtractTextAsync("#hidden-div", CancellationToken.None);
        Assert.That(revealedText, Is.EqualTo("Revealed content"));
    }

    [Test]
    public async Task WaitForAsync_ReturnsFalse_ForSelectorThatNeverAppears_WithinBoundedTime()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = await _session!.WaitForAsync("#never-appears", TimeSpan.FromSeconds(1), CancellationToken.None);
        sw.Stop();

        Assert.That(found, Is.False);
        // Bounded, not hung indefinitely -- allow generous slack for CI/slow-machine scheduling
        // jitter around the 1s timeout itself.
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
            "WaitForAsync must not block far longer than its own timeout");
    }

    [Test]
    public async Task TypeAsync_ThenClickAsync_DrivesARealFormInteraction()
    {
        await _session!.TypeAsync("#name-input", "TheOrc", CancellationToken.None);
        await _session.ClickAsync("#greet-btn", CancellationToken.None);

        var result = await _session.ExtractTextAsync("#greet-result", CancellationToken.None);
        Assert.That(result, Is.EqualTo("Hello, TheOrc"));
    }

    [Test]
    public async Task ScreenshotAsync_WritesARealNonTrivialImageFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "browser-session-tests-" + Path.GetRandomFileName());
        var outputPath = Path.Combine(tempDir, "screenshot.png");
        try
        {
            var written = await _session!.ScreenshotAsync(outputPath, CancellationToken.None);

            Assert.That(written, Is.EqualTo(outputPath));
            Assert.That(File.Exists(outputPath), Is.True);
            // A trivial/empty file would indicate a fake or failed capture, not a real screenshot.
            Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(1024));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    [NonParallelizable] // mutates the process-global Environment.CurrentDirectory below
                        // (grok-review follow-up) -- this suite runs sequentially today (no
                        // [Parallelizable] anywhere in this project), but this attribute makes
                        // that a guaranteed contract for THIS test specifically, not an incidental
                        // fact a future parallelism change elsewhere could silently invalidate.
    public async Task ScreenshotAsync_WithBareFilename_NoDirectoryComponent_DoesNotThrow()
    {
        // grok-review BLOCKER regression: Path.GetDirectoryName("screenshot.png") returns "" (not
        // null), and Directory.CreateDirectory("") used to throw ArgumentException before this
        // was fixed to only create a directory when there's genuinely one to create.
        var originalCwd = Environment.CurrentDirectory;
        var tempDir = Path.Combine(Path.GetTempPath(), "browser-session-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        Environment.CurrentDirectory = tempDir;
        try
        {
            var bareFilename = "bare-screenshot.png";
            Assert.That(async () => await _session!.ScreenshotAsync(bareFilename, CancellationToken.None),
                Throws.Nothing);
            Assert.That(File.Exists(Path.Combine(tempDir, bareFilename)), Is.True);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task DownloadAsync_ConfinesSavedFile_EvenWhenServerSuggestsATraversalFilename()
    {
        // grok-review MINOR regression: a page's Content-Disposition filename is untrusted input
        // (the fixture server above deliberately suggests "../../../evil.txt"). The saved file
        // must land inside outputDirectory regardless -- never escape via the suggested name.
        var tempDir = Path.Combine(Path.GetTempPath(), "browser-session-tests-" + Path.GetRandomFileName());
        try
        {
            var savedPath = await _session!.DownloadAsync("#malicious-download-link", tempDir, CancellationToken.None);

            var resolvedRoot = Path.GetFullPath(tempDir);
            var resolvedSaved = Path.GetFullPath(savedPath);
            Assert.That(resolvedSaved, Does.StartWith(resolvedRoot + Path.DirectorySeparatorChar),
                "the saved file must stay confined inside the requested output directory -- the " +
                "real safety property, regardless of what the (possibly browser-sanitized) " +
                "suggested filename happens to literally contain");
            Assert.That(File.Exists(savedPath), Is.True);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task ASessionThatHadAnOperationCancelled_RefusesFurtherUse_UntilRelaunched()
    {
        // grok-review BLOCKER: a cancelled call can leave Playwright mid-operation underneath
        // (Task.WaitAsync(ct) only stops awaiting, it doesn't abort the real browser call) --
        // GuardedAsync marks the session faulted so a caller can't accidentally keep using a
        // possibly-corrupted session and hit confusing downstream errors instead.
        await using var session = await BrowserSession.LaunchAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(async () => await session.NavigateAsync(_baseUrl, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.NavigateAsync(_baseUrl, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("Dispose it and launch a new session"));
    }

    [Test]
    public async Task NavigateAsync_WithAlreadyCancelledToken_ThrowsPromptly_NotHangs()
    {
        // Dedicated session, NOT the shared fixture one (grok-review-caught issue while writing
        // this test): Playwright's IPage methods don't accept a CancellationToken natively --
        // BrowserSession wraps each call in Task.WaitAsync(ct), which stops AWAITING once
        // cancelled but does not itself abort the real in-flight browser navigation underneath.
        // Using the shared _session here left a genuinely still-running navigation that then
        // collided with the NEXT test's SetUp() call ("Navigation ... is interrupted by another
        // navigation"), an order-dependent failure in a completely unrelated test. A dedicated,
        // disposed-at-the-end session contains that abandoned navigation to this test alone.
        await using var session = await BrowserSession.LaunchAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.That(
            async () => await session.NavigateAsync(_baseUrl, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
        sw.Stop();

        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            "an already-cancelled token must fail fast, not wait for the navigation to complete first");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
