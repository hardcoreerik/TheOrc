// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// TryQueryLiveNvidiaBudget shells out to nvidia-smi, so its result is genuinely
/// environment-dependent — a machine with no NVIDIA GPU (or no driver installed) legitimately
/// returns null, which is the documented, correct behavior, not a test failure. These tests
/// assert invariants that hold either way rather than asserting a specific outcome.
/// </summary>
[TestFixture]
public sealed class NativeVramProbeTests
{
    [Test]
    public void TryQueryLiveNvidiaBudget_Returns_Null_Or_A_Sane_Budget()
    {
        var budget = NativeVramProbe.TryQueryLiveNvidiaBudget();

        // No assertion that it MUST be non-null: CI/dev machines without an NVIDIA GPU are
        // expected to hit the "no nvidia-smi" / "non-NVIDIA GPU" null path, and that is the
        // correct, documented behavior, not a failure.
        if (budget is null)
            return;

        Assert.Multiple(() =>
        {
            Assert.That(budget.TotalBytes, Is.GreaterThan(0),
                "A returned budget must report a real positive total.");
            Assert.That(budget.ReservedBytes, Is.GreaterThanOrEqualTo(0),
                "nvidia-smi's memory.used should never be negative.");
            Assert.That(budget.ReservedBytes, Is.LessThanOrEqualTo(budget.TotalBytes),
                "Used VRAM should never exceed total VRAM (barring a driver-reporting anomaly).");
        });
    }

    [Test]
    public void TryQueryLiveNvidiaBudget_Is_Repeatable_Without_Throwing()
    {
        // Called fresh on every admission by RuntimeOrchestrator.EnsureAdmitted -- must not leak
        // process handles or throw on repeated back-to-back invocation.
        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 3; i++)
                _ = NativeVramProbe.TryQueryLiveNvidiaBudget();
        });
    }
}
