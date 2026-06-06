using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorSetup.Pages;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup;

public partial class MainWindow : Window
{
    private readonly InstallerViewModel _vm;

    // One instance of each page — created once, reused if the user navigates back
    private readonly UserControl[] _pages;

    private static readonly string[] StepNames =
    {
        "Welcome",
        "License",
        "Hardware",
        "Install Paths",
        "Coding Profile",
        "Model",
        "Ollama Check",
        "Download",
        "Complete",
    };

    public MainWindow()
    {
        InitializeComponent();

        _vm = new InstallerViewModel();

        _pages = new UserControl[]
        {
            new WelcomePage(_vm),
            new LicensePage(_vm),
            new HardwareDetectPage(_vm),
            new InstallPathPage(_vm),
            new ProfilePage(_vm),
            new ModelPage(_vm),
            new OllamaCheckPage(_vm),
            new DownloadPage(_vm),
            new CompletePage(_vm),
        };

        BuildStepList();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallerViewModel.CurrentPage))
                SyncToCurrentPage();
        };

        SyncToCurrentPage();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        _vm.GoBack();
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        // Let the current page validate before we advance
        if (_pages[_vm.PageIndex] is IInstallerPage page && !page.CanLeave())
            return;

        _vm.GoNext();
    }

    // ── Sync UI to ViewModel state ────────────────────────────────────────────

    private void SyncToCurrentPage()
    {
        var idx = _vm.PageIndex;

        // Swap page content
        PageHost.Content = _pages[idx];

        // Progress
        WizardProgress.Value = _vm.ProgressPercent;
        ProgressLabel.Text   = $"Step {idx + 1} of {StepNames.Length}";

        // Nav buttons
        BtnBack.IsEnabled = _vm.CanGoBack;

        var isLastStep = idx == StepNames.Length - 1;
        var isDownload = idx == (int)InstallerViewModel.Page.Download;

        BtnNext.Content    = isDownload ? "Install" : isLastStep ? "Finish" : "Next →";
        BtnNext.IsEnabled  = !isLastStep || _vm.State.InstallationComplete;
        BtnNext.Visibility = isLastStep && _vm.State.InstallationComplete
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (isLastStep && _vm.State.InstallationComplete)
            BtnNext.Visibility = Visibility.Collapsed;

        // Highlight active step in sidebar
        RefreshStepHighlights(idx);

        // On download page, hide nav (the page drives itself)
        BtnBack.Visibility = isDownload ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Visibility = isDownload ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Sidebar step list ─────────────────────────────────────────────────────

    private void BuildStepList()
    {
        StepList.Items.Clear();
        for (int i = 0; i < StepNames.Length; i++)
        {
            var row = new Border { Tag = i, Padding = new Thickness(24, 8, 24, 8) };
            var sp  = new StackPanel { Orientation = Orientation.Horizontal };

            var dot = new Border
            {
                Width        = 18,
                Height       = 18,
                CornerRadius = new CornerRadius(9),
                Background   = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                Margin       = new Thickness(0, 0, 10, 0),
                Tag          = $"dot_{i}",
                Child        = new TextBlock
                {
                    Text                = (i + 1).ToString(),
                    FontSize            = 10,
                    FontWeight          = FontWeights.Bold,
                    Foreground          = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                }
            };

            var lbl = new TextBlock
            {
                Text             = StepNames[i],
                FontFamily       = new FontFamily("Segoe UI"),
                FontSize         = 12,
                Foreground       = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                VerticalAlignment = VerticalAlignment.Center,
                Tag              = $"lbl_{i}",
            };

            sp.Children.Add(dot);
            sp.Children.Add(lbl);
            row.Child = sp;
            StepList.Items.Add(row);
        }
    }

    private void RefreshStepHighlights(int activeIdx)
    {
        for (int i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is not Border row) continue;
            if (row.Child is not StackPanel sp) continue;

            var dot = sp.Children[0] as Border;
            var lbl = sp.Children[1] as TextBlock;
            if (dot is null || lbl is null) continue;

            if (i < activeIdx)
            {
                // Completed
                dot.Background = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                if (dot.Child is TextBlock t) t.Text = "✓";
                lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
            else if (i == activeIdx)
            {
                // Active
                dot.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                if (dot.Child is TextBlock t) t.Text = (i + 1).ToString();
                lbl.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
                lbl.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                // Pending
                dot.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                if (dot.Child is TextBlock t) t.Text = (i + 1).ToString();
                lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                lbl.FontWeight = FontWeights.Normal;
            }
        }
    }
}

/// <summary>
/// Implemented by pages that need to validate state before the user can advance.
/// Return false from <see cref="CanLeave"/> to block navigation and show a message.
/// </summary>
public interface IInstallerPage
{
    bool CanLeave();
}
