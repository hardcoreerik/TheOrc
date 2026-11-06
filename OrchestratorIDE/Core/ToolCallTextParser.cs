// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Parses text-format tool calls from raw model output.
/// Single source of truth shared by AgentLoop and LLamaSharpRuntime. On the native runtime path,
/// output is now GBNF grammar-constrained (see <see cref="Runtime.ToolCallGrammarBuilder"/>)
/// before it ever reaches this parser, so malformed/fabricated tool names become unreachable
/// rather than needing to be caught here -- this parser still runs unchanged either way (grammar
/// constrains syntax, not the parsing step), and remains the only parser for the Ollama/server
/// backends, which are not grammar-constrained.
/// </summary>
public static class ToolCallTextParser
{
    private static readonly Regex _fenceRegex =
        new(@"```(?:json)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ToolCall> Parse(string text)
    {
        var result = new List<ToolCall>();

        // Gemma 4's native form (call:name{key:value,...}) is checked first: its string values can
        // hold JSON/code, which the brace scanner below would misread as phantom calls.
        var gemma = ParseGemmaCalls(text);
        if (gemma.Count > 0) return gemma;

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

    // ── Gemma 4 native tool calls ────────────────────────────────────────────────────────────
    // Gemma 4 writes calls as `call:write_file{content:<|"|>...<|"|>,path:<|"|>a.py<|"|>}`.
    // When the special tokens are stripped (llama.cpp detokenization, or StripGemma4Artifacts)
    // the values are bare: `call:write_file{content:...,path:a.py}`.

    private const string GemmaQuote = "<|\"|>";

    private static readonly Regex _gemmaStartRegex =
        new(@"call:([A-Za-z_][A-Za-z0-9_]*)\{", RegexOptions.Compiled);

    // Bare values can contain commas and "key:" text (Python dicts), so a split only counts at a
    // known argument name.
    private static readonly string[] _gemmaArgNames =
    [
        "path", "content", "command", "reason", "query", "pattern", "directory", "old_string",
        "new_string", "url", "text", "selector", "file", "glob", "include", "max_results",
        "start_line", "end_line", "symbol", "depth", "timeout", "name", "id",
    ];

    private static readonly Regex _gemmaKeyRegex =
        new(@"(?:^|,)\s*(" + string.Join("|", _gemmaArgNames) + @"):", RegexOptions.Compiled);

    internal static List<ToolCall> ParseGemmaCalls(string text)
    {
        var calls = new List<ToolCall>();
        if (string.IsNullOrEmpty(text) || !text.Contains("call:")) return calls;

        var starts = _gemmaStartRegex.Matches(text);
        for (var n = 0; n < starts.Count; n++)
        {
            var m = starts[n];
            var bodyStart = m.Index + m.Length;
            var segEnd = n + 1 < starts.Count ? starts[n + 1].Index : text.Length;

            var args = text.AsSpan(bodyStart, segEnd - bodyStart).Contains(GemmaQuote.AsSpan(), StringComparison.Ordinal)
                ? ParseGemmaQuotedArgs(text, bodyStart, segEnd)
                : ParseGemmaBareArgs(text, bodyStart, segEnd);

            if (args is null) continue;
            calls.Add(new ToolCall
            {
                Id           = Guid.NewGuid().ToString("N")[..8],
                Name         = m.Groups[1].Value,
                Arguments    = args,
                IsTextFormat = true,
            });
        }
        return calls;
    }

    // key:<|"|>value<|"|>,key2:<|"|>value2<|"|>}
    private static Dictionary<string, object?>? ParseGemmaQuotedArgs(string text, int pos, int limit)
    {
        var args = new Dictionary<string, object?>();
        while (pos < limit)
        {
            while (pos < limit && (char.IsWhiteSpace(text[pos]) || text[pos] == ',')) pos++;
            if (pos >= limit || text[pos] == '}') break;

            var colon = text.IndexOf(':', pos, limit - pos);
            if (colon < 0) break;
            var key = text[pos..colon].Trim();
            pos = colon + 1;

            if (string.CompareOrdinal(text, pos, GemmaQuote, 0, GemmaQuote.Length) == 0)
            {
                pos += GemmaQuote.Length;
                var close = text.IndexOf(GemmaQuote, pos, limit - pos, StringComparison.Ordinal);
                if (close < 0) { args[key] = text[pos..limit]; return args; }  // truncated output
                args[key] = text[pos..close];
                pos = close + GemmaQuote.Length;
            }
            else
            {
                var end = pos;
                while (end < limit && text[end] != ',' && text[end] != '}') end++;
                args[key] = text[pos..end].Trim();
                pos = end;
            }
        }
        return args.Count > 0 ? args : null;
    }

    // key:value,key2:value2}   (no delimiters, so split at known argument names only)
    private static Dictionary<string, object?>? ParseGemmaBareArgs(string text, int pos, int limit)
    {
        var body = text[pos..limit].TrimEnd();
        var lastBrace = body.LastIndexOf('}');
        if (lastBrace >= 0) body = body[..lastBrace];

        var keys = _gemmaKeyRegex.Matches(body);
        if (keys.Count == 0) return null;

        var args = new Dictionary<string, object?>();
        for (var k = 0; k < keys.Count; k++)
        {
            var valStart = keys[k].Index + keys[k].Length;
            var valEnd   = k + 1 < keys.Count ? keys[k + 1].Index : body.Length;
            args[keys[k].Groups[1].Value] = body[valStart..valEnd].Trim();
        }
        return args;
    }
}
