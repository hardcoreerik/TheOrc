// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Verifies that generalizing ChatEngine (constructor params for systemPrompt/tools/
/// temperature/topP, defaulting to the original hardcoded research-chat values) didn't
/// silently change the existing Research Chat tab's behavior -- the only call site that
/// matters for backward compatibility is the unchanged 2-arg `new ChatEngine(runtime, model)`.
/// </summary>
[TestFixture]
public class ChatEngineTests
{
    private sealed class CapturingRuntime : IModelRuntime
    {
        public IEnumerable<AgentMessage>? LastHistory { get; private set; }
        public IReadOnlyList<object>?      LastTools   { get; private set; }
        public double                      LastTemperature { get; private set; }
        public double?                     LastTopP    { get; private set; }

        public string RuntimeName => "Capturing";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            double? topP = null,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastHistory     = history.ToList();
            LastTools       = tools;
            LastTemperature = temperature;
            LastTopP        = topP;
            await Task.Yield();
            yield return "ok";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);
        public RuntimeStats GetStats() => new(RuntimeName);
    }

    [Test]
    public async Task DefaultConstruction_MatchesOriginalResearchChatBehavior()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "qwen2.5-coder:7b");

        await engine.SendAsync("hello");

        var first = runtime.LastHistory!.First();
        Assert.Multiple(() =>
        {
            // Temperature: the original hardcoded value, unchanged.
            Assert.That(runtime.LastTemperature, Is.EqualTo(0.2));

            // topP: never set by the original code, must still be null by default.
            Assert.That(runtime.LastTopP, Is.Null);

            // Tools: the research toolset (WebSearchTool/FetchPageTool), not empty.
            Assert.That(runtime.LastTools, Is.Not.Null);
            Assert.That(runtime.LastTools!.Count, Is.GreaterThan(0));

            // System prompt: the research base prompt must still be present as the first message.
            Assert.That(first.Role, Is.EqualTo(MessageRole.System));
            Assert.That(first.Content, Does.Contain(ResearchToolset.BaseSystemPrompt));
        });
    }

    [Test]
    public async Task ExplicitEmptyPromptAndTools_InjectsNothing()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: []);

        await engine.SendAsync("hello");

        // No system message at all -- the first (and only, before the user turn) message
        // must be the user's own message, not an empty-content system message.
        var first = runtime.LastHistory!.First();
        Assert.Multiple(() =>
        {
            Assert.That(first.Role, Is.EqualTo(MessageRole.User));

            // Tools: explicitly empty, not the research default.
            Assert.That(runtime.LastTools, Is.Not.Null);
            Assert.That(runtime.LastTools!, Is.Empty);
        });
    }

    [Test]
    public async Task CustomTemperatureAndTopP_AreThreadedThrough()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: [], temperature: 0.8, topP: 0.95);

        await engine.SendAsync("hello");

        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastTemperature, Is.EqualTo(0.8));
            Assert.That(runtime.LastTopP, Is.EqualTo(0.95));
        });
    }
}
