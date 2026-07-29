// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Does a failed job's retained error name the CAUSE, or only the layer that wrapped it?
///
/// It named only the layer. Every native failure is wrapped for fail-closed reporting -- "Worker:
/// native role runtime failed. Phase 3B does not fall back." -- and the worker recorded
/// <c>ex.Message</c>, which is the wrapper alone. The real reason sat in an inner exception and was
/// discarded before the result was posted, so the Warchief's evidence said nothing actionable.
///
/// HV-5's diagnosability drill caught it on both fleet machines: a correctly failed, correctly
/// fail-closed job whose cause could not be recovered from the retained evidence
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-5, 2026-07-27). §6 requires the retained
/// diagnostics to be sufficient to identify a cause without interactive debugging.
/// </summary>
[TestFixture]
public sealed class HiveWorkerErrorChainTests
{
    private const string FailClosedWrapper =
        "Worker: native role runtime failed. Phase 3B does not fall back.";

    [Test]
    public void TheWrappedCause_SurvivesIntoTheRetainedError()
    {
        // The exact shape HV-5 induced: a CF reader whose corpus artifact does not exist. The
        // wrapper is what the Warchief used to receive; the 404 is what a reader actually needs.
        var inner = new HttpRequestException(
            "Response status code does not indicate success: 404 (Not Found).");
        var wrapped = new InvalidOperationException(FailClosedWrapper, inner);

        var described = HiveWorkerAgent.DescribeExceptionChain(wrapped);

        Assert.That(described, Does.Contain("404"),
            "the cause must survive — this is the whole point");
        Assert.That(described, Does.Contain(FailClosedWrapper),
            "the fail-closed wrapper is still useful context and must not be dropped either");
        Assert.That(described, Does.Contain(nameof(HttpRequestException)),
            "the inner TYPE is often the most identifying thing available");
    }

    [Test]
    public void ASingleException_IsDescribedWithoutDecoration()
    {
        var described = HiveWorkerAgent.DescribeExceptionChain(
            new InvalidOperationException("Worker: model runtime not configured"));

        Assert.That(described, Does.Contain("Worker: model runtime not configured"));
        Assert.That(described, Does.Not.Contain("←"), "nothing to chain, so no chain separator");
    }

    [Test]
    public void ARepeatedMessage_IsNotRepeatedInTheOutput()
    {
        // A faulted Task's AggregateException commonly re-wraps the same message several layers
        // deep. A result payload is not the place for a wall of identical text.
        var inner = new InvalidOperationException("same message");
        var mid = new InvalidOperationException("same message", inner);
        var outer = new InvalidOperationException("same message", mid);

        var described = HiveWorkerAgent.DescribeExceptionChain(outer);

        Assert.That(described, Is.EqualTo("InvalidOperationException: same message"));
    }

    [Test]
    public void ADeepChain_IsCappedRatherThanUnbounded()
    {
        // Depth-capped so a pathological chain cannot turn a task result into a novel.
        Exception current = new InvalidOperationException("depth-0");
        for (var i = 1; i <= 12; i++)
            current = new InvalidOperationException($"depth-{i}", current);

        var described = HiveWorkerAgent.DescribeExceptionChain(current);

        Assert.That(described.Split(" ← ").Length, Is.LessThanOrEqualTo(5));
        Assert.That(described, Does.Contain("depth-12"), "the outermost frame is still reported");
    }

    [Test]
    public void AnEmptyMessage_DoesNotProduceAnEmptyDescription()
    {
        // Some exceptions carry no message at all; "" as a task's errorMsg is indistinguishable
        // from "no error was retained", which is the failure mode this whole change removes.
        var described = HiveWorkerAgent.DescribeExceptionChain(new EmptyMessageException());

        Assert.That(described, Is.Not.Empty);
        Assert.That(described, Does.Contain(nameof(EmptyMessageException)));
    }

    private sealed class EmptyMessageException : Exception
    {
        public override string Message => "";
    }
}
