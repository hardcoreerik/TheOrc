// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Regression guard for a 2026-06-20 bug report: newly discovered HIVE nodes (Scan LAN /
/// Find Tailscale nodes / Trust &amp; Add) didn't appear in the constellation canvas until the
/// user navigated away from the Hive tab and back. Root cause was that
/// <c>BtnAddNode_Click</c>/<c>BtnFindTailscale_Click</c>/<c>TrustAndAdd</c> only redrew via the
/// tail end of <c>ProbeAndDrawAsync</c> (after a network probe with its own multi-second
/// timeout, and in one case after an awaited modal dialog). Tab-switching "fixed" it only
/// because re-entering the tab re-fires <c>Loaded</c>, which calls the synchronous
/// <c>DrawConstellation</c> directly. Fix: each handler now calls <c>DrawConstellation</c>
/// synchronously right after mutating the host list, before any await. This test exercises
/// the underlying invariant the fix depends on — that mutating the private host list and
/// calling <c>DrawConstellation</c> immediately produces a visible card — without going
/// through the handlers themselves (which call <c>HiveHosts.Save</c>, writing to the real
/// per-user hive-hosts.json).
/// </summary>
[TestFixture]
public sealed class HivePanelConstellationTests
{
    private static void WithEphemeralIdentity(Action action)
    {
        var instanceField = typeof(HiveIdentity).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertionException("Expected HiveIdentity to have a private static '_instance' field.");
        var createMethod = typeof(HiveIdentity).GetMethod("CreateEphemeral", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertionException("Expected HiveIdentity to expose CreateEphemeral for tests.");

        var prior = instanceField.GetValue(null);
        var ephemeral = (HiveIdentity?)createMethod.Invoke(null, null)
            ?? throw new AssertionException("CreateEphemeral returned null.");

        instanceField.SetValue(null, ephemeral);
        try
        {
            action();
        }
        finally
        {
            instanceField.SetValue(null, prior);
            ephemeral.Dispose();
        }
    }

    private static List<HiveHost> HostsField(HivePanel panel)
    {
        var field = typeof(HivePanel).GetField("_hosts", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertionException("Expected HivePanel to have a private '_hosts' field.");
        return (List<HiveHost>)field.GetValue(panel)!;
    }

    private static void InvokeDrawConstellation(HivePanel panel)
    {
        var method = typeof(HivePanel).GetMethod("DrawConstellation", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertionException("Expected HivePanel to have a private 'DrawConstellation' method.");
        method.Invoke(panel, null);
    }

    private static int NodeCount(HiveConstellationView view)
    {
        var field = typeof(HiveConstellationView).GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertionException("Expected HiveConstellationView to have a private '_nodes' field.");
        return ((IReadOnlyList<HiveNodeVisual>)field.GetValue(view)!).Count;
    }

    [AvaloniaTest]
    public void DrawConstellation_immediately_renders_a_card_for_a_newly_added_host()
    {
        WithEphemeralIdentity(() =>
        {
            var panel = new HivePanel();
            var window = new Window { Width = 900, Height = 700, Content = panel };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var view = panel.FindControl<HiveConstellationView>("HiveView")
                    ?? throw new AssertionException("Expected to find HiveView.");
                var baselineCount = NodeCount(view);

                HostsField(panel).Add(new HiveHost { Name = "REGRESSION-NODE", Url = "http://10.0.0.99:11434" });
                InvokeDrawConstellation(panel);

                Assert.That(NodeCount(view), Is.GreaterThan(baselineCount),
                    "Adding a host and calling DrawConstellation synchronously must update HiveView " +
                    "immediately — handlers must not rely solely on the deferred redraw at the tail " +
                    "of ProbeAndDrawAsync.");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
