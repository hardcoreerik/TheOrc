// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Parses text-format tool calls from raw model output.
/// Single source of truth shared by AgentLoop and LLamaSharpRuntime.
/// Phase 3 will replace this with GBNF-constrained generation.
/// </summary>
public static class ToolCallTextParser
{
    private static readonly Regex _fenceRegex =
        new(@"```(?:json)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ToolCall> Parse(string text)
    {
        var result = new List<ToolCall>();
        // Fix #8: use pre-compiled static Regex.
        var stripped = _fenceRegex.Replace(text, "").Trim();

        int i = 0;
        while (i < stripped.Length)
        {
            var start = stripped.IndexOf('{', i);
            if (start < 0) break;

            int depth = 0, end = -1;
            bool inString = false;
            for (int j = start; j < stripped.Length; j++)
            {
                var ch = stripped[j];
                if (ch == '"')
                {
                    // Count consecutive preceding backslashes: even = real quote, odd = escaped.
                    var bs = 0;
                    for (var k = j - 1; k >= start && stripped[k] == '\\'; k--) bs++;
                    if (bs % 2 == 0) inString = !inString;
                }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }

            if (end < 0) break;

            try
            {
                var node = JsonNode.Parse(stripped[start..(end + 1)]);
                var name = node?["name"]?.GetValue<string>()
                        ?? node?["tool"]?.GetValue<string>()
                        ?? node?["function"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(name))
                {
                    // Fix #4: align with AgentLoop.TryParseTextToolCalls — add "inputs" alias
                    // and flat-format fallback (all non-name keys treated as args).
                    var argsNode = node?["arguments"]
                                ?? node?["args"]
                                ?? node?["parameters"]
                                ?? node?["inputs"];

                    var args = new Dictionary<string, object?>();
                    var argsObj = argsNode as JsonObject
                               ?? (argsNode == null
                                   ? node!.AsObject()
                                         .Where(kv => kv.Key is not ("name" or "tool" or "function"))
                                         .Aggregate(new JsonObject(), (acc, kv) =>
                                         {
                                             acc[kv.Key] = kv.Value?.DeepClone();
                                             return acc;
                                         })
                                   : null);

                    if (argsObj != null)
                        foreach (var kvp in argsObj)
                            args[kvp.Key] = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                                ? s
                                : kvp.Value?.ToJsonString() ?? "";

                    result.Add(new ToolCall
                    {
                        Id           = Guid.NewGuid().ToString("N")[..8],
                        Name         = name,
                        Arguments    = args,
                        IsTextFormat = true,
                    });
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* malformed JSON — skip */ }

            i = end + 1;
        }

        return result;
    }
}
