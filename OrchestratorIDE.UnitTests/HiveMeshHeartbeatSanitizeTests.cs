// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// CodeRabbit finding (HiveMeshHeartbeat.cs:262, Minor): a rejection reason read from an UNTRUSTED
/// remote peer's HTTP response body was logged verbatim, with no newline stripping. A malicious or
/// compromised peer could return a body containing embedded newlines formatted to look like fake
/// log entries -- a classic log-forging attack -- making forged lines indistinguishable from
/// genuine ones to an operator or an automated log scanner.
///
/// These tests pin the fix at the unit doing the sanitizing, independent of the HTTP plumbing
/// around it (mocking an HttpResponseMessage for this would be a lot of ceremony for one string
/// transform) -- `Sanitize` is exposed `internal` for exactly this.
/// </summary>
[TestFixture]
public sealed class HiveMeshHeartbeatSanitizeTests
{
    [Test]
    public void EmbeddedNewlines_AreCollapsedToASingleLine()
    {
        var forged = "real error\n[12:00:00] ⚠ FAKE CRITICAL: system compromised";

        var sanitized = HiveMeshHeartbeat.Sanitize(forged);

        Assert.That(sanitized, Does.Not.Contain("\n"));
        Assert.That(sanitized, Does.Not.Contain("\r"));
        // Content is preserved, just no longer able to masquerade as a second log line.
        Assert.That(sanitized, Does.Contain("real error"));
        Assert.That(sanitized, Does.Contain("FAKE CRITICAL"));
    }

    [Test]
    public void CarriageReturnNewline_IsAlsoCollapsed()
    {
        var forged = "line one\r\nline two\r\nline three";

        var sanitized = HiveMeshHeartbeat.Sanitize(forged);

        Assert.That(sanitized, Is.EqualTo("line one line two line three"));
    }

    [Test]
    public void OrdinaryText_PassesThroughUnchanged()
    {
        Assert.That(HiveMeshHeartbeat.Sanitize("HMAC mismatch"), Is.EqualTo("HMAC mismatch"));
    }

    [Test]
    public void Null_StaysNull()
    {
        Assert.That(HiveMeshHeartbeat.Sanitize(null), Is.Null);
    }

    [Test]
    public void LeadingAndTrailingWhitespace_IsTrimmed()
    {
        Assert.That(HiveMeshHeartbeat.Sanitize("  spaced out  "), Is.EqualTo("spaced out"));
    }
}
