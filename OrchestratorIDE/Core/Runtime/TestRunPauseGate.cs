// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Cooperative pause point for long test runs. The runner awaits
/// <see cref="WaitWhilePausedAsync"/> at safe boundaries (between bench cases); the UI toggles
/// <see cref="Pause"/>/<see cref="Resume"/>. Pausing never interrupts an in-flight sample —
/// it holds the run at the next boundary, which is the only semantics that don't corrupt
/// per-sample timing. Cancellation always wins over pause so "Cancel" works while paused.
/// Thread-safe.
/// </summary>
public sealed class TestRunPauseGate
{
    private readonly object _lock = new();
    private TaskCompletionSource? _resumeTcs;

    public bool IsPaused
    {
        get { lock (_lock) return _resumeTcs is not null; }
    }

    public void Pause()
    {
        lock (_lock)
            _resumeTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? tcs;
        lock (_lock)
        {
            tcs = _resumeTcs;
            _resumeTcs = null;
        }
        tcs?.TrySetResult();
    }

    /// <summary>Returns immediately when not paused; otherwise waits for Resume or cancellation.</summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task waitTask;
            lock (_lock)
            {
                if (_resumeTcs is null) return;
                waitTask = _resumeTcs.Task;
            }
            await waitTask.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
