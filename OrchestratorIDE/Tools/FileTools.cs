using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class FileTools
{
    private static readonly string[] _skipDirs =
        ["node_modules", ".venv", "venv", "__pycache__", ".git",
         "dist", "build", ".next", "target", "bin", "obj"];

    public static void Register(ToolRegistry registry, string workspaceRoot,
        Action<string, string, string>? onDiffPreview = null)
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
                if (!File.Exists(path)) return $"[ERROR] File not found: {path}";
                var content = await File.ReadAllTextAsync(path, ct);
                var lines = content.Split('\n');
                // Return with line numbers (like cat -n)
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
            RequiresApproval = true,   // always goes through approval + diff
            Handler = async (args, ct) =>
            {
                var path    = Resolve(workspaceRoot, args);
                var content = args.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                var reason  = args.TryGetValue("reason",  out var r) ? r?.ToString() ?? "" : "";

                // Build diff for the approval UI
                var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
                var diff = BuildInlineDiff(existing, content);
                onDiffPreview?.Invoke(path, diff, reason);

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
            Handler = (args, ct) =>
            {
                var dir   = args.TryGetValue("path", out var p) && p != null
                    ? Resolve(workspaceRoot, new() { ["path"] = p }) : workspaceRoot;
                var depth = args.TryGetValue("depth", out var d) ? int.Parse(d?.ToString() ?? "3") : 3;
                var lines = new List<string>();
                WalkDir(dir, workspaceRoot, "", depth, lines);
                return Task.FromResult(string.Join('\n', lines));
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
