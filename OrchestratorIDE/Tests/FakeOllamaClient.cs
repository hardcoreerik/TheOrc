// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Tests;

/// <summary>
/// Scripted fake for OllamaClient. Lets tests drive the agent loop through
/// deterministic scenarios — write_file approvals, shell command gates, sandbox
/// bypass dialogs — without a running Ollama server.
///
/// Usage:
///   var fake = new FakeOllamaClient();
///
///   // 1. Queue a plain text reply
///   fake.Enqueue("Here is my plan.");
///
///   // 2. Queue a tool call
///   fake.EnqueueToolCall("write_file", new() {
///       ["path"]    = "hello.txt",
///       ["content"] = "hello world",
///       ["reason"]  = "write greeting"
///   });
///
///   // 3. Queue a follow-up reply after the tool result comes back
///   fake.Enqueue("Done! I wrote hello.txt.");
///
///   // Then run the agent loop normally — it will receive these in order.
/// </summary>
public class FakeOllamaClient : OllamaClient
{
    // ── Script queue ──────────────────────────────────────────────────────────

    private readonly Queue<ScriptedTurn> _script = new();

    public FakeOllamaClient() : base("http://fake-local:11434") { }

    // ── Scripting API ─────────────────────────────────────────────────────────

    /// <summary>Queue a plain text response for the next StreamCompletionAsync call.</summary>
    public void Enqueue(string text) =>
        _script.Enqueue(new ScriptedTurn(text, null));

    /// <summary>Queue a tool call response for the next StreamCompletionAsync call.</summary>
    public void EnqueueToolCall(string toolName, Dictionary<string, object?> args) =>
        _script.Enqueue(new ScriptedTurn(null, new ScriptedToolCall(toolName, args)));

    /// <summary>Clear all remaining queued responses.</summary>
    public void Reset() => _script.Clear();

    /// <summary>True when all queued responses have been consumed.</summary>
    public bool IsEmpty => _script.Count == 0;

    // ── Override completion ───────────────────────────────────────────────────

    public override async IAsyncEnumerable<string> StreamCompletionAsync(
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
        if (_script.Count == 0)
        {
            yield return "[FakeOllamaClient] Script queue is empty — no more responses.";
            yield break;
        }

        var turn = _script.Dequeue();
        await Task.Yield();    // simulate async handoff

        if (turn.ToolCall is not null)
        {
            // Emit the tool call through the callback then yield nothing (like a real
            // model that emits only a tool call and no text content).
            var call = new ToolCall
            {
                Name      = turn.ToolCall.Name,
                Arguments = turn.ToolCall.Args,
                Status    = ToolCallStatus.Pending,
            };
            onToolCall?.Invoke(call);
            onUsage?.Invoke(10, 5);
            yield break;
        }

        // Plain text — stream word-by-word so streaming display is exercised
        if (turn.Text is not null)
        {
            foreach (var word in turn.Text.Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                yield return word + " ";
                await Task.Delay(1, ct);
            }
            onUsage?.Invoke(10, turn.Text.Split(' ').Length);
        }
    }

    // ── Override connectivity / model list ────────────────────────────────────

    public override Task<bool> IsReachableAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    public override Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<string> { "fake-model:7b" });

    // ── Private types ─────────────────────────────────────────────────────────

    private record ScriptedTurn(string? Text, ScriptedToolCall? ToolCall);
    private record ScriptedToolCall(string Name, Dictionary<string, object?> Args);
}

// ── Scenario builder helpers ──────────────────────────────────────────────────

/// <summary>
/// Pre-built scenario scripts for common trust-path test cases.
/// </summary>
public static class FakeScenarios
{
    /// <summary>
    /// Agent plans, then issues a write_file, then summarises.
    /// Tests: diff approval gate fires before file is written.
    /// </summary>
    public static void WriteFileFlow(FakeOllamaClient fake, string targetPath = "output.txt", string content = "hello")
    {
        fake.Enqueue("I will write the file now.");
        fake.EnqueueToolCall("write_file", new()
        {
            ["path"]    = targetPath,
            ["content"] = content,
            ["reason"]  = "Writing requested output",
        });
        fake.Enqueue($"Done — wrote {targetPath}.");
    }

    /// <summary>
    /// Agent issues a run_shell command, then summarises.
    /// Tests: shell approval card appears.
    /// </summary>
    public static void ShellFlow(FakeOllamaClient fake, string command = "echo hello")
    {
        fake.Enqueue("Running the shell command now.");
        fake.EnqueueToolCall("run_shell", new()
        {
            ["command"] = command,
            ["reason"]  = "Test shell execution",
        });
        fake.Enqueue("Shell command finished.");
    }

    /// <summary>
    /// Agent attempts to write outside the workspace.
    /// Tests: sandbox bypass dialog fires.
    /// </summary>
    public static void SandboxEscapeFlow(FakeOllamaClient fake)
    {
        fake.Enqueue("Writing outside the workspace.");
        fake.EnqueueToolCall("write_file", new()
        {
            ["path"]    = @"C:\Windows\System32\evil.txt",
            ["content"] = "should be blocked",
            ["reason"]  = "Sandbox escape test",
        });
        fake.Enqueue("File write attempted.");
    }

    /// <summary>
    /// Agent issues a destructive shell command.
    /// Tests: ToolPolicyEngine hard-blocks it before approval UI is shown.
    /// </summary>
    public static void DestructiveShellFlow(FakeOllamaClient fake)
    {
        fake.Enqueue("Attempting destructive command.");
        fake.EnqueueToolCall("run_shell", new()
        {
            ["command"] = "rm -rf /",
            ["reason"]  = "Destructive shell test",
        });
        fake.Enqueue("Command issued.");
    }
}
