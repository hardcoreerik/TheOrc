// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Real, non-mocked coverage of the headless-surface browser tool profile
/// (docs/NATIVE_BROWSER_AUTOMATION_SPEC.md §3 Phase 1b), including "at least one deterministic
/// headless test" run through the actual HeadlessAgentLoop (not just the tool functions in
/// isolation), matching CampaignEngineTests' own established
/// HeadlessLoop_ExecutesAllowedTool_ThenReturnsFinalAnswer pattern. Same Playwright-installed
/// prerequisite and Assert.Ignore convention as the other browser test fixtures.
/// </summary>
[TestFixture]
public sealed class NativeWorkerBrowserToolProfileTests
{
    private HttpListener? _listener;
    private Task? _serverLoop;
    private string _baseUrl = "";
    private string _outputDirectory = "";

    private const string FixtureHtml = """
        <!doctype html>
        <html><head><title>Headless Fixture</title></head>
        <body><h1 id="heading">Headless Tools Fixture</h1></body></html>
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

    // Create() returns (Tools, Cleanup); every call site's Cleanup handle goes here and is
    // disposed in TearDown -- same leak-prevention convention as BrowserToolsTests.cs's own
    // _handles list (a real Chromium process per un-disposed lazily-launched session otherwise).
    private readonly List<IAsyncDisposable> _cleanupHandles = [];

    [SetUp]
    public void SetUp()
    {
        NativeToolCapabilities.ResetForTest();
        NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
        _outputDirectory = Path.Combine(Path.GetTempPath(), "hive-browser-tools-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_outputDirectory);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var handle in _cleanupHandles) await handle.DisposeAsync();
        _cleanupHandles.Clear();
        NativeToolCapabilities.ResetForTest();
        if (Directory.Exists(_outputDirectory)) Directory.Delete(_outputDirectory, recursive: true);
    }

    private IReadOnlyList<HeadlessTool> CreateTools(HeadlessBrowserPolicy? policy = null)
    {
        var (tools, cleanup) = NativeWorkerBrowserToolProfile.Create(_outputDirectory, policy);
        _cleanupHandles.Add(cleanup);
        return tools;
    }

    [Test]
    public void Create_ReturnsEmptyList_WhenCapabilityUnavailable()
    {
        NativeToolCapabilities.ResetForTest(); // not marked available for this test
        var tools = CreateTools();
        Assert.That(tools, Is.Empty);
    }

    [Test]
    public void Create_ReturnsAllSevenTools_WhenCapabilityAvailable()
    {
        var tools = CreateTools();
        var names = tools.Select(t => t.Name).ToList();
        Assert.That(names, Is.EquivalentTo(new[]
        {
            "browser_navigate", "browser_click", "browser_type", "browser_wait",
            "browser_extract", "browser_screenshot", "browser_download",
        }));
    }

    [Test]
    public async Task BrowserNavigate_DeniedByDefaultPolicy_ForAnyOrigin()
    {
        var tools = CreateTools(); // default: HeadlessBrowserPolicy.DenyAll
        var navigate = tools.Single(t => t.Name == "browser_navigate");

        var result = await navigate.ExecuteAsync(new Dictionary<string, object?> { ["url"] = _baseUrl }, CancellationToken.None);

        Assert.That(result, Does.StartWith("[POLICY BLOCKED]"));
        Assert.That(result, Does.Contain("not in this task's allowed navigation list"));
    }

    [Test]
    public async Task BrowserNavigate_Succeeds_WhenOriginExplicitlyAllowed()
    {
        var origin = new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);
        var policy = new HeadlessBrowserPolicy(AllowedOrigins: [origin]);
        var tools = CreateTools(policy);
        var navigate = tools.Single(t => t.Name == "browser_navigate");

        var result = await navigate.ExecuteAsync(new Dictionary<string, object?> { ["url"] = _baseUrl }, CancellationToken.None);

        Assert.That(result, Does.StartWith("[OK]"));
        Assert.That(result, Does.Contain("Headless Fixture"));
    }

    [Test]
    public async Task BrowserNavigate_PolicyBlocked_AfterExceedingMaxNavigations()
    {
        var origin = new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);
        var policy = new HeadlessBrowserPolicy(AllowedOrigins: [origin], MaxNavigations: 2);
        var tools = CreateTools(policy);
        var navigate = tools.Single(t => t.Name == "browser_navigate");
        var args = new Dictionary<string, object?> { ["url"] = _baseUrl };

        var first = await navigate.ExecuteAsync(args, CancellationToken.None);
        var second = await navigate.ExecuteAsync(args, CancellationToken.None);
        var third = await navigate.ExecuteAsync(args, CancellationToken.None);

        Assert.That(first, Does.StartWith("[OK]"));
        Assert.That(second, Does.StartWith("[OK]"));
        Assert.That(third, Does.StartWith("[POLICY BLOCKED]"));
        Assert.That(third, Does.Contain("exceeded its maximum of 2"));
    }

    [Test]
    public async Task BrowserDownload_PolicyBlocked_ByDefault()
    {
        var tools = CreateTools(); // DownloadsAllowed: false by default
        var download = tools.Single(t => t.Name == "browser_download");

        var result = await download.ExecuteAsync(
            new Dictionary<string, object?> { ["trigger_selector"] = "#anything" }, CancellationToken.None);

        Assert.That(result, Is.EqualTo("[POLICY BLOCKED] Downloads are not permitted for this task."));
    }

    [Test]
    public async Task BrowserScreenshot_PolicyBlocked_WhenPathEscapesIsolatedWorkArea()
    {
        var origin = new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);
        var policy = new HeadlessBrowserPolicy(AllowedOrigins: [origin]);
        var tools = CreateTools(policy);
        var navigate = tools.Single(t => t.Name == "browser_navigate");
        var screenshot = tools.Single(t => t.Name == "browser_screenshot");

        await navigate.ExecuteAsync(new Dictionary<string, object?> { ["url"] = _baseUrl }, CancellationToken.None);
        var result = await screenshot.ExecuteAsync(
            new Dictionary<string, object?> { ["path"] = "../../../escape.png" }, CancellationToken.None);

        Assert.That(result, Does.StartWith("[POLICY BLOCKED]"));
        Assert.That(result, Does.Contain("escapes the isolated work area"));
    }

    [Test]
    public async Task HeadlessLoop_NavigatesAndExtracts_ThroughTheRealAgentLoop()
    {
        // The actual Phase 1b exit criterion: run through HeadlessAgentLoop.ExecuteAsync itself
        // (matching CampaignEngineTests' own HeadlessLoop_ExecutesAllowedTool_ThenReturnsFinalAnswer
        // pattern), not just calling a HeadlessTool's ExecuteAsync directly in isolation.
        var origin = new Uri(_baseUrl).GetLeftPart(UriPartial.Authority);
        var policy = new HeadlessBrowserPolicy(AllowedOrigins: [origin]);
        var tools = CreateTools(policy);

        var runtime = new NavigateThenExtractThenAnswerRuntime(_baseUrl);
        var loop = new HeadlessAgentLoop(runtime);

        var result = await loop.ExecuteAsync(RuntimeRole.Worker,
            [new AgentMessage { Role = MessageRole.User, Content = "Read the fixture page heading." }],
            tools, new HeadlessAgentLimits(MaxSteps: 4));

        Assert.Multiple(() =>
        {
            Assert.That(runtime.SawNavigateResult, Is.True, "the loop must have fed the navigate tool's [OK] result back into history");
            Assert.That(runtime.SawExtractResult, Is.True, "the loop must have fed the extract tool's real page text back into history");
            Assert.That(result.Output, Is.EqualTo("Headless Tools Fixture"));
            Assert.That(result.Steps, Is.EqualTo(3));
        });
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Scripted fake IRoleRuntime: step 1 emits browser_navigate, step 2 emits
    /// browser_extract (only once it has observed the navigate's own [OK] result in history,
    /// proving the loop actually fed the tool result back), step 3 answers with whatever text
    /// browser_extract actually returned -- proving that came from a REAL page render, not a
    /// canned string.</summary>
    private sealed class NavigateThenExtractThenAnswerRuntime(string url) : IRoleRuntime
    {
        private int _calls;
        public bool SawNavigateResult { get; private set; }
        public bool SawExtractResult { get; private set; }
        public string RuntimeName => "test-native";

        public async IAsyncEnumerable<string> StreamRoleCompletionAsync(RuntimeRole role,
            IEnumerable<AgentMessage> history, IReadOnlyList<object>? tools = null,
            double temperature = 0.1, int maxTokens = 4096, Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _calls++;
            var historyList = history.ToList();

            if (_calls == 1)
            {
                onToolCall?.Invoke(new ToolCall { Name = "browser_navigate", Arguments = new() { ["url"] = url } });
                yield break;
            }

            if (_calls == 2)
            {
                SawNavigateResult = historyList.Any(m => m.Content.Contains("[OK] Navigated", StringComparison.Ordinal));
                onToolCall?.Invoke(new ToolCall
                {
                    Name = "browser_extract",
                    Arguments = new() { ["selector"] = "#heading" },
                });
                yield break;
            }

            var extractedText = historyList.LastOrDefault(m => m.Role == MessageRole.Tool)?.Content ?? "";
            SawExtractResult = extractedText == "Headless Tools Fixture";
            onUsage?.Invoke(4, 2);
            yield return extractedText;
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName, "fake.gguf");
        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName, "fake.gguf");
    }
}
