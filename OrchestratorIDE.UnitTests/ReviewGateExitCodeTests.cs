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

    // ── Verdict text ──────────────────────────────────────────────────────────────────

    [Test]
    public void Clean_needs_the_word_CLEAN_and_findings_still_win()
    {
        Assert.That(ReviewGateService.ParseVerdict("# Gate review\n\nCLEAN", "").Verdict, Is.EqualTo(ReviewGateService.GateVerdict.Clean));
        var r = ReviewGateService.ParseVerdict("BLOCKER a.cs:3 — null deref\nMINOR b.cs:1 — naming", "");
        Assert.That(r.Verdict, Is.EqualTo(ReviewGateService.GateVerdict.Blocker));
        Assert.That(r.Findings, Has.Count.EqualTo(2));
    }

    [TestCase("")]
    [TestCase("   \n")]
    [TestCase("Error: request failed with status 401")]
    [TestCase("I can't review this.")]
    public void A_reply_with_no_findings_and_no_CLEAN_is_a_failure(string raw) =>
        Assert.Throws<InvalidOperationException>(() => ReviewGateService.ParseVerdict(raw, ""));

    [Test]
    public void The_gate_only_counts_workspaces_that_ship_its_script()
    {
        var ws = Path.Combine(Path.GetTempPath(), "orc-gate-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "Tools"));
        try
        {
            Assert.That(ReviewGateService.HasGateScript(ws), Is.False);
            File.WriteAllText(Path.Combine(ws, "Tools", "gate-review.ps1"), "");
            Assert.That(ReviewGateService.HasGateScript(ws), Is.True);
        }
        finally { Directory.Delete(ws, recursive: true); }
    }
}
