using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Controls;

public partial class ModelPickerPopup : UserControl
{
    public event Action<string>? ModelSelected;
    public event Action? Dismissed;

    private readonly ObservableCollection<ModelItemVm> _items = [];

    public ModelPickerPopup()
    {
        InitializeComponent();
        ModelList.ItemsSource = _items;
    }

    public void Load(IReadOnlyList<string> installedModels, string activeModel)
    {
        _items.Clear();
        TbInstalled.Text = $"{installedModels.Count} installed";

        // Preferred order
        var preferred = new[] {
            "qwen2.5-coder:14b", "qwen2.5-coder:7b",
            "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M",
            "hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M",
            "hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M",
            "hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M",
            "hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0",
            "qwen2.5:14b-instruct", "gemma4:e4b", "phi4-mini:latest",
            "llama3.1:8b",
        };

        // Sort: preferred first, then rest alphabetically
        var ordered = preferred
            .Where(p => installedModels.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Concat(installedModels
                .Where(m => !preferred.Contains(m, StringComparer.OrdinalIgnoreCase)
                         && !m.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m))
            .ToList();

        foreach (var modelId in ordered)
        {
            var profile = ModelProfiles.Get(modelId);
            var isActive = modelId.Equals(activeModel, StringComparison.OrdinalIgnoreCase);

            _items.Add(new ModelItemVm
            {
                ModelId      = modelId,
                Name         = profile.Name,
                Description  = profile.Description,
                ContextBadge = $"{profile.ContextK}k ctx",
                ToolSetBadge = profile.ToolSet.ToString().ToLower(),
                IsActive     = isActive,
                ActiveDot    = isActive ? "●" : "",
                NameColor    = new SolidColorBrush(isActive
                    ? Color.FromRgb(0x4E, 0xC9, 0xB0)
                    : Color.FromRgb(0xCC, 0xCC, 0xCC)),
                RowBg        = isActive
                    ? new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71))
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            });
        }
    }

    private void Model_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ModelItemVm vm)
        {
            ModelSelected?.Invoke(vm.ModelId);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Dismissed?.Invoke();
    }
}

public class ModelItemVm
{
    public string  ModelId      { get; set; } = "";
    public string  Name         { get; set; } = "";
    public string  Description  { get; set; } = "";
    public string  ContextBadge { get; set; } = "";
    public string  ToolSetBadge { get; set; } = "";
    public bool    IsActive     { get; set; }
    public string  ActiveDot    { get; set; } = "";
    public Brush   NameColor    { get; set; } = Brushes.White;
    public Brush   RowBg        { get; set; } = Brushes.Transparent;
}
