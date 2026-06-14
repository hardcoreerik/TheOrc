using System.Diagnostics;
using LibGit2Sharp;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Manages per-task isolated work areas for swarm runs.
///
/// Repo mode   — workspace is a git repo: each task gets a real git worktree
///               on its own branch, merged back into a per-run integration branch.
/// Greenfield  — workspace is empty/non-git: each task gets a plain directory;
///               a scratch git repo is initialised at the integration dir so
///               the same attribution/history machinery works.
///
/// Phase 1: primitives only, opt-in via AppSettings.HiveWorktreeIsolation.
/// The existing flat-staging path runs unchanged when isolation is disabled.
/// </summary>
public sealed class WorktreeManager : IDisposable
{
    private readonly string  _runDir;
    private readonly string? _workspaceRoot;
    private readonly string  _treesDir;
    private readonly string  _integrationDir;
    private readonly string  _runId;
    private readonly string  _integrationBranch;
    private bool             _integrationReady;
    // Serialises concurrent Merge calls and the one-time integration setup so concurrent
    // git merge/commit calls never race on the integration worktree or index.lock.
    private readonly object  _lock = new();

    /// <summary>True when workspaceRoot is a valid git repository.</summary>
    public bool RepoMode { get; }

    public WorktreeManager(string runDir, string? workspaceRoot)
    {
        _runDir        = runDir;
        _workspaceRoot = workspaceRoot;
        _runId         = Path.GetFileName(
            runDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _treesDir          = Path.Combine(runDir, "trees");
        _integrationDir    = Path.Combine(runDir, "integration");
        _integrationBranch = $"orc/run-{_runId}/integration";

        RepoMode = !string.IsNullOrEmpty(workspaceRoot) && Repository.IsValid(workspaceRoot);

        Directory.CreateDirectory(_treesDir);
        Directory.CreateDirectory(_integrationDir);
    }

    // ── public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Create an isolated working area for <paramref name="taskId"/>.
    /// Idempotent — calling Acquire twice for the same taskId returns the same handle.
    /// </summary>
    public WorktreeHandle Acquire(string taskId, string role)
    {
        var treePath   = Path.Combine(_treesDir, $"{role}-{taskId}");
        var branchName = RepoMode ? $"orc/run-{_runId}/{role}-{taskId}" : null;

        if (RepoMode)
        {
            EnsureIntegrationWorktree();
            if (!Directory.Exists(treePath))
                RunGit(_workspaceRoot!,
                    $"worktree add \"{treePath}\" -b \"{branchName}\" \"{_integrationBranch}\"");
        }
        else
        {
            Directory.CreateDirectory(treePath);
        }

        return new WorktreeHandle(taskId, role, treePath, branchName);
    }

    /// <summary>
    /// Merge a finished task's output into the run's integration tree.
    ///
    /// The caller supplies <paramref name="declaredFiles"/> — the files the task
    /// was declared to own (from boss-plan parsing). If the task touched a file
    /// outside this set that is owned by a *different* task in <paramref name="ledger"/>,
    /// a <see cref="WorktreeConflictException"/> is thrown before any write occurs.
    ///
    /// When <paramref name="ledger"/> is null (Phase 1 / scheduler not yet wired),
    /// undeclared files are allowed and claimed retroactively — identical to the
    /// existing flat-staging behaviour.
    /// </summary>
    public MergeResult Merge(
        WorktreeHandle              handle,
        IReadOnlyCollection<string> declaredFiles,
        FileOwnershipLedger?        ledger = null)
    {
        lock (_lock)
        {
            var actualFiles = GetActualChangedFiles(handle);

            // Conflict check — only when a ledger is provided (Phase 2+)
            if (ledger != null)
            {
                var ownedByTask = new HashSet<string>(
                    declaredFiles.Select(FileOwnershipLedger.Normalize),
                    StringComparer.Ordinal);

                foreach (var file in actualFiles)
                {
                    var norm = FileOwnershipLedger.Normalize(file);
                    if (ownedByTask.Contains(norm)) continue;

                    var ownerOfFile = ledger.OwnerOf(norm);
                    if (ownerOfFile != null && ownerOfFile != handle.TaskId)
                        throw new WorktreeConflictException(norm, handle.TaskId, ownerOfFile);
                }
            }

            // Apply actual file changes into the integration directory.
            // Repo mode: git merge handles the file application; just track the list.
            // Greenfield: copy additions/modifications, propagate deletions.
            List<string> merged;
            if (RepoMode)
            {
                merged = actualFiles;
            }
            else
            {
                merged = new List<string>(actualFiles.Count);
                foreach (var file in actualFiles)
                {
                    var relPath = file.Replace('/', Path.DirectorySeparatorChar);
                    var src     = Path.Combine(handle.Path, relPath);
                    var dst     = Path.Combine(_integrationDir, relPath);

                    if (File.Exists(src))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst, overwrite: true);
                    }
                    else if (File.Exists(dst))
                    {
                        // File was deleted in the task — propagate to integration.
                        File.Delete(dst);
                    }
                    merged.Add(file); // count additions, modifications, and deletions
                }
            }

            if (merged.Count > 0)
            {
                if (RepoMode)
                    CommitRepoMode(handle);
                else
                    CommitGreenfieldMode(handle, merged);
            }

            return new MergeResult(merged.Count, merged);
        }
    }

    /// <summary>
    /// Remove a task's worktree. Failed tasks keep theirs for inspection
    /// unless <paramref name="force"/> is true.
    /// </summary>
    public void Release(WorktreeHandle handle, bool force = false)
    {
        if (RepoMode)
        {
            try
            {
                RunGit(_workspaceRoot!,
                    $"worktree remove \"{handle.Path}\"" + (force ? " --force" : ""));
            }
            catch
            {
                if (force && Directory.Exists(handle.Path))
                    Directory.Delete(handle.Path, recursive: true);
            }
        }
        else
        {
            // Greenfield: task directories are temporary scratch space; outputs live
            // in the integration dir after Merge. Clean up unconditionally — callers
            // that want to preserve a failed task dir should simply not call Release().
            if (Directory.Exists(handle.Path))
                Directory.Delete(handle.Path, recursive: true);
        }
    }

    /// <summary>
    /// Remove worktrees whose last-write time is older than <paramref name="maxAge"/>.
    /// Mirrors HiveTaskQueue's terminal-entry eviction for abandoned runs.
    /// Returns the count of trees removed.
    /// </summary>
    public int ReapStale(TimeSpan maxAge)
    {
        if (!Directory.Exists(_treesDir)) return 0;

        var cutoff = DateTime.UtcNow - maxAge;
        var reaped = 0;
        foreach (var dir in Directory.GetDirectories(_treesDir))
        {
            if (new DirectoryInfo(dir).LastWriteTimeUtc >= cutoff) continue;
            try
            {
                if (RepoMode)
                    try { RunGit(_workspaceRoot!, $"worktree remove \"{dir}\" --force"); }
                    catch { /* tree may not be registered (manually created) */ }

                Directory.Delete(dir, recursive: true);
                reaped++;
            }
            catch { /* best-effort */ }
        }
        return reaped;
    }

    public void Dispose() { }

    // ── private helpers ──────────────────────────────────────────────────────

    private void EnsureIntegrationWorktree()
    {
        lock (_lock)
        {
            if (_integrationReady) return;

            // Branch from current HEAD — idempotent, branch may already exist on resume
            try { RunGit(_workspaceRoot!, $"branch \"{_integrationBranch}\""); }
            catch { }

            // Add integration worktree if not already registered
            if (!Repository.IsValid(_integrationDir))
                RunGit(_workspaceRoot!,
                    $"worktree add \"{_integrationDir}\" \"{_integrationBranch}\"");

            _integrationReady = true;
        }
    }

    private List<string> GetActualChangedFiles(WorktreeHandle handle)
    {
        if (RepoMode)
        {
            // git status --porcelain: "XY path" — path starts at column 3
            var raw = RunGitCapture(handle.Path, "status --porcelain");
            var files = new List<string>();
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length <= 3) continue;
                var path = line[3..].Trim();
                if (!string.IsNullOrEmpty(path))
                    files.Add(path.Replace('\\', '/'));
            }
            return files;
        }

        // Greenfield: every file the worker wrote into the task directory
        if (!Directory.Exists(handle.Path)) return [];
        return Directory.GetFiles(handle.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(handle.Path, f).Replace('\\', '/'))
            .ToList();
    }

    private void CommitRepoMode(WorktreeHandle handle)
    {
        // Stage + commit in the task branch, then merge into integration
        try
        {
            RunGit(handle.Path, "add -A");
            RunGit(handle.Path, $"commit -m \"task:{handle.Role}/{handle.TaskId}\"");
        }
        catch { /* nothing to commit — no-op */ }

        try
        {
            RunGit(_integrationDir,
                $"merge --no-ff \"{handle.Branch}\" -m \"merge:{handle.Role}/{handle.TaskId}\"");
        }
        catch (Exception innerEx)
        {
            throw new WorktreeConflictException(
                "<integration-merge>", handle.TaskId, "<integration>",
                innerEx);
        }
    }

    private void CommitGreenfieldMode(WorktreeHandle handle, IReadOnlyList<string> files)
    {
        try
        {
            if (!Repository.IsValid(_integrationDir))
                Repository.Init(_integrationDir);

            using var repo = new Repository(_integrationDir);
            foreach (var f in files)
                Commands.Stage(repo, f.Replace('/', Path.DirectorySeparatorChar));

            var sig = new Signature(
                $"orc/{handle.Role}", "agent@local", DateTimeOffset.UtcNow);
            repo.Commit($"task:{handle.Role}/{handle.TaskId}", sig, sig);
        }
        catch (EmptyCommitException) { }
        catch { /* non-fatal for Phase 1 */ }
    }

    // ── git process helpers ──────────────────────────────────────────────────

    private static void RunGit(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName               = "git",
            Arguments              = args,
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        }) ?? throw new InvalidOperationException($"git not found (git {args})");

        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {args} exited {p.ExitCode}: {err.Trim()}");
    }

    private static string RunGitCapture(string dir, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName               = "git",
            Arguments              = args,
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        }) ?? throw new InvalidOperationException($"git not found (git {args})");

        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

// ── value types ─────────────────────────────────────────────────────────────

public sealed record WorktreeHandle(string TaskId, string Role, string Path, string? Branch);

public sealed record MergeResult(int FilesMerged, IReadOnlyList<string> Files);

public sealed class WorktreeConflictException : Exception
{
    public string ConflictPath { get; }
    public string Claimant    { get; }
    public string Owner       { get; }

    public WorktreeConflictException(string path, string claimant, string owner)
        : base($"Worktree conflict on '{path}': owned by '{owner}', touched by '{claimant}'")
    {
        ConflictPath = path;
        Claimant     = claimant;
        Owner        = owner;
    }

    public WorktreeConflictException(string path, string claimant, string owner, Exception innerException)
        : base($"Worktree conflict on '{path}': owned by '{owner}', touched by '{claimant}'", innerException)
    {
        ConflictPath = path;
        Claimant     = claimant;
        Owner        = owner;
    }
}
