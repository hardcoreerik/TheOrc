// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OrchestratorIDE.Services.CodeGraph.Data;

namespace OrchestratorIDE.Services.CodeGraph;

/// <summary>
/// Lifecycle façade for the code knowledge graph.
///
/// Usage pattern (per ConfirmWorkspace):
///   1. <see cref="Attach"/> — bind to the new workspace's GraphRepository
///      (call BEFORE StartIndexing, AFTER the SqliteStore is ready).
///   2. <see cref="StartIndexing"/> — fire-and-forget background index.
///   3. Subscribe to <see cref="OnStatus"/> for activity-log messages.
///      Callbacks always fire off the UI thread — callers must marshal.
///   4. <see cref="Dispose"/> on window close — cancels the in-flight task
///      and clears event subscribers so the window is not kept alive.
///
/// Re-entrancy: each StartIndexing call increments a generation counter.
/// Any in-flight IndexAsync that detects a newer generation exits without
/// writing state, preventing stale updates from clobbering the new run.
/// </summary>
public sealed class CodeGraphService : IDisposable
{
    private GraphRepository? _repo;
    private CancellationTokenSource _cts = new();
    private int  _gen;      // incremented on each StartIndexing; guards stale writes
    private bool _disposed;

    // ── Observable state (written only from winning IndexAsync run) ───────────

    public bool      IsIndexing    { get; private set; }
    public DateTime? LastIndexedAt { get; private set; }
    public int       NodeCount     { get; private set; }
    public int       EdgeCount     { get; private set; }

    /// <summary>
    /// Fired from a background thread with short status strings for the
    /// activity log. Callers must marshal to the UI thread before updating UI.
    /// </summary>
    public event Action<string>? OnStatus;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Bind the service to a workspace's graph repository.
    /// Call this from ConfirmWorkspace after InitDataLayer creates the SqliteStore,
    /// then call StartIndexing. Do NOT call from a bare InitDataLayer invocation
    /// that may run before workspace confirmation (avoids double-attach races).
    /// </summary>
    public void Attach(GraphRepository repo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _repo = repo;
    }

    /// <summary>
    /// Cancel any in-flight index and start a fresh background index.
    /// Safe to call on the UI thread. No-ops when disposed, path is empty,
    /// or Attach has not yet been called.
    /// </summary>
    public void StartIndexing(string workspaceRoot)
    {
        if (_disposed || string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return;
        if (_repo is not { } repo)
            return;

        // Advance generation BEFORE cancelling so a racing prior task sees
        // the new generation immediately on its next ct check.
        var gen = Interlocked.Increment(ref _gen);

        var prev = _cts;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        prev.Cancel();
        prev.Dispose();

        // Set IsIndexing here (on the caller's thread) so it's true before the async
        // work begins. IndexAsync does NOT set it — only clears it on completion.
        // This prevents a superseded generation that slipped past its initial gen-check
        // from re-raising the flag after the winner has already cleared it.
        IsIndexing = true;
        _ = IndexAsync(workspaceRoot, repo, token, gen);
    }

    // ── Private indexing pipeline ─────────────────────────────────────────────

    private async Task IndexAsync(string root, GraphRepository repo,
                                  CancellationToken ct, int gen)
    {
        // Only the winning generation proceeds — a prior run that lost the race
        // exits cleanly without touching shared state.
        if (gen != _gen) return;

        FireStatus("CodeGraph: indexing…");
        try
        {
            // repo is captured as a parameter so superseded generations write to their
            // own (now-abandoned) GraphRepository, never to the winner's. The CT keeps
            // Roslyn work short-lived; the gen check below guards shared mutable state.
            var indexer = new RoslynIndexer(repo);
            await Task.Run(() => indexer.IndexDirectoryAsync(root, ct), ct)
                      .ConfigureAwait(false);

            // After the await we are on a thread-pool thread (ConfigureAwait false).
            // Discard the result if cancelled or superseded before touching shared state.
            if (ct.IsCancellationRequested || gen != _gen) return;

            NodeCount     = repo.CountNodes();
            EdgeCount     = repo.CountEdges();
            LastIndexedAt = DateTime.Now;
            FireStatus($"CodeGraph ready — {NodeCount:N0} nodes · {EdgeCount:N0} edges");
        }
        catch (OperationCanceledException)
        {
            if (gen == _gen) FireStatus("CodeGraph: index cancelled");
        }
        catch (Exception ex)
        {
            // Non-fatal: graph tools degrade to "no results" when the graph is empty.
            if (gen == _gen) FireStatus($"CodeGraph: index failed — {ex.Message}");
        }
        finally
        {
            // Only the current generation resets the flag; a superseded run leaves
            // it alone so the winning run's state is not disturbed.
            if (gen == _gen) IsIndexing = false;
        }
    }

    private void FireStatus(string msg)
    {
        if (_disposed) return;
        OnStatus?.Invoke(msg);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        OnStatus  = null;   // release all subscribers; prevents InvokeAsync to dead dispatcher
        _cts.Cancel();
        _cts.Dispose();
    }
}
