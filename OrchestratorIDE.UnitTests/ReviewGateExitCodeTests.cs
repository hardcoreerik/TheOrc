// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Windows reviewer gate: gate-review.ps1's exit code. Only 3 (codex not found) used to be
/// handled; a timeout (2) or any script error was parsed as output with no findings and came
/// back as a CLEAN review.
/// </summary>
[TestFixture]
public sealed class ReviewGateExitCodeTests
{
    [Test]
    public void Reviewed_and_codex_missing_are_the_two_normal_outcomes()
    {
        Assert.That(ReviewGateService.ScriptReviewed(0), Is.True, "verdict is parsed");
        Assert.That(ReviewGateService.ScriptReviewed(3), Is.False, "gate skipped: codex not installed");
    }

    [Test]
    public void A_timeout_is_a_failure_not_a_clean_review() =>
        Assert.Throws<TimeoutException>(() => ReviewGateService.ScriptReviewed(2));

    [TestCase(1)]
    [TestCase(4)]
    [TestCase(-1)]
    public void Any_other_exit_code_is_a_failure(int exitCode)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ReviewGateService.ScriptReviewed(exitCode));
        Assert.That(ex!.Message, Does.Contain($"exit {exitCode}"));
    }
}
