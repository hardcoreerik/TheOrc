// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Real, non-mocked end-to-end coverage of the FULL browser_* tool path through
/// <see cref="ToolRegistry"/> (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §3 Phase 1a cross-cutting
/// exit criterion: "an end-to-end browse/extract/screenshot loop working through OrcChat") -- not
/// just BrowserSession in isolation (that's BrowserSessionTests.cs's job). Same Playwright-
/// installed prerequisite and Assert.Ignore-on-missing convention as that fixture.
/// </summary>
[TestFixture]
public sealed class BrowserToolsTests
{
    private HttpListener? _listener;
    private Task? _serverLoop;
    private string _baseUrl = "";
    private string _workspaceRoot = "";
    // Every BrowserTools.Register call in this fixture's registered handle, disposed in
    // TearDown (grok-review MINOR: an undisposed lazily-launched BrowserSession leaks a real
    // Chromium process per test and can hold file handles open under this fixture's own
    // Directory.Delete(_workspaceRoot) below).
    private readonly List<IAsyncDisposable> _handles = [];

    private const string FixtureHtml = """
        <!doctype html>
        <html><head><title>BrowserTools Fixture</title></head>
        <body><h1 id="heading">Tools Fixture Page</h1></body></html>
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

        // Real probe (not a Playwright-installed assumption) -- Assert.Ignore the whole fixture
        // with a clear reason if Chromium isn't available, same convention as BrowserSessionTests.
        try
        {
            await using var probe = await OrchestratorIDE.Core.Browser.BrowserSession.LaunchAsync();
        }
        catch (OrchestratorIDE.Core.Browser.PlaywrightBrowsersNotInstalledException ex)
        {
            Assert.Ignore($"Playwright Chromium is not installed on this machine: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _listener?.Stop();
        _listener?.Close();
        if (_serverLoop is not null) { try { await _serverLoop; } catch { } }
    }

    [SetUp]
    public void SetUp()
    {
        NativeToolCapabilities.ResetForTest();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "browser-tools-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var handle in _handles) await handle.DisposeAsync();
        _handles.Clear();
        NativeToolCapabilities.ResetForTest();
        if (Directory.Exists(_workspaceRoot)) Directory.Delete(_workspaceRoot, recursive: true);
    }

    private static ModelProfile FullToolSetProfile() => new(
        ModelId: "test-model", Name: "Test Model", ContextTokens: 4096, NativeToolUse: true,
        Strengths: [], ToolSet: ToolSet.Full, PromptStyle: PromptStyle.Agent);

    /// <summary>Register() fires capability detection in the background (non-blocking, see
    /// BrowserTools.cs's own doc comment) -- poll rather than assume it's already resolved by the
    /// time this method returns.</summary>
    private static async Task WaitForBrowserCapabilityAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation)) return;
            await Task.Delay(100);
        }
        Assert.Fail("BrowserAutomation capability never became available within 15s");
    }

    [Test]
    public async Task Register_MakesBrowserToolsAdvertised_OnceCapabilityDetectionCompletes()
    {
        var registry = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
        _handles.Add(BrowserTools.Register(registry, _workspaceRoot));

        await WaitForBrowserCapabilityAsync();

        var names = registry.GetForProfile(FullToolSetProfile()).Select(t => t.Name).ToList();
        Assert.That(names, Does.Contain("browser_navigate"));
        Assert.That(names, Does.Contain("browser_click"));
        Assert.That(names, Does.Contain("browser_type"));
        Assert.That(names, Does.Contain("browser_wait"));
        Assert.That(names, Does.Contain("browser_extract"));
        Assert.That(names, Does.Contain("browser_screenshot"));
        Assert.That(names, Does.Contain("browser_download"));
    }

    [Test]
    public async Task FullBrowseExtractScreenshotLoop_RunsEndToEnd_ThroughToolRegistry()
    {
        // The actual exit criterion: navigate -> extract -> screenshot, each invoked the way a
        // model actually calls tools (ToolRegistry.ExecuteAsync with a ToolCall), not by calling
        // BrowserSession methods directly.
        var registry = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
        _handles.Add(BrowserTools.Register(registry, _workspaceRoot));
        await WaitForBrowserCapabilityAsync();

        var navResult = await registry.ExecuteAsync(
            new ToolCall { Name = "browser_navigate", Arguments = new() { ["url"] = _baseUrl } },
            CancellationToken.None);
        Assert.That(navResult, Does.StartWith("[OK]"));
        Assert.That(navResult, Does.Contain("BrowserTools Fixture"));

        var extractResult = await registry.ExecuteAsync(
            new ToolCall { Name = "browser_extract", Arguments = new() { ["selector"] = "#heading" } },
            CancellationToken.None);
        Assert.That(extractResult, Is.EqualTo("Tools Fixture Page"));

        var screenshotResult = await registry.ExecuteAsync(
            new ToolCall { Name = "browser_screenshot", Arguments = new() { ["path"] = "shots/loop.png" } },
            CancellationToken.None);
        Assert.That(screenshotResult, Does.StartWith("[OK]"));
        var expectedPath = Path.Combine(_workspaceRoot, "shots", "loop.png");
        Assert.That(File.Exists(expectedPath), Is.True);
        Assert.That(new FileInfo(expectedPath).Length, Is.GreaterThan(1024));
    }

    [Test]
    public async Task BrowserScreenshot_OutsideWorkspace_IsSandboxBlocked_WhenNoBypassWired()
    {
        var registry = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
        _handles.Add(BrowserTools.Register(registry, _workspaceRoot)); // no onSandboxBypass
        await WaitForBrowserCapabilityAsync();

        await registry.ExecuteAsync(
            new ToolCall { Name = "browser_navigate", Arguments = new() { ["url"] = _baseUrl } },
            CancellationToken.None);

        var outsidePath = Path.Combine(Path.GetTempPath(), "outside-workspace-" + Path.GetRandomFileName() + ".png");
        var result = await registry.ExecuteAsync(
            new ToolCall { Name = "browser_screenshot", Arguments = new() { ["path"] = outsidePath } },
            CancellationToken.None);

        Assert.That(result, Does.StartWith("[SANDBOX BLOCKED]"));
        Assert.That(File.Exists(outsidePath), Is.False);
    }

    [Test]
    public async Task BrowserNavigate_WithRequireApprovalTrue_WaitsForApproval_NotAutoApproved()
    {
        // Guarded (the ApprovalQueue default) + a real ApprovalRequested subscriber, proving
        // RequiresApproval genuinely gates this tool rather than always sailing through --
        // distinct from every other test here, which uses AutoApprove=true for simplicity.
        var approvals = new ApprovalQueue(); // Level defaults to Guarded, AutoApprove defaults to false
        PendingApproval? seen = null;
        approvals.ApprovalRequested += p => seen = p;
        var registry = new ToolRegistry(approvals);
        _handles.Add(BrowserTools.Register(registry, _workspaceRoot)); // default requireApprovalForNavigateAndDownload: true
        await WaitForBrowserCapabilityAsync();

        var call = new ToolCall { Name = "browser_navigate", Arguments = new() { ["url"] = _baseUrl } };
        var executeTask = registry.ExecuteAsync(call, CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (seen is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.That(seen, Is.Not.Null, "ApprovalRequested must fire for browser_navigate under Guarded trust");
        Assert.That(call.Status, Is.EqualTo(ToolCallStatus.AwaitingApproval));

        approvals.Approve(seen!);
        var result = await executeTask;
        Assert.That(result, Does.StartWith("[OK]"));
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
