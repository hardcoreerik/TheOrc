// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Trust;

// ── Risk levels ───────────────────────────────────────────────────────────────

/// <summary>
/// Ordered from safest to most dangerous. Higher values require more scrutiny.
/// </summary>
public enum ToolRiskLevel
{
    /// <summary>Read-only access inside the workspace.</summary>
    ReadWorkspace        = 0,
    /// <summary>Write inside the workspace (diff review required).</summary>
    WriteWorkspace       = 10,
    /// <summary>Shell command that builds, tests, or lints (e.g. dotnet build, npm test).</summary>
    RunBuildCommand      = 20,
    /// <summary>Shell command that installs packages or modifies global state.</summary>
    RunPackageInstall    = 30,
    /// <summary>Shell command that touches the network (curl, wget, etc.).</summary>
    RunNetworkCommand    = 30,
    /// <summary>Git command other than read (commit, push, reset, checkout).</summary>
    RunGitCommand        = 30,
    /// <summary>Read outside the workspace sandbox.</summary>
    OutOfWorkspaceRead   = 40,
    /// <summary>Write outside the workspace sandbox.</summary>
    OutOfWorkspaceWrite  = 50,
    /// <summary>Command classified as destructive or irreversible.</summary>
    DestructiveShell     = 100,
}

// ── Assessment record ─────────────────────────────────────────────────────────

/// <summary>
/// Result of evaluating a tool call against the policy engine.
/// </summary>
/// <param name="Risk">The highest risk level that matched.</param>
/// <param name="IsDestructive">True if any destructive pattern matched.</param>
/// <param name="TouchesOutsideWorkspace">True if the path or cwd is outside the workspace.</param>
/// <param name="NetworkAccess">True if the command appears to access the network.</param>
/// <param name="BlockReason">Non-null → the call must be hard-blocked with this message.</param>
public record ToolRiskAssessment(
    ToolRiskLevel Risk,
    bool          IsDestructive,
    bool          TouchesOutsideWorkspace,
    bool          NetworkAccess,
    string?       BlockReason);

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// Stateless policy evaluator. Replaces ad-hoc string-contains checks with
/// a structured risk taxonomy that can be tested in isolation.
/// </summary>
public static class ToolPolicyEngine
{
    // ── Destructive command patterns (hard-block) ─────────────────────────
    private static readonly string[] _hardBlock =
    [
        "rm -rf", "rm -r", "rmdir /s", "rmdir /q",
        "del /f",  "del /q", "del /s",
        "format ", "format/",
        "shutdown", "restart-computer",
        "reboot",
        "reg delete", "reg add",
        ":(){:|:&};:",           // fork bomb
        "dd if=", "dd of=",
        "> /dev/",
        "mkfs",
    ];

    // ── Destructive git operations ─────────────────────────────────────────
    private static readonly string[] _destructiveGit =
    [
        "git reset --hard",
        "git checkout --",
        "git clean -f",
        "git clean -fd",
        "git clean -fx",
        "git push --force",
        "git push -f",
        "git push --delete",
        "git branch -D",
        "git branch -d",
    ];

    // ── Process/service killers ────────────────────────────────────────────
    private static readonly string[] _processKillers =
    [
        "taskkill",
        "stop-process",
        "kill -9",
        "killall",
        "pkill",
        "kill -sigkill",
    ];

    // ── Package installers ─────────────────────────────────────────────────
    private static readonly string[] _packageInstallers =
    [
        "pip install", "pip3 install",
        "npm install", "npm i ",
        "yarn add",
        "choco install",
        "winget install",
        "apt install", "apt-get install",
        "dotnet tool install",
    ];

    // ── Network-touching commands ──────────────────────────────────────────
    private static readonly string[] _networkCommands =
    [
        "curl ", "wget ",
        "invoke-webrequest", "invoke-restmethod",
        "iwr ", "irm ",
        "ftp ", "sftp ",
        "ssh ", "scp ",
        "nc ", "netcat",
        "nmap",
        "telnet",
    ];

    // ── Build / test runners (lower risk) ─────────────────────────────────
    private static readonly string[] _buildCommands =
    [
        "dotnet ", "msbuild",
        "npm run", "npm test", "npm build",
        "yarn run", "yarn test", "yarn build",
        "cargo ", "go build", "go test",
        "python ", "python3 ",
        "pytest", "jest",
        "cmake", "make",
        "idf.py",
    ];

    // ── Read-only git ──────────────────────────────────────────────────────
    private static readonly string[] _gitReadOnly =
    [
        "git status", "git log", "git diff",
        "git show",   "git fetch",
        "git remote", "git branch",
    ];

    // ── Core evaluate ─────────────────────────────────────────────────────

