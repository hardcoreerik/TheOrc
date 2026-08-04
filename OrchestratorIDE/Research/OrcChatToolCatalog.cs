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
        var caseForgeToken = Environment.GetEnvironmentVariable("THEORC_CASEFORGE_TOKEN");
        // Token is optional (hardcoreerik, 2026-08-01) -- his own local CaseForge/ComfyUI/Art
        // Forge/KeyHound instances run unauthenticated. Registration is gated on a valid URL
        // alone; CaseForgeTools.Register itself still enforces the loopback/private-IP/
        // single-label host restriction, so an unauthenticated call can only ever reach a local
        // service, never a public one.
        if (Uri.TryCreate(caseForgeUrl, UriKind.Absolute, out var caseForgeUri))
        {
            var workspaceSetting = Environment.GetEnvironmentVariable("THEORC_CASEFORGE_WORKSPACE_URL");
            try
            {
                Uri? workspaceUri = null;
                if (!string.IsNullOrWhiteSpace(workspaceSetting)
                    && !Uri.TryCreate(workspaceSetting, UriKind.Absolute, out workspaceUri))
                    throw new ArgumentException("THEORC_CASEFORGE_WORKSPACE_URL must be an absolute URL.");
                CaseForgeTools.Register(registry, caseForgeUri, caseForgeToken, workspaceUri);
            }
            catch (ArgumentException)
            {
                // Invalid optional integration settings must not prevent TheOrc from starting.
            }
        }

        RegisterArtForge(registry);
        RegisterKeyHound(registry);

        var tools = new List<ToolDefinition>();
        foreach (var name in TopToolNames.Concat(new[]
                 {
                     "model3d_create", "model3d_status", "model3d_cancel", "model3d_open",
                     "image_create", "image_status", "image_gallery", "image_open",
                     "atlas_start", "atlas_graph", "atlas_expand", "atlas_evidence", "atlas_open",
                 }).Where(n => n != "web_search" && n != "fetch_page"))
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

    private static void RegisterArtForge(ToolRegistry registry)
    {
        var url = Environment.GetEnvironmentVariable("THEORC_ARTFORGE_URL");
        // Token optional -- see the CaseForge block above for why.
        var token = Environment.GetEnvironmentVariable("THEORC_ARTFORGE_TOKEN");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var serviceUri)) return;

        var workspaceSetting = Environment.GetEnvironmentVariable("THEORC_ARTFORGE_WORKSPACE_URL");
        try
        {
            Uri? workspaceUri = null;
            if (!string.IsNullOrWhiteSpace(workspaceSetting)
                && !Uri.TryCreate(workspaceSetting, UriKind.Absolute, out workspaceUri))
                throw new ArgumentException("THEORC_ARTFORGE_WORKSPACE_URL must be an absolute URL.");
            ArtForgeTools.Register(registry, serviceUri, token, workspaceUri);
        }
        catch (ArgumentException)
        {
            // Invalid optional integration settings must not prevent TheOrc from starting.
        }
    }

    private static void RegisterKeyHound(ToolRegistry registry)
    {
        var url = Environment.GetEnvironmentVariable("THEORC_KEYHOUND_URL");
        // Token optional -- see the CaseForge block above for why.
        var token = Environment.GetEnvironmentVariable("THEORC_KEYHOUND_TOKEN");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var serviceUri)) return;

        var workspaceSetting = Environment.GetEnvironmentVariable("THEORC_KEYHOUND_WORKSPACE_URL");
        try
        {
            Uri? workspaceUri = null;
            if (!string.IsNullOrWhiteSpace(workspaceSetting)
                && !Uri.TryCreate(workspaceSetting, UriKind.Absolute, out workspaceUri))
                throw new ArgumentException("THEORC_KEYHOUND_WORKSPACE_URL must be an absolute URL.");
            KeyHoundAtlasTools.Register(registry, serviceUri, token, workspaceUri);
        }
        catch (ArgumentException)
        {
            // Invalid optional integration settings must not prevent TheOrc from starting.
        }
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
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Contains("model3d_create"))
            sb.AppendLine("- 3D workflow: call model3d_create, then poll model3d_status; open the workspace only when the user asks.");
        if (names.Contains("image_create"))
            sb.AppendLine("- Image workflow: call image_create, then poll image_status; use image_gallery to find recent outputs.");
        if (names.Contains("atlas_start"))
            sb.AppendLine("- Atlas workflow: call atlas_start, inspect atlas_graph, and use atlas_evidence before presenting a claim as supported.");
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
