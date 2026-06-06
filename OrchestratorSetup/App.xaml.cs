using System.Windows;

namespace OrchestratorSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{ex.Exception.Message}\n\n" +
                "The installer will close. Please report this at:\n" +
                "https://github.com/hardcoreerik/The-Orchestrator/issues",
                "OrchestratorSetup — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };
    }
}