    /// <summary>
    /// Evaluate a tool call and return a risk assessment.
    /// </summary>
    /// <param name="toolName">The registered tool name (e.g. "run_shell", "write_file").</param>
    /// <param name="args">The tool call arguments dictionary.</param>
    /// <param name="workspaceRoot">The active workspace root for sandbox checks.</param>
    public static ToolRiskAssessment Evaluate(
        string                        toolName,
        Dictionary<string, object?>   args,
        string                        workspaceRoot)
    {
        return toolName switch
        {
            "read_file"   => EvaluateRead(args, workspaceRoot),
            "list_files"  => EvaluateRead(args, workspaceRoot),
            "write_file"  => EvaluateWrite(args, workspaceRoot),
            "run_shell"   => EvaluateShell(args, workspaceRoot),
            _             => new ToolRiskAssessment(ToolRiskLevel.ReadWorkspace,
                                 false, false, false, null),
        };
    }

    // ── Read ──────────────────────────────────────────────────────────────

    private static ToolRiskAssessment EvaluateRead(
        Dictionary<string, object?> args, string workspaceRoot)
    {
        var path    = GetString(args, "path");
        var outside = IsOutside(path, workspaceRoot);
        return new ToolRiskAssessment(
            Risk:                   outside ? ToolRiskLevel.OutOfWorkspaceRead : ToolRiskLevel.ReadWorkspace,
            IsDestructive:          false,
            TouchesOutsideWorkspace: outside,
            NetworkAccess:          false,
            BlockReason:            null);
    }

    // ── Write ─────────────────────────────────────────────────────────────

    private static ToolRiskAssessment EvaluateWrite(
        Dictionary<string, object?> args, string workspaceRoot)
    {
        var path    = GetString(args, "path");
        var outside = IsOutside(path, workspaceRoot);
        return new ToolRiskAssessment(
            Risk:                   outside ? ToolRiskLevel.OutOfWorkspaceWrite : ToolRiskLevel.WriteWorkspace,
            IsDestructive:          outside,
            TouchesOutsideWorkspace: outside,
            NetworkAccess:          false,
            BlockReason:            outside
                ? $"[POLICY] write_file: path '{path}' is outside the workspace sandbox."
                : null);
    }

    // ── Shell ─────────────────────────────────────────────────────────────

    private static ToolRiskAssessment EvaluateShell(
        Dictionary<string, object?> args, string workspaceRoot)
    {
        var cmd      = (GetString(args, "command") + " " + GetString(args, "env_setup")).ToLowerInvariant();
        var cwd      = GetString(args, "cwd");
        var outside  = IsOutside(cwd, workspaceRoot);

        // Hard block — must not run
        foreach (var pattern in _hardBlock)
            if (cmd.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new ToolRiskAssessment(ToolRiskLevel.DestructiveShell, true, outside, false,
                    $"[POLICY BLOCKED] Destructive command pattern '{pattern}' is not allowed.");

        foreach (var pattern in _processKillers)
            if (cmd.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new ToolRiskAssessment(ToolRiskLevel.DestructiveShell, true, outside, false,
                    $"[POLICY BLOCKED] Process-kill command '{pattern}' is not allowed.");

        // Destructive git — require explicit approval
        foreach (var pattern in _destructiveGit)
            if (cmd.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new ToolRiskAssessment(ToolRiskLevel.DestructiveShell, true, outside, false,
                    $"[POLICY BLOCKED] Destructive git command '{pattern}' must be run manually.");

        // Network
        bool network = _networkCommands.Any(p => cmd.Contains(p, StringComparison.OrdinalIgnoreCase));

        // Package install
        if (_packageInstallers.Any(p => cmd.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return new ToolRiskAssessment(ToolRiskLevel.RunPackageInstall, false, outside, network, null);

        // Network command
        if (network)
            return new ToolRiskAssessment(ToolRiskLevel.RunNetworkCommand, false, outside, true, null);

        // Git (non-destructive)
        if (cmd.TrimStart().StartsWith("git "))
        {
            bool gitRead = _gitReadOnly.Any(p => cmd.Contains(p, StringComparison.OrdinalIgnoreCase));
            if (!gitRead)
                return new ToolRiskAssessment(ToolRiskLevel.RunGitCommand, false, outside, false, null);
        }

        // Build / test
        if (_buildCommands.Any(p => cmd.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return new ToolRiskAssessment(ToolRiskLevel.RunBuildCommand, false, outside, false, null);

        // Generic shell — treat as build level (needs approval)
        return new ToolRiskAssessment(ToolRiskLevel.RunBuildCommand, false, outside, false, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string GetString(Dictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static bool IsOutside(string path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspaceRoot))
            return false;
        try
        {
            var resolved = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(workspaceRoot, path));
            return !PathSandbox.IsInsideSandbox(resolved, workspaceRoot);
        }
        catch { return false; }
    }
}
