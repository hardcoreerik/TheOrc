using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Ctrl+K command palette — fuzzy-searchable list of registered actions.
/// </summary>
public partial class CommandPalette : UserControl
{
    public event Action<PaletteCommand>? CommandSelected;
    public event Action? Dismissed;

    private readonly List<PaletteCommand> _allCommands = [];
    private readonly ObservableCollection<PaletteCommand> _filtered = [];

    public CommandPalette()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filtered;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };
    }

    public void RegisterCommands(IEnumerable<PaletteCommand> commands)
    {
        _allCommands.Clear();
        _allCommands.AddRange(commands);
        Filter("");
    }

    private void Filter(string query)
    {
        _filtered.Clear();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

        var q = query.Trim().ToLowerInvariant();
        var matches = string.IsNullOrEmpty(q)
            ? _allCommands
            : _allCommands.Where(c =>
                c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Detail.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)));

        foreach (var cmd in matches.OrderBy(c => c.SortOrder))
            _filtered.Add(cmd);

        if (_filtered.Count > 0)
            ResultsList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => Filter(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                if (ResultsList.SelectedIndex < _filtered.Count - 1)
                    ResultsList.SelectedIndex++;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                break;
            case Key.Up:
                e.Handled = true;
                if (ResultsList.SelectedIndex > 0)
                    ResultsList.SelectedIndex--;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                break;
            case Key.Enter:
                e.Handled = true;
                Execute();
                break;
            case Key.Escape:
                e.Handled = true;
                Dismissed?.Invoke();
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Dismissed?.Invoke();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e) => Execute();

    private void Execute()
    {
        if (ResultsList.SelectedItem is PaletteCommand cmd)
        {
            Dismissed?.Invoke();          // Close first
            CommandSelected?.Invoke(cmd); // Then execute
        }
    }
}

// ── Command model ─────────────────────────────────────────────────────────────

public class PaletteCommand
{
    public string Id          { get; set; } = "";
    public string Label       { get; set; } = "";
    public string Detail      { get; set; } = "";
    public string Icon        { get; set; } = "·";
    public string Shortcut    { get; set; } = "";
    public string[] Keywords  { get; set; } = [];
    public int SortOrder      { get; set; } = 100;

    public Brush IconColor => Icon switch
    {
        "⚡" => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        "📁" => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
        "⚙" => new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)),
        "⬡" => new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)),
        "▶" => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        "●" => new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)),
        _ => new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85)),
    };

    public FontWeight LabelWeight => SortOrder < 50
        ? FontWeights.SemiBold
        : FontWeights.Normal;
}
