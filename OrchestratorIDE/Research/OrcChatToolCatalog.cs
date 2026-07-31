// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Tools;

namespace OrchestratorIDE.Research;

/// <summary>
/// TheOrc Chat's general-purpose tool pack. These are the highest-value tools for an
/// interactive coding/research chat: web lookup, page fetch, workspace inspection,
/// file reads/writes, search, outline, verification, and first-class markdown export.
/// The pack intentionally excludes arbitrary shell execution from ChatPanel so this
/// surface stays closer to ChatGPT-style assistance than to a raw remote terminal.
/// </summary>
public static class OrcChatToolCatalog
{
    public static IReadOnlyList<string> TopToolNames =>
    [
        "web_search",
        "fetch_page",
        "fetch_url",
        "list_files",
        "read_file",
        "write_file",
        "grep_code",
        "get_outline",
        "library_list",
        "library_search",
        "library_open",
        "library_graph",
        "run_tests",
        "save_markdown_document",
        "browser_navigate",
        "browser_click",
        "browser_type",
        "browser_wait",
        "browser_extract",
        "browser_screenshot",
        "browser_download",
        "model3d_create",
        "model3d_status",
        "model3d_cancel",
        "model3d_open",
    ];

    /// <param name="onDiffPreview">
    ///   Forwarded to <see cref="FileTools.Register"/> for <c>write_file</c>'s own diff-preview
    ///   gate. Previously always null here, which meant OrcChat writes proceeded with NO
    ///   confirmation at all (docs/ORCISH_TONGUE_SPEC.md's correctness-fix follow-up, found
    ///   2026-07-30: this whole registry+queue pair is discarded right after construction --
    ///   <c>ChatEngine</c> never executes through it, so the queue itself was never the reason
    ///   writes were ungated. The real fix is threading a REAL callback in from the caller, which
    ///   this parameter now allows).
    /// </param>
    public static List<ToolDefinition> CreateWorkspaceTools(string workspaceRoot,
        Func<string, string, string, string, CancellationToken, Task<bool>>? onDiffPreview = null)
    {
        var approvals = new Trust.ApprovalQueue();
        var registry = new ToolRegistry(approvals);

        FileTools.Register(registry, workspaceRoot, onDiffPreview: onDiffPreview);
        SearchTools.Register(registry, workspaceRoot);
        FabricTools.Register(registry, workspaceRoot);
        TestTools.Register(registry, workspaceRoot);
        WebTools.Register(registry);
        // requireApprovalForNavigateAndDownload now defaults to true (Orcish Tongue v1
        // correctness fix): the original "false" here was because THIS throwaway registry+queue
        // pair has no ApprovalRequested subscriber, which would have hung RequestApprovalAsync
        // forever under Guarded trust -- but ChatEngine never actually executes tools through
        // this ToolRegistry/ApprovalQueue at all (see ChatEngine.OnApprovalRequired's own doc
        // comment), so that concern never applied to the real execution path in the first place.
        // ChatEngine.ExecuteTool now checks ToolDefinition.RequiresApproval directly and gates
        // through its own OnApprovalRequired callback -- a real approval mechanism now exists,
        // so there's no more reason to leave this false.
        BrowserTools.Register(registry, workspaceRoot);
        var caseForgeUrl = Environment.GetEnvironmentVariable("THEORC_CASEFORGE_URL");
        if (Uri.TryCreate(caseForgeUrl, UriKind.Absolute, out var caseForgeUri))
        {
            Uri.TryCreate(Environment.GetEnvironmentVariable("THEORC_CASEFORGE_WORKSPACE_URL"),
                UriKind.Absolute, out var workspaceUri);
            CaseForgeTools.Register(registry, caseForgeUri,
                Environment.GetEnvironmentVariable("THEORC_CASEFORGE_TOKEN"), workspaceUri);
        }

        var tools = new List<ToolDefinition>();
        foreach (var name in TopToolNames.Where(n => n != "web_search" && n != "fetch_page"))
        {
            if (registry.TryGet(name, out var def) && def is not null)
                tools.Add(def);
        }

        var webSearch = new WebSearchTool();
        var fetchPage = new FetchPageTool();
        tools.InsertRange(0, ResearchToolset.GetTools(webSearch, fetchPage));
        tools.Add(BuildSaveMarkdownTool(workspaceRoot));

        return tools;
    }

    public static string BuildReactInstructions(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You can use tools when they help.");
        sb.AppendLine();
        sb.AppendLine("To use a tool, output ONLY this block:");
        sb.AppendLine("<tool_call>");
        sb.AppendLine("<name>TOOL_NAME</name>");
        sb.AppendLine("<args>{\"param\": \"value\"}</args>");
        sb.AppendLine("</tool_call>");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.Name} — {tool.Description}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use web_search or fetch_page for current information.");
        sb.AppendLine("- Use save_markdown_document when the user asks for a markdown document or notes file.");
        sb.AppendLine("- Prefer workspace tools over describing hypothetical edits.");
        sb.AppendLine("- After tool results arrive, continue normally and cite concrete file paths when you created something.");
        return sb.ToString().TrimEnd();
    }

    private static ToolDefinition BuildSaveMarkdownTool(string workspaceRoot) => new()
    {
        Name = "save_markdown_document",
        Description = "Save a markdown document into the current workspace and return its path.",
        Parameters = new()
        {
            ["filename"] = new("string", "File name, with or without the .md extension."),
            ["content"] = new("string", "Full markdown document body to save."),
            ["folder"] = new("string", "Optional workspace-relative folder. Defaults to chat-output."),
        },
        Required = ["filename", "content"],
        Handler = async (args, ct) =>
        {
            var rawName = GetString(args, "filename", "notes.md");
            var folder = GetString(args, "folder", "chat-output");
            var content = GetString(args, "content");

            if (string.IsNullOrWhiteSpace(content))
                return "[ERROR] content is required.";

            var safeName = string.Concat(rawName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            if (!safeName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                safeName += ".md";

            var relativeFolder = string.IsNullOrWhiteSpace(folder) ? "chat-output" : folder.Replace('/', Path.DirectorySeparatorChar);
            var targetDir = Path.GetFullPath(Path.Combine(workspaceRoot, relativeFolder));
            if (!Trust.PathSandbox.IsInsideSandbox(targetDir, workspaceRoot))
                return $"[SANDBOX BLOCKED] save_markdown_document: '{targetDir}' is outside the workspace.";

            Directory.CreateDirectory(targetDir);
            var fullPath = Path.Combine(targetDir, safeName);
            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);

            var fileUri = new Uri(fullPath).AbsoluteUri;
            return $"Saved markdown document to [{safeName}]({fileUri})\n\nPath: `{fullPath}`";
        }
    };

    private static string GetString(Dictionary<string, object?> args, string key, string def = "")
    {
        if (!args.TryGetValue(key, out var value)) return def;
        return value switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? def,
            JsonElement je => je.GetRawText(),
            _ => value?.ToString() ?? def,
        };
    }
}
