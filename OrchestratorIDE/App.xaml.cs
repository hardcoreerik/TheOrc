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
        else
        {
            // Normal launch
            new MainWindow().Show();
        }
    }
}
