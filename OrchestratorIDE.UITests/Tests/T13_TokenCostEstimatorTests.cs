// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T13 — Token cost estimator (v1.4 roadmap item). Pure logic tests.
/// </summary>
[TestFixture]
public class T13_TokenCostEstimatorTests
{
    [Test]
    public void Estimate_SumsContextInputAndCompletion()
    {
        var e = TokenCostEstimator.Estimate(1000, 32_768, new string('x', 400), 700);

        Assert.Multiple(() =>
        {
            Assert.That(e.InputTokens, Is.EqualTo(100));   // 400 chars / 4
            Assert.That(e.PromptTokens, Is.EqualTo(1100));
            Assert.That(e.TotalTokens, Is.EqualTo(1800));
            Assert.That(e.FitsContext, Is.True);
        });
    }

    [Test]
    public void Estimate_EmptyInput_HasZeroInputTokens()
        => Assert.That(TokenCostEstimator.Estimate(500, 32_768, "").InputTokens, Is.EqualTo(0));

    [Test]
    public void Estimate_OverflowingContext_FlagsNotFitting()
    {
        var e = TokenCostEstimator.Estimate(32_000, 32_768, new string('x', 4000), 700);

        Assert.That(e.FitsContext, Is.False);
        Assert.That(e.Summary(25), Does.Contain("EXCEEDS CONTEXT"));
    }

    [Test]
    public void Eta_ScalesWithSpeed_AndZeroSpeedIsSafe()
    {
        var e = TokenCostEstimator.Estimate(0, 32_768, "", 500);

        Assert.Multiple(() =>
        {
            Assert.That(e.EtaSeconds(25), Is.EqualTo(20).Within(0.01));
            Assert.That(e.EtaSeconds(0), Is.EqualTo(0));   // no divide-by-zero
        });
    }

    [Test]
    public void Summary_ContainsAllComponents()
    {
        var s = TokenCostEstimator.Estimate(2000, 32_768, new string('x', 800), 700).Summary(25);

        Assert.Multiple(() =>
        {
            Assert.That(s, Does.Contain("context 2,000"));
            Assert.That(s, Does.Contain("input 200"));
            Assert.That(s, Does.Contain("≈700"));
            Assert.That(s, Does.Contain("32,768"));
            Assert.That(s, Does.Contain("~28s response"));   // 700 tok @ 25 tok/s — exact ETA pinned
        });
    }

    [Test]
    public void Estimate_NegativeInputsAreClamped()
    {
        var e = TokenCostEstimator.Estimate(-50, 0, "abc", -10);

        Assert.Multiple(() =>
        {
            Assert.That(e.ContextTokens, Is.EqualTo(0));
            Assert.That(e.CompletionTokens, Is.EqualTo(0));
            Assert.That(e.MaxTokens, Is.EqualTo(1));
        });
    }
}
