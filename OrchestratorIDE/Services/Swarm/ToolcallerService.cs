// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Runtime client for theorc-toolcaller — the first Foundry specialist
/// (Qwen2.5-1.5B + the currently promoted LoRA round; gate scores live in
/// docs/TOOLCALLER_REFUSAL_GAUNTLET.md and the shipped modelfile's header,
/// so they aren't duplicated here to rot).
///
/// V0 integration is the opt-in REPAIR LANE only (AppSettings.ToolcallerRepairEnabled,
/// default off): when a swarm worker turn produces content but no parseable tool call —
/// neither structured tool_calls nor a text-format fallback parse — the specialist gets
/// one shot at converting the worker's stated intent into a proper proposal. A "call"
/// decision re-enters the normal execution loop, so ToolPolicyEngine and human approval
/// remain fully authoritative; any other decision leaves today's behavior untouched.
///
/// The prompt below MUST stay byte-identical to SYSTEM_TEMPLATE and render_tools_block
/// in training_pit/foundry/scripts/export_toolcaller_dataset.py — the model was trained
/// on exactly that serialization, and drift here silently degrades it.
/// </summary>
public static class ToolcallerService
{
    /// <summary>Mirror of AppSettings.ToolcallerRepairEnabled — set at startup/settings-save.</summary>
    public static bool IsEnabled { get; set; } = false;

    /// <summary>Ollama tag of the specialist. Built from training_pit/modelfiles/toolcaller-qwen25-1.5b.modelfile.</summary>
    public static string Model { get; set; } = "theorc-toolcaller:qwen25-1.5b";

    /// <summary>
    /// Provenance marker stamped into ToolCall.ExplainWhy for repair-lane proposals.
    /// ToolcallerDatasetCapture MUST skip calls carrying it — staging the specialist's
    /// own outputs as "organic" examples would feed the model its own behavior in the
    /// next training round (self-distillation contamination).
    /// </summary>
    public const string RepairProvenanceMarker = "toolcaller repair lane";

    public sealed record Decision(
        string  Kind,        // call | no_tool | clarify | unsupported | (parse_error)
        string? Tool,
        Dictionary<string, object?>? Arguments,
        string? ReasonCode,
        string  Raw);

