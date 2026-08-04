// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// ORCISH TONGUE Phase 2 (plan elegant-bubbling-coral.md) — compiles the live tool list into a
/// GBNF grammar for LLamaSharp's <see cref="LLama.Sampling.DefaultSamplingPipeline.Grammar"/>,
/// making it structurally impossible for the native runtime to emit a tool-call JSON referencing
/// a name outside the tool list it was actually given — the plausible_fabrication problem
/// (round 1/2 of the toolcaller-v2 track) solved at the decoder level instead of by training.
///
/// Scope (see plan's "Explicitly out of scope" section — this is intentional, not a shortcut):
/// constrains the tool NAME to the live registry and the overall JSON shape. Argument VALUES are
/// only constrained to well-formed JSON (string/number/bool/null/nested object), not validated
/// per-tool-per-key — exact required/optional argument-key enforcement per tool would need a
/// combinatorial per-tool grammar and is semantic-correctness territory the toolcaller-v2
/// specialist and ToolPolicyEngine already own. This grammar's only hard guarantee is: the name
/// is real, and the arguments are syntactically valid JSON.
///
/// root ::= tool-call | free-text -- a turn is not always a tool call, so the grammar must allow
/// ordinary conversational replies too. free-text is deliberately permissive (anything not
/// starting with '{'); tool-call is deliberately strict (name must be one of the live tools).
/// </summary>
public static class ToolCallGrammarBuilder
{
    private static readonly JsonSerializerOptions _wireJson = new() { WriteIndented = false };

    /// <summary>
    /// Builds a GBNF grammar string (root rule "root") from the same loosely-typed tools list
    /// StreamCompletionAsync already receives (the wire-schema shape ToolDefinition.ToOllamaSchema()
    /// produces: {"type":"function","function":{"name":...,"parameters":{...}}}). Returns null if
    /// no valid tool names could be extracted (caller should fall back to unconstrained decoding
    /// rather than emit a grammar with an empty alternation, which would make ALL tool calls
    /// unreachable -- worse than no grammar at all).
    /// </summary>
    public static string? Build(IReadOnlyList<object> tools)
    {
        var names = ExtractToolNames(tools);
        if (names.Count == 0)
            return null;

        var nameAlternation = string.Join(" | ", names.Select(GbnfQuotedString));

        return $$"""
            root        ::= tool-call | free-text
            tool-call   ::= "{" ws "\"name\"" ws ":" ws tool-name ws "," ws "\"arguments\"" ws ":" ws args-object ws "}"
            tool-name   ::= {{nameAlternation}}
            args-object ::= "{" ws "}" | "{" ws pair (ws "," ws pair)* ws "}"
            pair        ::= json-string ws ":" ws json-value
            json-value  ::= json-string | json-number | "true" | "false" | "null" | args-object
            json-string ::= "\"" str-char* "\""
            str-char    ::= [^"\\] | "\\" ["\\/bfnrt]
            json-number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [+-]? [0-9]+)?
            ws          ::= [ \t\n]*
            free-text   ::= [^{] [^\x00]*
            """;
    }

    /// <summary>
    /// Reads tool names out of the wire-schema object list the same way NativePromptBuilder
    /// already does (JsonSerializer.Serialize(tools, ...) then walk the JSON generically) --
    /// reused deliberately rather than assuming a strong ToolDefinition type, since by the time
    /// this list reaches StreamCompletionAsync it's already been through ToWireSchema() and is a
    /// list of anonymous OllamaSchema objects, not ToolDefinition instances.
    /// </summary>
    private static List<string> ExtractToolNames(IReadOnlyList<object> tools)
    {
        var names = new List<string>();
        var json = JsonSerializer.Serialize(tools, _wireJson);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            // Wire shape: {"type":"function","function":{"name":"...", ...}}. Defensive fallback
            // to a flat {"name":"..."} shape in case a future caller passes tools pre-unwrapped.
            string? name = null;
            if (entry.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n1))
                name = n1.GetString();
            else if (entry.TryGetProperty("name", out var n2))
                name = n2.GetString();

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>Renders a literal string as a GBNF-quoted terminal, escaping backslash/quote so a
    /// tool name containing either doesn't break the grammar's own syntax.</summary>
    private static string GbnfQuotedString(string value)
    {
        var sb = new StringBuilder("\"\\\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append("\\\"\"");
        return sb.ToString();
    }
}
