using System.Diagnostics;
using System.IO;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class SearchTools
{
    public static void Register(ToolRegistry registry, string workspaceRoot)
    {
        // ── grep_code ──────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "grep_code",
            Description = "Search code for a pattern. Uses ripgrep if available, falls back to built-in.",
            Parameters = new()
            {
                ["pattern"] = new("string", "Regex pattern to search for."),
                ["glob"]    = new("string", "File glob filter (e.g. '*.cs', '*.py'). Optional."),
                ["path"]    = new("string", "Directory to search. Defaults to workspace root."),
            },
            Required = ["pattern"],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var pattern = args.TryGetValue("pattern", out var p) ? p?.ToString() ?? "" : "";
                var glob    = args.TryGetValue("glob",    out var g) ? g?.ToString() : null;
                var path    = args.TryGetValue("path",    out var d) ? d?.ToString() ?? workspaceRoot : workspaceRoot;
                if (!Path.IsPathRooted(path)) path = Path.Combine(workspaceRoot, path);

                // Try ripgrep first
                var rg = FindRg();
                if (rg != null) return await RgSearch(rg, pattern, path, glob, ct);

                // Fallback: built-in recursive search
                return await FallbackSearch(pattern, path, glob, ct);
            }
        });

        // ── get_outline ────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "get_outline",
            Description = "Get a structural outline of a file (classes, methods, functions).",
            Parameters = new()
            {
                ["path"] = new("string", "File path to outline (supports .cs, .py, .js, .ts)."),
            },
            Required = ["path"],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var raw  = args.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
                var path = Path.IsPathRooted(raw) ? raw : Path.Combine(workspaceRoot, raw);
                if (!File.Exists(path)) return Task.FromResult($"[ERROR] File not found: {path}");
                return Task.FromResult(GetOutline(path));
            }
        });
    }

    // ── ripgrep ───────────────────────────────────────────────────────────────

    private static string? FindRg()
    {
        foreach (var candidate in new[] { "rg", "rg.exe" })
        {
            var path = ShellTools.RunAsync($"(Get-Command {candidate} -ErrorAction SilentlyContinue).Source",
                Environment.CurrentDirectory, CancellationToken.None).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(path) && !path.Contains("[ERROR]"))
                return candidate;
        }
        return null;
    }

    private static async Task<string> RgSearch(string rg, string pattern, string path, string? glob, CancellationToken ct)
    {
        var args = $"-n --color never --max-count 3 {(glob != null ? $"-g \"{glob}\"" : "")} \"{pattern}\" \"{path}\"";
        var psi  = new ProcessStartInfo(rg, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var out_ = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var lines = out_.Trim().Split('\n').Take(80).ToArray();
        return lines.Length == 0 ? "[No matches]" : string.Join('\n', lines);
    }

    private static async Task<string> FallbackSearch(string pattern, string dir, string? glob, CancellationToken ct)
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var results = new List<string>();
        var searchPattern = glob?.Replace("*.", "*.") ?? "*.*";

        foreach (var file in Directory.EnumerateFiles(dir, searchPattern, SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;
            if (results.Count >= 80) break;
            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct);
                var rel   = Path.GetRelativePath(dir, file);
                foreach (var (line, idx) in lines.Select((l, i) => (l, i + 1)))
                {
                    if (regex.IsMatch(line))
                        results.Add($"{rel}:{idx}: {line.Trim()}");
                    if (results.Count >= 80) break;
                }
            }
            catch { /* skip unreadable */ }
        }
        return results.Count == 0 ? "[No matches]" : string.Join('\n', results);
    }

    // ── outline ───────────────────────────────────────────────────────────────

    private static string GetOutline(string path)
    {
        var ext   = Path.GetExtension(path).ToLower();
        var lines = File.ReadAllLines(path);
        var out_  = new List<string> { $"[Outline: {Path.GetFileName(path)}]" };

        // Simple regex-based outline for common languages
        System.Text.RegularExpressions.Regex[] patterns = ext switch
        {
            ".cs" =>
            [
                new(@"^\s*(public|private|protected|internal|static).*\s(class|interface|record|enum)\s+(\w+)"),
                new(@"^\s*(public|private|protected|internal|static|override|async).*\s(\w+)\s*\("),
            ],
            ".py" =>
            [
                new(@"^(class\s+\w+)"),
                new(@"^(\s{0,4}def\s+\w+)"),
                new(@"^(\s{0,4}async\s+def\s+\w+)"),
            ],
            ".ts" or ".js" =>
            [
                new(@"^(export\s+(default\s+)?(class|function|const)\s+\w+)"),
                new(@"^\s*(async\s+)?(function\s+\w+|\w+\s*[=:]\s*(async\s+)?\()"),
            ],
            _ => []
        };

        foreach (var (line, idx) in lines.Select((l, i) => (l, i + 1)))
        {
            foreach (var rx in patterns)
            {
                if (rx.IsMatch(line))
                {
                    out_.Add($"  {idx,4}: {line.Trim()}");
                    break;
                }
            }
        }

        return string.Join('\n', out_);
    }
}
