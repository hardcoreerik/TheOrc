// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.Hive;

public static class NativeWorkerToolProfile
{
    private const int MaxTextBytes = 1024 * 1024;

    public static IReadOnlyList<HeadlessTool> Create(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        return
        [
            Tool("read_file", "Read a UTF-8 text file inside the isolated work area.",
                new { path = new { type = "string" } }, ["path"], async (args, ct) =>
                {
                    var path = Resolve(root, StringArg(args, "path"));
                    if (!File.Exists(path)) return "[ERROR] File not found.";
                    var info = new FileInfo(path);
                    if (info.Length > MaxTextBytes) return "[POLICY BLOCKED] File exceeds the 1 MB text limit.";
                    return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                }),
            Tool("list_files", "List files inside the isolated work area.",
                new { path = new { type = "string" } }, [], (args, _) =>
                {
                    var path = Resolve(root, StringArg(args, "path", "."));
                    if (!Directory.Exists(path)) return Task.FromResult("[ERROR] Directory not found.");
                    var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Take(2000).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'));
                    return Task.FromResult(string.Join('\n', files));
                }),
            Tool("grep_code", "Regex-search UTF-8 source files in the isolated work area.",
                new { pattern = new { type = "string" } }, ["pattern"], async (args, ct) =>
                {
                    var regex = new Regex(StringArg(args, "pattern"), RegexOptions.IgnoreCase,
                        TimeSpan.FromSeconds(2));
                    var matches = new List<string>();
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (new FileInfo(path).Length > MaxTextBytes) continue;
                        string[] lines;
                        try { lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false); }
                        catch { continue; }
                        for (var i = 0; i < lines.Length && matches.Count < 200; i++)
                            if (regex.IsMatch(lines[i]))
                                matches.Add($"{Path.GetRelativePath(root, path)}:{i + 1}:{lines[i]}");
                        if (matches.Count >= 200) break;
                    }
                    return string.Join('\n', matches);
                }),
            Tool("write_file", "Write a UTF-8 text file inside the isolated work area.",
                new { path = new { type = "string" }, content = new { type = "string" } },
                ["path", "content"], async (args, ct) =>
                {
                    var path = Resolve(root, StringArg(args, "path"));
                    var content = StringArg(args, "content");
                    if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxTextBytes)
                        return "[POLICY BLOCKED] A single write may not exceed 1 MB.";
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
                    return $"[OK] Wrote {Path.GetRelativePath(root, path)}";
                }),
            Tool("run_tests", "Run dotnet tests without restore in the isolated work area.",
                new { }, [], async (_, ct) =>
                {
                    var target = Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
                    if (target is null) return "[POLICY BLOCKED] No supported test project was found.";
                    return await RunBoundedAsync("dotnet", ["test", target, "--no-restore", "--nologo"], root, ct)
                        .ConfigureAwait(false);
                }),
        ];
    }

    private static HeadlessTool Tool(string name, string description, object properties,
        IReadOnlyList<string> required,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> execute) =>
        new(name, HeadlessAgentLoop.BuildToolSchema(name, description,
            properties.GetType().GetProperties().ToDictionary(p => p.Name, p => (object)p.GetValue(properties)!), required), execute);

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) relative = ".";
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path != root && !path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the isolated work area.");
        return path;
    }

    private static string StringArg(IReadOnlyDictionary<string, object?> args, string name, string fallback = "") =>
        args.TryGetValue(name, out var value) ? value?.ToString() ?? fallback : fallback;

    private static async Task<string> RunBoundedAsync(string executable, IReadOnlyList<string> args,
        string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start test runner.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (text.Length > 64_000) text = text[..64_000] + "\n[truncated]";
        return $"[exit {process.ExitCode}]\n{text}";
    }
}
