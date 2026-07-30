// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Covers the "model has no native tool-calling template" retry path added to
/// OllamaClient.StreamCompletionAsync: live-tested 2026-07-30 against
/// hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M, which 400s the whole
/// request when a `tools` array is attached, instead of just ignoring it.
/// </summary>
[TestFixture]
public class OllamaClientToolFallbackTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public FakeHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return _responses.Count > 0 ? _responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private static HttpResponseMessage ToolsUnsupported400() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            "{\"error\":{\"message\":\"model \\\"m\\\" does not support tools\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":null}}",
            Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage SseStream(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static readonly List<AgentMessage> History =
        [new AgentMessage { Role = MessageRole.User, Content = "hi" }];

    private static readonly List<object> Tools = [new Dictionary<string, object?> { ["type"] = "function" }];

    [Test]
    public async Task StreamCompletionAsync_RetriesWithoutTools_WhenModelRejects400ToolsUnsupported()
    {
        var handler = new FakeHandler(ToolsUnsupported400, () => SseStream("Hello"));
        var client = new OllamaClient(handler);

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamCompletionAsync("m", History, Tools, ct: CancellationToken.None))
            chunks.Add(chunk);

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2), "should retry exactly once");
        Assert.That(handler.RequestBodies[0], Does.Contain("\"tools\""), "first attempt includes tools");
        Assert.That(handler.RequestBodies[1], Does.Not.Contain("\"tools\""), "retry omits tools");
        Assert.That(string.Concat(chunks), Does.Contain("Hello"), "the retried call's real content still streams through");
        Assert.That(string.Concat(chunks), Does.Contain("no native tool-calling support"), "the user is told why tools were dropped");
    }

    [Test]
    public async Task StreamCompletionAsync_DoesNotRetry_WhenNoToolsWereRequested()
    {
        var handler = new FakeHandler(ToolsUnsupported400);
        var client = new OllamaClient(handler);

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamCompletionAsync("m", History, tools: null, ct: CancellationToken.None))
            chunks.Add(chunk);

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(1), "nothing to retry without -- the model failure must be surfaced as-is");
        Assert.That(string.Concat(chunks), Does.Contain("[ERROR 400]"));
    }

    [Test]
    public async Task StreamCompletionAsync_DoesNotRetry_OnUnrelated400()
    {
        var unrelated400 = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"context length exceeded\"}}", Encoding.UTF8, "application/json"),
        };
        var handler = new FakeHandler(unrelated400);
        var client = new OllamaClient(handler);

        var chunks = new List<string>();
        await foreach (var chunk in client.StreamCompletionAsync("m", History, Tools, ct: CancellationToken.None))
            chunks.Add(chunk);

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(1), "only retries the specific tools-unsupported error, not every 400");
        Assert.That(string.Concat(chunks), Does.Contain("context length exceeded"));
    }
}
