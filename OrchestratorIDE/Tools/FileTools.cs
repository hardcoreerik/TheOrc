// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Tools;

public static class FileTools
{
    private static readonly string[] _skipDirs =
        ["node_modules", ".venv", "venv", "__pycache__", ".git",
         "dist", "build", ".next", "target", "bin", "obj"];

    /// <param name="onDiffPreview">
    ///   Async gate called before writing. Receives (path, oldContent, newContent, reason, ct)
    ///   and returns <c>true</c> to allow the write or <c>false</c> to reject it.
    ///   When null the write proceeds immediately (used by swarm workers in auto-approve mode).
    /// </param>
    /// <param name="onSandboxBypass">
    ///   Called when a resolved path is outside the workspace sandbox.
    ///   Signature: <c>(toolName, escapedPath, sandboxRoot, ct) → Task&lt;bool&gt;</c>.
    ///   Return <c>true</c> to allow the operation, <c>false</c> to block it.
    ///   When null, all out-of-sandbox accesses are silently blocked.
    /// </param>
    public static void Register(ToolRegistry registry, string workspaceRoot,
        Func<string, string, string, string, CancellationToken, Task<bool>>? onDiffPreview = null,
        Func<string, string, string, CancellationToken, Task<bool>>? onSandboxBypass = null)
    {
        // ── read_file ──────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "read_file",
            Description = "Read the contents of a file.",
            Parameters = new()
            {
                ["path"] = new("string", "File path relative to workspace root, or absolute."),
            },
            Required = ["path"],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var path = Resolve(workspaceRoot, args);

                // ── Sandbox guard ─────────────────────────────────────────
                if (!PathSandbox.IsInsideSandbox(path, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("read_file", path, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] read_file: '{path}' is outside the workspace " +
                               $"'{workspaceRoot}'. Use a path inside the workspace.";
                }

                if (!File.Exists(path)) return $"[ERROR] File not found: {path}";
                var content = await File.ReadAllTextAsync(path, ct);
                var lines = content.Split('\n');
                return string.Join('\n', lines.Select((l, i) => $"{i + 1,4}\t{l}"));
            }
        });

        // ── write_file ─────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "write_file",
            Description = "Write content to a file. Shows a diff preview before writing.",
            Parameters = new()
            {
                ["path"]    = new("string", "File path relative to workspace root."),
                ["content"] = new("string", "Complete new file content."),
                ["reason"]  = new("string", "Why this change is being made."),
            },
            Required = ["path", "content"],
            // RequiresApproval = false here — the onDiffPreview callback IS the gate.
            // When onDiffPreview is null (swarm auto-mode) no gate is shown and writes proceed.
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var path    = Resolve(workspaceRoot, args);
                var content = args.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                var reason  = args.TryGetValue("reason",  out var r) ? r?.ToString() ?? "" : "";

                // ── Sandbox guard ─────────────────────────────────────────
                if (!PathSandbox.IsInsideSandbox(path, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("write_file", path, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] write_file: '{path}' is outside the workspace " +
                               $"'{workspaceRoot}'. Write to a path inside the workspace instead.";
                }

                // ── Diff approval gate ────────────────────────────────────
                // Pass old and new content to the callback so the UI can render the diff
                // itself (e.g. using DiffViewer). The callback returns true to allow the
                // write or false to reject it. When null (swarm auto-approve mode) the
                // write proceeds unconditionally.
                var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";

                if (onDiffPreview is not null)
                {
                    var approved = await onDiffPreview(path, existing, content, reason, ct);
                    if (!approved)
                        return $"[REJECTED] User rejected the write to '{Path.GetRelativePath(workspaceRoot, path)}'.";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, ct);
                var lines = content.Split('\n').Length;
                return $"[OK] Wrote {lines} lines to {Path.GetRelativePath(workspaceRoot, path)}";
            }
        });

        // ── list_files ─────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "list_files",
            Description = "List files in a directory (recursive, respects .gitignore-style skips).",
            Parameters = new()
            {
                ["path"]  = new("string", "Directory path. Defaults to workspace root."),
                ["depth"] = new("integer", "Max recursion depth (default 3)."),
            },
            Required = [],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var dir = args.TryGetValue("path", out var p) && p != null
                    ? Resolve(workspaceRoot, new() { ["path"] = p }) : workspaceRoot;
                var depth = args.TryGetValue("depth", out var d) ? int.Parse(d?.ToString() ?? "3") : 3;

                // ── Sandbox guard ─────────────────────────────────────────
                if (!PathSandbox.IsInsideSandbox(dir, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("list_files", dir, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] list_files: '{dir}' is outside the workspace " +
                               $"'{workspaceRoot}'. List a directory inside the workspace instead.";
                }

                var lines = new List<string>();
                WalkDir(dir, workspaceRoot, "", depth, lines);
                var result = string.Join('\n', lines);
                return string.IsNullOrWhiteSpace(result)
                    ? $"(empty directory — no files found in {Path.GetRelativePath(workspaceRoot, dir)})"
                    : result;
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Resolve(string root, Dictionary<string, object?> args)
    {
        var raw = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
        return Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(root, raw));
    }

    private static void WalkDir(string dir, string root, string indent, int depth, List<string> out_)
    {
        if (depth < 0) return;
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(dir).OrderBy(e => e))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') && name != ".agent.md") continue;
                if (Directory.Exists(entry))
                {
                    if (_skipDirs.Contains(name)) continue;
                    out_.Add($"{indent}{name}/");
                    WalkDir(entry, root, indent + "  ", depth - 1, out_);
                }
                else
                {
                    var size = new FileInfo(entry).Length;
                    out_.Add($"{indent}{name} ({size:N0} B)");
                }
            }
        }
        catch { /* permission denied — skip */ }
    }

    private static string BuildInlineDiff(string oldText, string newText)
    {
        var diff = InlineDiffBuilder.Diff(oldText, newText);
        var sb = new System.Text.StringBuilder();
        foreach (var line in diff.Lines)
        {
            var prefix = line.Type switch
            {
                ChangeType.Inserted => "+ ",
                ChangeType.Deleted  => "- ",
                _                   => "  "
            };
            sb.AppendLine(prefix + line.Text);
        }
        return sb.ToString();
    }
}
