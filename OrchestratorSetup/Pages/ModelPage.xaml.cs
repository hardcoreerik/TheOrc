using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class ModelPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    public ModelPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        // Ensure a recommendation exists
        _vm.UpdateRecommendedModel();

        var rec = _vm.RecommendedModel;
        if (rec is not null)
        {
            TxtRecName.Text    = rec.Name;
            TxtRecDesc.Text    = rec.Description;
            TxtRecVram.Text    = rec.VramDisplay;
            TxtRecSize.Text    = rec.SizeDisplay;
            TxtRecContext.Text = rec.ContextDisplay;
            TxtRecStars.Text   = rec.StarsDisplay;
        }

        // Populate the full list
        ModelList.ItemsSource = _vm.AllModels;

        UpdateSelectionLabel();
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void BtnUseRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.RecommendedModel is { } rec)
        {
            _vm.SelectModel(rec);
            UpdateSelectionLabel();
        }
    }

    private void ModelRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: ModelEntry entry })
        {
            _vm.SelectModel(entry);
            UpdateSelectionLabel();
        }
    }

    private void ModelSelectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            var entry = _vm.AllModels.FirstOrDefault(m => m.Id == id);
            if (entry is not null)
            {
                _vm.SelectModel(entry);
                UpdateSelectionLabel();
            }
        }
    }

    private void UpdateSelectionLabel()
    {
        var id    = _vm.State.SelectedModelId;
        var entry = _vm.AllModels.FirstOrDefault(m => m.Id == id);
        TxtCurrentSelection.Text = entry is not null
            ? $"Selected: {entry.Name}  ({entry.SizeDisplay})"
            : "No model selected";
    }

    public bool CanLeave()
    {
        if (string.IsNullOrEmpty(_vm.State.SelectedModelId))
        {
            MessageBox.Show(
                "Please select a model before continuing.",
                "Model Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }
}
