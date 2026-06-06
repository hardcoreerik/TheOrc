using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonEditB;
using AvalonEditB.Highlighting;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Phase 4: Code editor panel backed by AvalonEditB.
///
/// Features
/// ─────────
/// • Multi-tab (one document per open file)
/// • Two side-by-side panes — click ⊟ or drag a tab to the right side to split
/// • Drag a tab from pane 1 over pane 2 (or vice versa) and drop to move it
/// • Syntax highlighting via AvalonEditB's built-in highlighting definitions
/// • Ln/Col footer + "? Legend" popup explaining all colour codes
///
/// Public API used by MainWindow
/// ──────────────────────────────
///   OpenFile(path)          open or focus a file in the primary pane
///   RefreshFile(path)       re-read from disk after an agent write
///   SetFontSize(size)       sync both panes
///   SetWordWrap(on)         sync both panes
///   Editor                  exposes primary AvalonEdit for MainWindow compat
///   ClosePane (event)       fired when user clicks ✕ — MainWindow collapses split
/// </summary>
public partial class CodeEditorPanel : UserControl
{
    // ── Public events ─────────────────────────────────────────────────────
    public event Action? ClosePane;

    // ── Public compat property (MainWindow addresses this for font/wrap) ──
    public TextEditor Editor => EditorPrimary;

    // ── Tab collections ───────────────────────────────────────────────────
    private readonly ObservableCollection<EditorTab> _tabs1 = [];
    private readonly ObservableCollection<EditorTab> _tabs2 = [];
    private EditorTab? _active1;
    private EditorTab? _active2;
    private bool _splitVisible;

    // ── Drag state ────────────────────────────────────────────────────────
    private EditorTab? _dragTab;
    private int        _dragSourcePane;   // 1 or 2
    private Point      _dragStart;
    private bool       _dragInitiated;

    // ── Init ──────────────────────────────────────────────────────────────

    public CodeEditorPanel()
    {
        InitializeComponent();
        TabList1.ItemsSource = _tabs1;
        TabList2.ItemsSource = _tabs2;

        EditorPrimary.TextArea.Caret.PositionChanged   += (_, _) => UpdatePosition(EditorPrimary);
        EditorSecondary.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition(EditorSecondary);
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void OpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var existing = _tabs1.FirstOrDefault(t =>
            t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActivateTab(1, existing); return; }

        try
        {
            var text = File.ReadAllText(path);
            var tab  = new EditorTab(path);
            _tabs1.Add(tab);
            ActivateTab(1, tab);
            LoadContent(EditorPrimary, text, path);
        }
        catch (Exception ex) { LoadContent(EditorPrimary, $"// Could not open: {ex.Message}", path); }
    }

    public void RefreshFile(string path)
    {
        TryRefreshPane(path, _tabs1, _active1, EditorPrimary);
        TryRefreshPane(path, _tabs2, _active2, EditorSecondary);
    }

    public void SetFontSize(double size)
    {
        EditorPrimary.FontSize   = size;
        EditorSecondary.FontSize = size;
    }

    public void SetWordWrap(bool on)
    {
        EditorPrimary.WordWrap   = on;
        EditorSecondary.WordWrap = on;
    }

    // ── Tab activation ────────────────────────────────────────────────────

    private void ActivateTab(int pane, EditorTab tab)
    {
        if (pane == 1)
        {
            foreach (var t in _tabs1) t.IsActive = t == tab;
            _active1 = tab;
            ShowEditorOrPlaceholder(1, _tabs1.Count > 0);
            UpdateFooter(EditorPrimary, tab.Path);
        }
        else
        {
            foreach (var t in _tabs2) t.IsActive = t == tab;
            _active2 = tab;
            ShowEditorOrPlaceholder(2, _tabs2.Count > 0);
            UpdateFooter(EditorSecondary, tab.Path);
        }
    }

    private void ShowEditorOrPlaceholder(int pane, bool hasFile)
    {
        if (pane == 1)
        {
            TbNoFile1.Visibility      = hasFile ? Visibility.Collapsed : Visibility.Visible;
            TbPlaceholder1.Visibility = hasFile ? Visibility.Collapsed : Visibility.Visible;
            EditorPrimary.Visibility  = hasFile ? Visibility.Visible   : Visibility.Collapsed;
        }
        else
        {
            TbNoFile2.Visibility       = hasFile ? Visibility.Collapsed : Visibility.Visible;
            TbPlaceholder2.Visibility  = hasFile ? Visibility.Collapsed : Visibility.Visible;
            EditorSecondary.Visibility = hasFile ? Visibility.Visible   : Visibility.Collapsed;
        }
    }

    // ── Tab close ─────────────────────────────────────────────────────────

