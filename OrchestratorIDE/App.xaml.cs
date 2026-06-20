// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using OrchestratorIDE.Tests;

namespace OrchestratorIDE;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--autotest"))
        {
            // Headless integration test mode — show test window, exit when done
            new AutoTestWindow().Show();
        }
        else if (e.Args.Contains("--tool-probe"))
        {
            // Retired 2026-06-19 alongside ToolCallTestWindow/ModelCapabilityTestDialog —
            // see docs/SPONSOR_TEST_LAB.md. Fail closed and exit immediately rather than
            // falling through to a full GUI launch, since a caller still scripting this
            // flag expects a headless probe-and-exit. No modal dialog here — a review pass
            // caught that MessageBox.Show is itself a blocking call nothing automated can
            // dismiss, which defeats the entire point of failing closed for a script. The
            // non-zero exit code is the signal; Console.Error reaches a caller that has a
            // console attached, but the exit code must not depend on that being true.
            Console.Error.WriteLine(
                "--tool-probe was retired along with ToolCallTestWindow (exit code 1). " +
                "See docs/SPONSOR_TEST_LAB.md.");
            Shutdown(1);
        }
        else
        {
            // Normal launch
            new MainWindow().Show();
        }
    }
}
