// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Research;

/// <summary>
/// Provides ToolDefinition instances for the research-chat tools and the
/// ReAct system prompt fallback for models that lack native tool calling.
///
/// Two execution paths share the same underlying tool implementations:
///  • Native  — model returns structured tool_calls via the API
///  • ReAct   — model embeds XML markers in plain text; we parse and execute them
/// </summary>
public static class ResearchToolset
{
    // ── Tool definitions (OpenAI function-calling schema) ─────────────────────

    public static List<ToolDefinition> GetTools(
        WebSearchTool webSearch,
        FetchPageTool fetchPage)
    {
        return
        [
            new ToolDefinition
            {
                Name        = "web_search",
                Description = "Search the web using DuckDuckGo. Returns titles, URLs, and snippets for the most relevant results. Use this first when you need current information or facts you're not certain about.",
                Parameters  = new Dictionary<string, ToolParameter>
                {
                    ["query"]       = new("string",  "The search query. Be specific. Use quotes around phrases."),
                    ["max_results"] = new("integer", "How many results to return. Default: 6, max: 10."),
                },
                Required    = ["query"],
                Handler     = async (args, ct) =>
                {
                    var query = GetString(args, "query");
                    var max   = GetInt(args, "max_results", 6);
                    if (string.IsNullOrWhiteSpace(query))
                        return "Error: query is required.";

                    var results = await webSearch.SearchAsync(query, Math.Clamp(max, 1, 10), ct);
                    if (results.Length == 0)
                        return "No results found. Try a different query.";

                    var sb = new StringBuilder();
                    sb.AppendLine($"Search results for: **{query}**\n");
                    for (int i = 0; i < results.Length; i++)
                    {
                        var r = results[i];
                        sb.AppendLine($"{i + 1}. [{r.Title}]({r.Url})");
                        if (!string.IsNullOrEmpty(r.Snippet))
                            sb.AppendLine($"   {r.Snippet}");
                        sb.AppendLine();
                    }
                    return sb.ToString();
                }
            },

            new ToolDefinition
            {
                Name        = "fetch_page",
                Description = "Fetch and extract readable content from a URL. Use this to deep-read an article, documentation page, or any web page after finding it via web_search. Returns clean text and links.",
                Parameters  = new Dictionary<string, ToolParameter>
                {
                    ["url"]  = new("string", "The full URL to fetch (must start with http:// or https://)."),
                    ["mode"] = new("string", "'text' for article body text (default), or 'links' to list all hyperlinks on the page."),
                },
                Required    = ["url"],
                Handler     = async (args, ct) =>
                {
                    var url  = GetString(args, "url");
                    var mode = GetString(args, "mode", "text");
                    if (string.IsNullOrWhiteSpace(url))
                        return "Error: url is required.";

                    var result = await fetchPage.FetchAsync(url, mode, maxChars: 8000, ct);
                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(result.Title))
                        sb.AppendLine($"# {result.Title}\n");
                    sb.AppendLine($"Source: {result.Url}\n");
                    sb.AppendLine(result.Text);
                    return sb.ToString();
                }
            },
        ];
    }

    // ── ReAct system prompt (for models without native tool calling) ──────────

    /// <summary>
    /// Injected into the system prompt when a model doesn't support tool_calls.
    /// Teaches the model to request tools via XML markers that we parse from the response.
    /// </summary>
    public static string ReActSystemPrompt => """
        You are a research assistant with access to the web. You can search for information and read web pages.

        To use a tool, output ONLY this block (nothing before or after it on the same lines):

        <tool_call>
        <name>TOOL_NAME</name>
        <args>{"param": "value"}</args>
        </tool_call>

        I will execute the tool and return the result in a <tool_result> block. Then continue your response.

        Available tools:
        - web_search(query, max_results=6) — Search DuckDuckGo. Returns titles, URLs, snippets.
        - fetch_page(url, mode="text") — Fetch and extract a web page. mode: "text" or "links".

        Rules:
        - Always search before answering questions about current events, facts you're unsure of, or specific data.
        - After searching, use fetch_page on the most relevant result URL to get the full article.
        - Always cite your sources with the URL in your final answer.
        - Format your final answer in clean markdown with clickable [links](url).
        - One tool call per response block — wait for results before calling another.
        """;

    /// <summary>
    /// The base research system prompt that is ALWAYS injected.
    /// Appended with the ReAct block when native tool calling is not available.
    /// </summary>
    public static string BaseSystemPrompt => """
        You are a research assistant integrated into TheOrc IDE. Your purpose is research, analysis, and information gathering — not writing code.

        Guidelines:
        - Be thorough: search for information, read source pages, then synthesise.
        - Be accurate: cite sources. If unsure, say so.
        - Format responses in markdown: use headers, bullet points, and **bold** for key facts.
        - Make URLs clickable as [descriptive text](url).
        - For complex topics, break the answer into sections.
        - Keep responses focused and well-structured — this is a research tool, not a general chatbot.
        """;

    // ── ReAct response parser ─────────────────────────────────────────────────

    /// <summary>
    /// Parses a model's text response for embedded <tool_call> XML blocks.
    /// Returns all tool call requests found.
    /// </summary>
    public static List<ToolCallRequest> ParseReActCalls(string text)
    {
        var calls   = new List<ToolCallRequest>();
        var pattern = new System.Text.RegularExpressions.Regex(
            @"<tool_call>\s*<name>(.*?)</name>\s*<args>(.*?)</args>\s*</tool_call>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            var name    = m.Groups[1].Value.Trim();
            var rawArgs = m.Groups[2].Value.Trim();

            Dictionary<string, object?> args = [];
            try
            {
                if (!string.IsNullOrEmpty(rawArgs))
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawArgs) ?? [];
            }
            catch { /* malformed JSON — pass empty args */ }

            calls.Add(new ToolCallRequest
            {
                Name      = name,
                Args      = args,
                FullMatch = m.Value,
            });
        }
        return calls;
    }

    // ── Model capability detection ────────────────────────────────────────────

    /// <summary>
    /// Returns true for models known to support native OpenAI-style function calling
    /// through the Ollama / llama.cpp API. Models not in this list fall back to ReAct.
    ///
    /// This is a best-effort list — tool support depends on the model's chat template.
    /// We always TRY native first and detect the fallback from the response shape.
    /// </summary>
    public static bool KnownNativeToolSupport(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        return id.Contains("qwen")      ||
               id.Contains("llama3")    ||
               id.Contains("llama-3")   ||
               id.Contains("mistral")   ||
               id.Contains("hermes")    ||
               id.Contains("nemotron")  ||
               id.Contains("phi4")      ||
               id.Contains("phi-4")     ||
               id.Contains("granite")   ||
               id.Contains("deepseek")  ||
               id.Contains("command-r");
    }

    // ── Argument helpers ──────────────────────────────────────────────────────

    private static string GetString(Dictionary<string, object?> args, string key, string def = "")
    {
        if (!args.TryGetValue(key, out var v)) return def;
        return v is System.Text.Json.JsonElement je ? je.GetString() ?? def : v?.ToString() ?? def;
    }

    private static int GetInt(Dictionary<string, object?> args, string key, int def)
    {
        if (!args.TryGetValue(key, out var v)) return def;
        if (v is System.Text.Json.JsonElement je)
        {
            if (je.TryGetInt32(out var i)) return i;
            if (int.TryParse(je.GetRawText(), out var p)) return p;
        }
        return int.TryParse(v?.ToString(), out var r) ? r : def;
    }
}

public class ToolCallRequest
{
    public string                       Name      { get; set; } = "";
    public Dictionary<string, object?>  Args      { get; set; } = [];
    public string                       FullMatch { get; set; } = "";  // original XML in text
}
