// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using LibGit2Sharp;

namespace OrchestratorIDE.Trust;

/// <summary>
/// Creates automatic git checkpoints before agent runs using LibGit2Sharp.
/// If the workspace isn't a git repo, silently skips (no crash).
/// </summary>
public class GitCheckpoint
{
    /// <summary>
    /// Stages all changes and creates a checkpoint commit.
    /// Returns the commit SHA or null if git is unavailable / nothing to commit.
    /// </summary>
    public Task<string?> CheckpointAsync(string workspaceRoot, string message)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Repository.IsValid(workspaceRoot)) return null;

                using var repo = new Repository(workspaceRoot);

                // Stage all changes
                Commands.Stage(repo, "*");

                var status = repo.RetrieveStatus();
                if (!status.IsDirty) return null;  // nothing to commit

                var sig = new Signature(
                    "OrchestratorIDE", "agent@local",
                    DateTimeOffset.UtcNow);

                var commit = repo.Commit(
                    $"[agent] {message} — {DateTime.UtcNow:HH:mm:ss}",
                    sig, sig);

                return commit.Sha;
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// Resets to a previous checkpoint SHA (hard reset).
    /// Returns true on success.
    /// </summary>
    public Task<bool> RollbackAsync(string workspaceRoot, string sha)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Repository.IsValid(workspaceRoot)) return false;
                using var repo = new Repository(workspaceRoot);
                var commit = repo.Lookup<Commit>(sha)
                    ?? throw new InvalidOperationException($"SHA {sha} not found");
                repo.Reset(ResetMode.Hard, commit);
                return true;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// Returns the current branch name, or null if not a git repo.
    /// </summary>
    public Task<string?> GetBranchAsync(string workspaceRoot)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Repository.IsValid(workspaceRoot)) return null;
                using var repo = new Repository(workspaceRoot);
                return repo.Head?.FriendlyName;
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// Returns recent checkpoint commits (those with "[agent]" prefix).
    /// </summary>
    public Task<List<CheckpointInfo>> GetCheckpointsAsync(string workspaceRoot, int max = 20)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Repository.IsValid(workspaceRoot)) return [];
                using var repo = new Repository(workspaceRoot);
                return repo.Commits
                    .Where(c => c.MessageShort.StartsWith("[agent]"))
                    .Take(max)
                    .Select(c => new CheckpointInfo(c.Sha, c.MessageShort, c.Author.When.LocalDateTime))
                    .ToList();
            }
            catch { return []; }
        });
    }
}

public record CheckpointInfo(string Sha, string Message, DateTime When);
