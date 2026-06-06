using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class WelcomePage : UserControl, IInstallerPage
{
    public WelcomePage(InstallerViewModel vm) => InitializeComponent();

    public bool CanLeave() => true;
}
