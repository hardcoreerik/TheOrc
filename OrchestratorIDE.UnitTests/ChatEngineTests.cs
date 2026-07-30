// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Research;
using OrchestratorIDE.Services.Swarm;

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
        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

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
            onUsage?.Invoke(42, 7);
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

    [Test]
    public async Task IncludeDateTimeContext_defaultsFalse_doesNotChangeExistingBehavior()
    {
        // Must stay false by default -- DefaultConstruction_MatchesOriginalResearchChatBehavior
        // above already asserts the research system prompt is present with nothing extra
        // prepended; this test makes the "off by default" guarantee explicit on its own.
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: []);

        await engine.SendAsync("hello");

        var first = runtime.LastHistory!.First();
        Assert.That(first.Role, Is.EqualTo(MessageRole.User),
            "IncludeDateTimeContext defaults to false -- an empty system prompt must still inject nothing.");
    }

    [Test]
    public async Task IncludeDateTimeContext_true_withEmptySystemPrompt_injectsJustDateTime()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: [])
        {
            IncludeDateTimeContext = true,
        };

        await engine.SendAsync("hello");

        var first = runtime.LastHistory!.First();
        Assert.Multiple(() =>
        {
            Assert.That(first.Role, Is.EqualTo(MessageRole.System));
            Assert.That(first.Content, Does.Contain("Current date and time:"));
        });
    }

    [Test]
    public async Task IncludeDateTimeContext_true_withCustomSystemPrompt_prependsDateTime()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "You are a pirate.", tools: [])
        {
            IncludeDateTimeContext = true,
        };

        await engine.SendAsync("hello");

        var first = runtime.LastHistory!.First();
        Assert.Multiple(() =>
        {
            Assert.That(first.Content, Does.Contain("Current date and time:"));
            Assert.That(first.Content, Does.Contain("You are a pirate."));
        });
    }

    [Test]
    public async Task OnUsage_fires_withPromptAndCompletionTokenCounts()
    {
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: []);

        (int Prompt, int Completion)? captured = null;
        engine.OnUsage += (p, c) => captured = (p, c);

        await engine.SendAsync("hello");

        Assert.That(captured, Is.EqualTo(((int Prompt, int Completion)?)(42, 7)));
    }

    // ── OnToolcallerDecision (toolcaller-v1 capture hook) ───────────────────────

    [Test]
    public async Task OnToolcallerDecision_DoesNotFire_WhenNoToolsOffered()
    {
        // Zero tools available -> nothing to decide against, so no capture-worthy decision.
        var runtime = new CapturingRuntime();
        var engine  = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: []);

        ChatToolDecision? captured = null;
        engine.OnToolcallerDecision += d => captured = d;

        await engine.SendAsync("A perfectly ordinary long enough question.");

        Assert.That(captured, Is.Null);
    }

    [Test]
    public async Task OnToolcallerDecision_FiresNoTool_WhenToolsOfferedButNoneCalled()
    {
        var runtime = new CapturingRuntime();
        var tools = new List<ToolDefinition> { new() { Name = "read_file", Description = "Read.", Parameters = new() } };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

        ChatToolDecision? captured = null;
        engine.OnToolcallerDecision += d => captured = d;

        await engine.SendAsync("What does a NullReferenceException mean in C#?");

        Assert.Multiple(() =>
        {
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.Calls, Is.Empty);
            Assert.That(captured.Request, Is.EqualTo("What does a NullReferenceException mean in C#?"));
            Assert.That(captured.AvailableTools.Select(t => t.Name), Is.EquivalentTo(new[] { "read_file" }));
            Assert.That(captured.Model, Is.EqualTo("some-model"));
        });
    }

    private sealed class ToolCallingRuntime : IModelRuntime
    {
        private int _calls;
        public string RuntimeName => "ToolCalling";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

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
            await Task.Yield();
            // First call proposes a tool; every subsequent call (the loop's follow-up
            // query after executing it) returns plain text so the loop terminates.
            if (_calls++ == 0)
                onToolCall?.Invoke(new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "a.txt" } });
            else
                yield return "done";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);
        public RuntimeStats GetStats() => new(RuntimeName);
    }

    [Test]
    public async Task OnToolcallerDecision_FiresCall_ForNativeToolCall_BeforeExecuting()
    {
        var runtime = new ToolCallingRuntime();
        var tools = new List<ToolDefinition>
        {
            new() { Name = "read_file", Description = "Read.", Parameters = new(),
                     Handler = (args, ct) => Task.FromResult("file contents") },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

        ChatToolDecision? captured = null;
        engine.OnToolcallerDecision += d => captured = d;

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.Calls, Has.Count.EqualTo(1));
            Assert.That(captured.Calls[0].Name, Is.EqualTo("read_file"));
            Assert.That(captured.Request, Is.EqualTo("Please read a.txt for me."));
        });
    }

    // ── Orcish Tongue v1: Path 3 (ToolCallTextParser), Path 4 (repair lane),
    //    and the extended unexecuted-tool-attempt warning (docs/ORCISH_TONGUE_SPEC.md) ──────

    private sealed class BareJsonToolCallRuntime : IModelRuntime
    {
        private int _calls;
        public string RuntimeName => "BareJson";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

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
            await Task.Yield();
            // First call: no onToolCall invocation (native path stays empty) and no ReAct XML
            // wrapper -- exactly the live 2026-07-30 failure mode (docs/ORCISH_TONGUE_SPEC.md
            // §0.1): bare JSON in the shape a model's own OpenAI/Ollama-style training defaults
            // to. Subsequent call (RunNativeToolLoop's own post-execution "give me a final
            // answer" round-trip, same as the existing ToolCallingRuntime fake above) returns
            // plain text so the loop actually terminates with real final text instead of the
            // SAME bare JSON getting reinterpreted as a second unexecuted attempt.
            if (_calls++ == 0)
                yield return """{"name": "read_file", "arguments": {"path": "a.txt"}}""";
            else
                yield return "The file contains: file contents";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);
        public RuntimeStats GetStats() => new(RuntimeName);
    }

    [Test]
    public async Task Path3_BareJsonToolCall_ActuallyExecutes_NotJustRenderedAsText()
    {
        var runtime = new BareJsonToolCallRuntime();
        var executed = false;
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "read_file", Description = "Read.", Parameters = new(),
                Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
            },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

        string? finalText = null;
        engine.OnTurnComplete += t => finalText = t;

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.True, "the tool handler must actually run, not just be recognized as text");
            Assert.That(finalText, Is.Not.Null);
            Assert.That(finalText, Does.Not.Contain("\"name\": \"read_file\""),
                "the raw JSON tool-call attempt must not be shown verbatim as the final answer");
        });
    }

    [Test]
    public async Task Path3_DoesNotFire_WhenNoToolsOffered()
    {
        // Same "only attempted when tools exist" guard as Path 2 -- a no-tools chat mode must
        // never scan for JSON-brace tool-call shapes in ordinary text.
        var runtime = new BareJsonToolCallRuntime();
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: []);

        string? finalText = null;
        engine.OnTurnComplete += t => finalText = t;

        await engine.SendAsync("hello");

        Assert.That(finalText, Does.Contain("\"name\": \"read_file\""),
            "with zero tools registered, the bare JSON text must pass through unchanged, never parsed");
    }

    private sealed class MalformedJsonNameOnlyRuntime : IModelRuntime
    {
        public string RuntimeName => "MalformedJson";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

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
            await Task.Yield();
            // Deliberately unbalanced braces -- ToolCallTextParser's balanced-brace scanner
            // can never extract this, so Path 3 also comes up empty. This is the case Phase B's
            // extended regex exists for: something Paths 1-3 (and, with the repair lane
            // disabled, Path 4) all fail on, that the OLD regex also wouldn't have caught since
            // "name" appears as a JSON key, never immediately followed by "(".
            yield return """{"name": "read_file", "arguments": {"path": "a.txt" MALFORMED NO CLOSE""";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);
        public RuntimeStats GetStats() => new(RuntimeName);
    }

    [Test]
    public async Task PhaseB_UnrecognizableJsonObjectShape_TriggersUnsupportedFormatWarning()
    {
        var runtime = new MalformedJsonNameOnlyRuntime();
        var tools = new List<ToolDefinition> { new() { Name = "read_file", Description = "Read.", Parameters = new() } };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

        string? finalText = null;
        engine.OnTurnComplete += t => finalText = t;

        await engine.SendAsync("Please read a.txt for me.");

        Assert.That(finalText, Does.StartWith("⚠️ This model appears to have attempted a tool call in an unsupported format"));
    }

    private sealed class UnparseableThenRepairProposalRuntime(string repairModel) : IModelRuntime
    {
        public string RuntimeName => "UnparseableThenRepair";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());
        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

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
            await Task.Yield();
            if (model == repairModel)
            {
                // The repair lane's OWN call to theorc-toolcaller -- a valid "call" decision.
                yield return """{"decision": "call", "tool": "read_file", "arguments": {"path": "a.txt"}}""";
            }
            else
            {
                // The primary model: plain prose stating intent, no parseable call in any of
                // Paths 1-3's shapes -- exactly the scenario the repair lane exists for.
                yield return "I should read a.txt to answer that, let me do so.";
            }
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);
        public RuntimeStats GetStats() => new(RuntimeName);
    }

    [Test]
    public async Task Path4_RepairLane_ProposesAndExecutes_WhenPaths1Through3AllEmpty()
    {
        var originalEnabled = ToolcallerService.IsEnabled;
        var originalModel   = ToolcallerService.Model;
        ToolcallerService.IsEnabled = true;
        try
        {
            var runtime = new UnparseableThenRepairProposalRuntime(ToolcallerService.Model);
            var executed = false;
            var tools = new List<ToolDefinition>
            {
                new()
                {
                    Name = "read_file", Description = "Read.", Parameters = new(),
                    Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
                },
            };
            var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

            await engine.SendAsync("Please read a.txt for me.");

            Assert.That(executed, Is.True, "a valid repair-lane 'call' decision must actually execute");
        }
        finally
        {
            ToolcallerService.IsEnabled = originalEnabled;
            ToolcallerService.Model     = originalModel;
        }
    }

    [Test]
    public async Task Path4_RepairLane_NeverInvoked_WhenDisabled_FallsThroughToWarningInstead()
    {
        // ToolcallerService.IsEnabled defaults to false -- explicit here so this test doesn't
        // depend on the previous test's finally block having already run, and documents the
        // default behavior directly: Path 4 must be a true no-op when the setting is off.
        var originalEnabled = ToolcallerService.IsEnabled;
        ToolcallerService.IsEnabled = false;
        try
        {
            var runtime = new UnparseableThenRepairProposalRuntime(ToolcallerService.Model);
            var tools = new List<ToolDefinition> { new() { Name = "read_file", Description = "Read.", Parameters = new() } };
            var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

            string? finalText = null;
            engine.OnTurnComplete += t => finalText = t;

            await engine.SendAsync("Please read a.txt for me.");

            // Plain prose, no tool name in a recognizable shape -- falls all the way through to
            // the plain-response path unmodified (SanitizeFinalText's warning heuristics don't
            // match ordinary prose that merely mentions reading a file in passing).
            Assert.That(finalText, Is.EqualTo("I should read a.txt to answer that, let me do so."));
        }
        finally
        {
            ToolcallerService.IsEnabled = originalEnabled;
        }
    }

    [Test]
    public async Task Path4_RepairLane_ReturnsNull_ForToolOutsideTrainedVocabulary_MatchingSpecSection14()
    {
        // docs/ORCISH_TONGUE_SPEC.md §1.4/§3 Phase C: browser_navigate is NOT in
        // ToolcallerService's frozen KnownToolNames, so ProposeAsync must filter it out and
        // return null -- proving the repair lane honestly does NOT help with browser tools yet,
        // rather than silently guessing.
        var originalEnabled = ToolcallerService.IsEnabled;
        ToolcallerService.IsEnabled = true;
        try
        {
            var runtime = new UnparseableThenRepairProposalRuntime(ToolcallerService.Model);
            var tools = new List<ToolDefinition>
            {
                new() { Name = "browser_navigate", Description = "Navigate.", Parameters = new() },
            };
            var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);

            string? finalText = null;
            engine.OnTurnComplete += t => finalText = t;

            await engine.SendAsync("Please browse to example.com for me.");

            // No repair happened (FilterToKnownTools empties the list, ProposeAsync short-
            // circuits to null before ever calling the runtime) -- falls through to plain prose.
            Assert.That(finalText, Is.EqualTo("I should read a.txt to answer that, let me do so."));
        }
        finally
        {
            ToolcallerService.IsEnabled = originalEnabled;
        }
    }

    // ── Approval gate correctness fix (docs/ORCISH_TONGUE_SPEC.md, found 2026-07-30) ───────────
    // ChatEngine.ExecuteTool previously called def.Handler directly for every path with NO
    // approval gating at all -- these tests lock down the fix across native (already-working
    // path, same execution route Path 3/4 also use) and the repair lane (Path 4, the original
    // motivating concern: a schema-valid-but-wrong repair proposal for a mutating tool must not
    // execute unconditionally).

    [Test]
    public async Task ApprovalGate_RefusesByDefault_WhenOnApprovalRequiredNotWired()
    {
        // Fail-closed: a RequiresApproval tool with no callback set must be refused, not
        // silently executed -- same "no UI here, don't do something dangerous" posture as
        // BrowserTools' requireApprovalForNavigateAndDownload.
        var runtime = new ToolCallingRuntime();
        var executed = false;
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "read_file", Description = "Read.", Parameters = new(),
                RequiresApproval = true,
                Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
            },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);
        // OnApprovalRequired deliberately left unset.

        string? finalText = null;
        engine.OnTurnComplete += t => finalText = t;

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.False, "the handler must never run without an approval decision");
            // The rejection is fed back to the model as the tool's own result (same convention
            // ToolRegistry.ExecuteAsync already uses) so it can react/apologize/try something
            // else -- it lands in history, not necessarily as the turn's own final text (the
            // fake runtime's second call just yields "done", same as a real model synthesizing
            // a follow-up after seeing the result).
            Assert.That(engine.History.Any(m => m.Content.Contains("[REJECTED] User denied this action.")), Is.True);
        });
    }

    [Test]
    public async Task ApprovalGate_Executes_WhenCallbackApproves()
    {
        var runtime = new ToolCallingRuntime();
        var executed = false;
        ToolCall? seenByCallback = null;
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "read_file", Description = "Read.", Parameters = new(),
                RequiresApproval = true,
                Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
            },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools)
        {
            OnApprovalRequired = (tc, ct) => { seenByCallback = tc; return Task.FromResult(true); },
        };

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.True);
            Assert.That(seenByCallback, Is.Not.Null);
            Assert.That(seenByCallback!.Name, Is.EqualTo("read_file"));
        });
    }

    [Test]
    public async Task ApprovalGate_Refuses_WhenCallbackDenies()
    {
        var runtime = new ToolCallingRuntime();
        var executed = false;
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "read_file", Description = "Read.", Parameters = new(),
                RequiresApproval = true,
                Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
            },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools)
        {
            OnApprovalRequired = (tc, ct) => Task.FromResult(false),
        };

        string? finalText = null;
        engine.OnTurnComplete += t => finalText = t;

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.False);
            // See ApprovalGate_RefusesByDefault_WhenOnApprovalRequiredNotWired's own comment --
            // the rejection lands in history as the tool's result, not necessarily as this
            // turn's final text.
            Assert.That(engine.History.Any(m => m.Content.Contains("[REJECTED] User denied this action.")), Is.True);
        });
    }

    [Test]
    public async Task ApprovalGate_NotConsulted_ForToolsThatDoNotRequireApproval()
    {
        var runtime = new ToolCallingRuntime();
        var executed = false;
        var callbackInvoked = false;
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "read_file", Description = "Read.", Parameters = new(),
                // RequiresApproval left at its default (false).
                Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
            },
        };
        var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools)
        {
            OnApprovalRequired = (tc, ct) => { callbackInvoked = true; return Task.FromResult(true); },
        };

        await engine.SendAsync("Please read a.txt for me.");

        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.True);
            Assert.That(callbackInvoked, Is.False, "a tool that doesn't require approval must never even consult the gate");
        });
    }

    [Test]
    public async Task ApprovalGate_AppliesToPath4RepairLaneProposals_TheOriginalMotivatingConcern()
    {
        // The exact scenario ChatGPT's second-opinion review surfaced: a schema-valid-but-wrong
        // repair-lane proposal for a mutating tool (write_file/run_shell are both in
        // ToolcallerService's trained vocabulary) must not execute unconditionally just because
        // it passed decision/schema validation. Confirms the gate applies to Path 4 specifically,
        // not just the native/ReAct paths that already worked before this fix.
        var originalEnabled = ToolcallerService.IsEnabled;
        ToolcallerService.IsEnabled = true;
        try
        {
            var runtime = new UnparseableThenRepairProposalRuntime(ToolcallerService.Model);
            var executed = false;
            var tools = new List<ToolDefinition>
            {
                new()
                {
                    Name = "read_file", Description = "Read.", Parameters = new(),
                    RequiresApproval = true,
                    Handler = (args, ct) => { executed = true; return Task.FromResult("file contents"); },
                },
            };
            var engine = new ChatEngine(runtime, "some-model", systemPrompt: "", tools: tools);
            // OnApprovalRequired deliberately left unset -- fail-closed.

            string? finalText = null;
            engine.OnTurnComplete += t => finalText = t;

            await engine.SendAsync("Please read a.txt for me.");

            Assert.Multiple(() =>
            {
                Assert.That(executed, Is.False, "a repair-lane proposal for a RequiresApproval tool must still be gated");
                // See ApprovalGate_RefusesByDefault_WhenOnApprovalRequiredNotWired's own comment
                // -- the rejection lands in history as the tool's result, not necessarily as
                // this turn's final text.
                Assert.That(engine.History.Any(m => m.Content.Contains("[REJECTED] User denied this action.")), Is.True);
            });
        }
        finally
        {
            ToolcallerService.IsEnabled = originalEnabled;
        }
    }
}