    private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EditorTab tab)
        {
            // Figure out which pane owns this tab
            if (_tabs1.Contains(tab)) RemoveTab(1, tab);
            else                      RemoveTab(2, tab);
        }
    }

    private void RemoveTab(int pane, EditorTab tab)
    {
        var list   = pane == 1 ? _tabs1 : _tabs2;
        var active = pane == 1 ? _active1 : _active2;
        var editor = pane == 1 ? EditorPrimary : EditorSecondary;

        var idx = list.IndexOf(tab);
        list.Remove(tab);

        if (list.Count == 0)
        {
            if (pane == 1) _active1 = null; else _active2 = null;
            editor.Document.Text = "";
            ShowEditorOrPlaceholder(pane, false);
            if (pane == 1) TbFilePath.Text = "";
        }
        else
        {
            var next = list[Math.Min(idx, list.Count - 1)];
            ActivateTab(pane, next);
            LoadContent(editor, File.Exists(next.Path)
                ? File.ReadAllText(next.Path) : "", next.Path);
        }
    }

    // ── Tab mouse events (activation + drag start) ────────────────────────

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((sender as FrameworkElement)?.Tag is not EditorTab tab) return;

        // Determine which pane owns the tab
        int pane = _tabs1.Contains(tab) ? 1 : 2;

        // Record drag start position
        _dragTab        = tab;
        _dragSourcePane = pane;
        _dragStart      = e.GetPosition(this);
        _dragInitiated  = false;

        // Activate the tab
        ActivateTab(pane, tab);
        if (pane == 1 && File.Exists(tab.Path))
            LoadContent(EditorPrimary, File.ReadAllText(tab.Path), tab.Path);
        else if (pane == 2 && File.Exists(tab.Path))
            LoadContent(EditorSecondary, File.ReadAllText(tab.Path), tab.Path);
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragTab == null || _dragInitiated) return;

        var pos  = e.GetPosition(this);
        var diff = pos - _dragStart;
        if (Math.Abs(diff.X) < 8 && Math.Abs(diff.Y) < 8) return;

        // Begin drag
        _dragInitiated = true;
        var data = new DataObject("EditorTab", _dragTab.Path);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);

        // Drag ended — hide any overlays
        HideDropZones();
        _dragTab       = null;
        _dragInitiated = false;
    }

    // ── Drag-over handlers ────────────────────────────────────────────────

    private void Primary_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("EditorTab")) { e.Effects = DragDropEffects.None; return; }

        // Show drop zone on the right 40% → hint to split
        var pos = e.GetPosition(EditorPrimary);
        bool onRightHalf = EditorPrimary.ActualWidth > 0
            && pos.X > EditorPrimary.ActualWidth * 0.6;

        if (onRightHalf)
        {
            DropZonePrimary.Visibility = Visibility.Visible;
            DropZonePrimary.Width      = EditorPrimary.ActualWidth * 0.4;
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            DropZonePrimary.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Secondary_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("EditorTab")) { e.Effects = DragDropEffects.None; return; }
        DropZoneSecondary.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Pane_DragLeave(object sender, DragEventArgs e)
        => HideDropZones();

    // ── Drop handlers ─────────────────────────────────────────────────────

    private void Primary_Drop(object sender, DragEventArgs e)
    {
        HideDropZones();
        if (!e.Data.GetDataPresent("EditorTab")) return;

        var path     = e.Data.GetData("EditorTab") as string ?? "";
        var pos      = e.GetPosition(EditorPrimary);
        bool onRight = EditorPrimary.ActualWidth > 0
            && pos.X > EditorPrimary.ActualWidth * 0.6;

        if (onRight)
            OpenInPane(2, path);   // split
        // else: drop on left half — ignore (already in primary)

        e.Handled = true;
    }

    private void Secondary_Drop(object sender, DragEventArgs e)
    {
        HideDropZones();
        if (!e.Data.GetDataPresent("EditorTab")) return;

        var path = e.Data.GetData("EditorTab") as string ?? "";
        OpenInPane(2, path);
        e.Handled = true;
    }

    // ── Split / close pane buttons ────────────────────────────────────────

    private void BtnSplit_Click(object sender, RoutedEventArgs e)
    {
        // Open the currently active primary file in the secondary pane
        if (_active1 != null)
            OpenInPane(2, _active1.Path);
        else
            ShowSplit();  // show empty secondary so user can drop/open something
    }

    private void BtnClosePrimary_Click(object sender, RoutedEventArgs e)
        => ClosePane?.Invoke();

    private void BtnCloseSecondary_Click(object sender, RoutedEventArgs e)
        => HideSplit();

    // ── Internal pane management ──────────────────────────────────────────

    private void OpenInPane(int pane, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (pane == 2) ShowSplit();

        var list   = pane == 1 ? _tabs1 : _tabs2;
        var editor = pane == 1 ? EditorPrimary : EditorSecondary;

        var existing = list.FirstOrDefault(t =>
            t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActivateTab(pane, existing); return; }

        try
        {
            var text = File.ReadAllText(path);
            var tab  = new EditorTab(path);
            list.Add(tab);
            ActivateTab(pane, tab);
            LoadContent(editor, text, path);
        }
        catch (Exception ex)
        {
            var tab = new EditorTab(path);
            list.Add(tab);
            ActivateTab(pane, tab);
            LoadContent(editor, $"// Could not open: {ex.Message}", path);
        }
    }

    private void ShowSplit()
    {
        if (_splitVisible) return;
        _splitVisible              = true;
        ColGutter.Width            = new GridLength(4);
        ColSecondary.Width         = new GridLength(1, GridUnitType.Star);
        PaneSplitter.Visibility    = Visibility.Visible;
        SecondaryPane.Visibility   = Visibility.Visible;
    }

    private void HideSplit()
    {
        _splitVisible              = false;
        ColGutter.Width            = new GridLength(0);
        ColSecondary.Width         = new GridLength(0);
        PaneSplitter.Visibility    = Visibility.Collapsed;
        SecondaryPane.Visibility   = Visibility.Collapsed;
        _tabs2.Clear();
        _active2                   = null;
        EditorSecondary.Document.Text = "";
        ShowEditorOrPlaceholder(2, false);
    }

    private void HideDropZones()
    {
        DropZonePrimary.Visibility   = Visibility.Collapsed;
        DropZoneSecondary.Visibility = Visibility.Collapsed;
    }

    // ── Content + highlighting ────────────────────────────────────────────

    private void LoadContent(TextEditor editor, string text, string path)
    {
        editor.Document.Text      = text;
        editor.SyntaxHighlighting = GetHighlighting(path);
        editor.ScrollToHome();
        UpdateFooter(editor, path);
        UpdatePosition(editor);
    }

    private void TryRefreshPane(
        string path,
        ObservableCollection<EditorTab> tabs,
        EditorTab? active,
        TextEditor editor)
    {
        var tab = tabs.FirstOrDefault(t =>
            t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (tab == null || tab != active) return;
        try { LoadContent(editor, File.ReadAllText(path), path); }
        catch { /* non-fatal */ }
    }

    // ── Footer ────────────────────────────────────────────────────────────

    private void UpdateFooter(TextEditor editor, string path)
    {
        // Only update footer for the focused / last-interacted editor
        if (editor == EditorPrimary || !_splitVisible)
        {
            TbFilePath.Text = path;
            TbLang.Text     = LangName(path);
        }
    }

    private void UpdatePosition(TextEditor editor)
    {
        if (editor != EditorPrimary && _splitVisible) return;
        var c = editor.TextArea.Caret;
        TbPosition.Text = $"Ln {c.Line}, Col {c.Column}";
    }

    // ── Legend popup ──────────────────────────────────────────────────────

    private void BtnLegend_Click(object sender, RoutedEventArgs e)
        => LegendPopup.IsOpen = !LegendPopup.IsOpen;

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IHighlightingDefinition? GetHighlighting(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs"   => HighlightingManager.Instance.GetDefinition("C#"),
            ".xaml" => HighlightingManager.Instance.GetDefinition("XML"),
            ".xml"  => HighlightingManager.Instance.GetDefinition("XML"),
            ".json" => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".js"   => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".ts"   => HighlightingManager.Instance.GetDefinition("JavaScript"),
            ".py"   => HighlightingManager.Instance.GetDefinition("Python"),
            ".ps1"  => HighlightingManager.Instance.GetDefinition("PowerShell"),
            ".html" => HighlightingManager.Instance.GetDefinition("HTML"),
            ".css"  => HighlightingManager.Instance.GetDefinition("CSS"),
            ".sql"  => HighlightingManager.Instance.GetDefinition("TSQL"),
            _       => HighlightingManager.Instance.GetDefinitionByExtension(ext),
        };
    }

    private static string LangName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs"   => "C#",
            ".xaml" => "XAML",
            ".xml"  => "XML",
            ".json" => "JSON",
            ".js"   => "JavaScript",
            ".ts"   => "TypeScript",
            ".py"   => "Python",
            ".ps1"  => "PowerShell",
            ".html" => "HTML",
            ".css"  => "CSS",
            ".sql"  => "SQL",
            ".md"   => "Markdown",
            ".txt"  => "Text",
            ".rs"   => "Rust",
            ".go"   => "Go",
            var e   => e.TrimStart('.').ToUpperInvariant(),
        };
}

// ── Tab view-model ────────────────────────────────────────────────────────────

public class EditorTab : INotifyPropertyChanged
{
    private static readonly SolidColorBrush ActiveBg   = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly SolidColorBrush InactiveBg = new(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly SolidColorBrush ActiveFg   = new(Colors.White);
    private static readonly SolidColorBrush InactiveFg = new(Color.FromRgb(0x99, 0x99, 0x99));

    public string Path        { get; }
    public string DisplayName { get; }   // filename

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPC(); OnPC(nameof(TabBackground)); OnPC(nameof(TabForeground)); }
    }

    public Brush TabBackground => _isActive ? ActiveBg   : InactiveBg;
    public Brush TabForeground => _isActive ? ActiveFg   : InactiveFg;

    public EditorTab(string path)
    {
        Path        = path;
        DisplayName = System.IO.Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
