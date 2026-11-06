// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Two generations at once on one loaded model. Swarm starts every coder and planned tester
/// together; on the Native Runtime they share one LLamaSharp BatchedExecutor, which allows only
/// one Infer() at a time ("Cannot start inference while it is already running"), and the
/// interleaved prompt/decode steps broke the KV cache ("inconsistent sequence positions").
/// Seen on Apple Silicon with Gemma-4-26B; reproduced here with any small GGUF.
/// </summary>
[TestFixture]
public sealed class NativeRoleConcurrencyTests
{
    [Test, CancelAfter(300_000)]
    public async Task Two_concurrent_generations_on_one_model_both_complete()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this real-model lane.");

        await using var runtime = new NativeRoleRuntime(
            ModelDepot.Scan(Path.GetDirectoryName(Path.GetFullPath(ggufPath!))!),
            new RuntimeOptions(ContextLength: 2048, GpuLayers: -1),
            allowUnbudgetedExecution: true);

        static List<AgentMessage> Ask(string q) => [new() { Role = MessageRole.User, Content = q }];

        async Task<string> Generate(string prompt)
        {
            var text = new System.Text.StringBuilder();
            await foreach (var t in runtime.StreamRoleCompletionAsync(RuntimeRole.Worker, Ask(prompt), maxTokens: 48))
                text.Append(t);
            return text.ToString();
        }

        var results = await Task.WhenAll(
            Generate("List three colours, comma separated."),
            Generate("Name two planets, comma separated."));

        Assert.That(results, Has.All.Not.Empty);
    }
}