    /// <summary>
    /// Ask the specialist for a tool proposal. Returns null on any failure (model missing
    /// on this node, timeout, unparseable output) — the repair lane is strictly best-effort
    /// and must never make a swarm run worse than the no-toolcaller baseline.
    /// Takes both client shapes because SwarmSession's worker loop branches the same way:
    /// nodeClient (HIVE node's OllamaClient) wins when present, else the local IModelRuntime.
    /// </summary>
    public static async Task<Decision?> ProposeAsync(
        IModelRuntime?                runtime,
        OllamaClient?                 nodeClient,
        SwarmWorkerRole               role,
        IReadOnlyList<ToolDefinition> tools,
        string                        request,
        CancellationToken             ct)
    {
        if (!IsEnabled || tools.Count == 0 || string.IsNullOrWhiteSpace(request))
            return null;
        if (runtime is null && nodeClient is null)
            return null;

        try
        {
            var history = new List<AgentMessage>
            {
                new() { Role = MessageRole.System, Content = BuildSystemPrompt(role, tools), Status = MessageStatus.Complete },
                new() { Role = MessageRole.User,   Content = request,                        Status = MessageStatus.Complete },
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var stream = nodeClient is not null
                ? nodeClient.StreamCompletionAsync(Model, history, temperature: 0, maxTokens: 512, ct: timeout.Token)
                : runtime!.StreamCompletionAsync(Model, history, temperature: 0, maxTokens: 512, ct: timeout.Token);

            var sb = new StringBuilder();
            await foreach (var token in stream)
                sb.Append(token);

            return ParseDecision(sb.ToString());
        }
        catch
        {
            return null; // best-effort: absent model / node error / timeout ⇒ no repair
        }
    }

    /// <summary>
    /// Convert a "call" decision into a runnable ToolCall, or null when the proposal
    /// names a tool outside the live set (never execute an invented tool).
    /// </summary>
    public static ToolCall? ToToolCall(Decision d, IReadOnlyList<ToolDefinition> tools)
    {
        if (d.Kind != "call" || d.Tool is null) return null;
        if (!tools.Any(t => t.Name == d.Tool))  return null;
        return new ToolCall
        {
            Name         = d.Tool,
            Arguments    = d.Arguments ?? [],
            IsTextFormat = true,   // result re-enters history as a user message, like text-parsed calls
            ExplainWhy   = $"proposed by {Model} ({RepairProvenanceMarker})",
        };
    }

    // ── Prompt construction (byte-identical to export_toolcaller_dataset.py) ──

    internal static string BuildSystemPrompt(SwarmWorkerRole role, IReadOnlyList<ToolDefinition> tools)
    {
        var block = new StringBuilder();
        foreach (var t in tools)
        {
            block.Append($"- {t.Name}: {t.Description}\n");
            if (t.Parameters.Count == 0)
            {
                block.Append("    (no parameters)\n");
                continue;
            }
            var required = t.Required.ToHashSet();
            foreach (var (name, p) in t.Parameters)
                block.Append($"    - {name} ({p.Type}{(required.Contains(name) ? ", required" : "")}): {p.Description}\n");
        }

        return
$@"You are theorc-toolcaller, TheOrc's tool-proposal specialist.
Your only job: given a worker role, its available tools, and a request, propose the single correct next action as JSON.

Role: {RoleToken(role)}

Available tools:
{block.ToString().TrimEnd('\n')}

Respond with EXACTLY one JSON object, no prose, in one of these shapes:
  {{""decision"": ""call"", ""tool"": ""<name from available tools>"", ""arguments"": {{...exact schema fields only...}}}}
  {{""decision"": ""no_tool""}}
  {{""decision"": ""clarify"", ""reason_code"": ""<missing_required_argument|ambiguous_target|ambiguous_intent>""}}
  {{""decision"": ""unsupported"", ""reason_code"": ""<no_matching_tool|tool_outside_role>""}}

Rules:
- Never invent tools or argument fields. Arguments must match the tool's schema exactly.
- If required information is missing or ambiguous, choose ""clarify"" — never fabricate arguments.
- You propose; you never execute. Deterministic policy and human approval decide whether the call runs.";
    }

    /// <summary>Same tokens as ToolcallerDatasetCapture.RoleToken — training-set role vocabulary.</summary>
    private static string RoleToken(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => "researcher",
        SwarmWorkerRole.Coder       => "coder",
        SwarmWorkerRole.UIDeveloper => "ui_developer",
        SwarmWorkerRole.Tester      => "tester",
        _                           => "unknown",
    };

    // ── Output parsing ────────────────────────────────────────────────────────

    internal static Decision? ParseDecision(string raw)
    {
        var json = ExtractJson(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("decision", out var d) ? d.GetString() ?? "" : "";
            if (kind is not ("call" or "no_tool" or "clarify" or "unsupported")) return null;

            Dictionary<string, object?>? args = null;
            if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                args = [];
                foreach (var prop in a.EnumerateObject())
                    args[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.True   => true,
                        JsonValueKind.False  => false,
                        JsonValueKind.Null   => null,
                        _                    => prop.Value.GetRawText(),
                    };
            }

            return new Decision(
                kind,
                root.TryGetProperty("tool", out var t) ? t.GetString() : null,
                args,
                root.TryGetProperty("reason_code", out var r) ? r.GetString() : null,
                raw);
        }
        catch { return null; }
    }

    private static string? ExtractJson(string text)
    {
        text = text.Trim();
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inStr = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
            switch (ch)
            {
                case '"': inStr = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }
}
