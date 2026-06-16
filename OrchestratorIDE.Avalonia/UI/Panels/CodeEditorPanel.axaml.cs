// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Phase 4: Code editor panel backed by AvaloniaEdit (Avalonia port).
///
/// Public API used by MainWindow.axaml.cs
///   OpenFile(path)   RefreshFile(path)   SetFontSize(size)   SetWordWrap(bool)
///   Editor           ClosePane (event)
///
/// Avalonia 12 DragDrop API notes:
///   DoDragDropAsync  → PointerPressedEventArgs (not PointerEventArgs); IDataTransfer (not DataObject)
///   DataTransfer     → DataTransferItem.Create(DataFormat<T>, value) + DataTransfer.Add(item)
///   Custom formats   → DataFormat.CreateInProcessFormat<T>("name")
///   DragEventArgs    → .DataTransfer (not .Data); .Contains/TryGetValue use DataFormat<T>
///   ColumnDefs       → SplitGrid.ColumnDefinitions[i].Width (Avalonia ignores x:Name on ColumnDefinition)
/// </summary>
public partial class CodeEditorPanel : UserControl
{
    // ── Drag-drop format (in-process; typed; no serialisation needed) ─────
    private static readonly DataFormat<string> TabFormat =
        DataFormat.CreateInProcessFormat<string>("EditorTab");

    public event Action? ClosePane;

    public TextEditor Editor => EditorPrimary;

    private readonly ObservableCollection<EditorTab> _tabs1 = [];
    private readonly ObservableCollection<EditorTab> _tabs2 = [];
    private EditorTab? _active1;
    private EditorTab? _active2;
    private bool _splitVisible;

    private EditorTab?              _dragTab;
    private int                     _dragSourcePane;
    private Point                   _dragStart;
    private bool                    _dragInitiated;
    private PointerPressedEventArgs? _dragPressedArgs;  // DoDragDropAsync needs the original press

    public CodeEditorPanel()
    {
        InitializeComponent();
        TabList1.ItemsSource = _tabs1;
        TabList2.ItemsSource = _tabs2;

        EditorPrimary.TextArea.Caret.PositionChanged   += (_, _) => UpdatePosition(EditorPrimary);
        EditorSecondary.TextArea.Caret.PositionChanged += (_, _) => UpdatePosition(EditorSecondary);

        // DragDrop events wired here — XAML event binding is less reliable in Avalonia.
        PrimaryPane.AddHandler(DragDrop.DragOverEvent,  Primary_DragOver);
        PrimaryPane.AddHandler(DragDrop.DragLeaveEvent, Pane_DragLeave);
        PrimaryPane.AddHandler(DragDrop.DropEvent,      Primary_Drop);
        SecondaryPane.AddHandler(DragDrop.DragOverEvent,  Secondary_DragOver);
        SecondaryPane.AddHandler(DragDrop.DragLeaveEvent, Pane_DragLeave);
        SecondaryPane.AddHandler(DragDrop.DropEvent,      Secondary_Drop);
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
            TbNoFile1.IsVisible      = !hasFile;
            TbPlaceholder1.IsVisible = !hasFile;
            EditorPrimary.IsVisible  = hasFile;
        }
        else
        {
            TbNoFile2.IsVisible       = !hasFile;
            TbPlaceholder2.IsVisible  = !hasFile;
            EditorSecondary.IsVisible = hasFile;
        }
    }

    // ── Tab close ─────────────────────────────────────────────────────────

    private void BtnCloseTab_Click(object? sender, RoutedEventArgs e)
    {
        // DataContext on button inside DataTemplate = the EditorTab instance.
        if ((sender as Control)?.DataContext is EditorTab tab)
        {
            if (_tabs1.Contains(tab)) RemoveTab(1, tab);
            else                      RemoveTab(2, tab);
        }
        e.Handled = true;
    }

    private void RemoveTab(int pane, EditorTab tab)
    {
        var list   = pane == 1 ? _tabs1 : _tabs2;
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

    // ── Tab pointer events (activation + drag start) ──────────────────────

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        if ((sender as Control)?.DataContext is not EditorTab tab) return;

        int pane    = _tabs1.Contains(tab) ? 1 : 2;
        _dragTab        = tab;
        _dragSourcePane = pane;
        _dragStart      = e.GetPosition(this);
        _dragInitiated  = false;
        _dragPressedArgs = e;  // DoDragDropAsync requires the original press args

        ActivateTab(pane, tab);
        var editor = pane == 1 ? EditorPrimary : EditorSecondary;
        if (File.Exists(tab.Path))
            LoadContent(editor, File.ReadAllText(tab.Path), tab.Path);

        e.Handled = true;
    }

    private async void Tab_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_dragTab == null || _dragInitiated || _dragPressedArgs == null) return;

        var pos  = e.GetPosition(this);
        var diff = pos - _dragStart;
        if (Math.Abs(diff.X) < 8 && Math.Abs(diff.Y) < 8) return;

        _dragInitiated = true;

        // Avalonia 12 DragDrop: DataFormat<T> → DataTransferItem → DataTransfer
        var item     = DataTransferItem.Create(TabFormat, _dragTab.Path);
        var transfer = new DataTransfer();
        transfer.Add(item);
        await DragDrop.DoDragDropAsync(_dragPressedArgs, transfer, DragDropEffects.Move);

        HideDropZones();
        _dragTab         = null;
        _dragPressedArgs = null;
        _dragInitiated   = false;
    }

    // ── Drag-over handlers ────────────────────────────────────────────────

    private void Primary_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(TabFormat)) { e.DragEffects = DragDropEffects.None; return; }
        DropZonePrimary.IsVisible = true;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Secondary_DragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(TabFormat)) { e.DragEffects = DragDropEffects.None; return; }
        DropZoneSecondary.IsVisible = true;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Pane_DragLeave(object? sender, DragEventArgs e)
        => HideDropZones();

    // ── Drop handlers ─────────────────────────────────────────────────────

    private void Primary_Drop(object? sender, DragEventArgs e)
    {
        HideDropZones();
        if (!e.DataTransfer.Contains(TabFormat)) return;
        var path = e.DataTransfer.TryGetValue(TabFormat) ?? "";
        OpenInPane(1, path);
        e.Handled = true;
    }

    private void Secondary_Drop(object? sender, DragEventArgs e)
    {
        HideDropZones();
        if (!e.DataTransfer.Contains(TabFormat)) return;
        var path = e.DataTransfer.TryGetValue(TabFormat) ?? "";
        OpenInPane(2, path);
        e.Handled = true;
    }

    // ── Split / close pane buttons ────────────────────────────────────────

    private void BtnSplit_Click(object? sender, RoutedEventArgs e)
    {
        if (_active1 != null)
            OpenInPane(2, _active1.Path);
        else
            ShowSplit();
    }

    private void BtnClosePrimary_Click(object? sender, RoutedEventArgs e)
        => ClosePane?.Invoke();

    private void BtnCloseSecondary_Click(object? sender, RoutedEventArgs e)
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
        _splitVisible           = true;
        SplitGrid.ColumnDefinitions[1].Width = new GridLength(4);
        SplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        PaneSplitter.IsVisible  = true;
        SecondaryPane.IsVisible = true;
    }

    private void HideSplit()
    {
        _splitVisible           = false;
        SplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
        SplitGrid.ColumnDefinitions[2].Width = new GridLength(0);
        PaneSplitter.IsVisible  = false;
        SecondaryPane.IsVisible = false;
        _tabs2.Clear();
        _active2                = null;
        EditorSecondary.Document.Text = "";
        ShowEditorOrPlaceholder(2, false);
    }

    private void HideDropZones()
    {
        DropZonePrimary.IsVisible   = false;
        DropZoneSecondary.IsVisible = false;
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

    // ── Helpers ───────────────────────────────────────────────────────────

    private static IHighlightingDefinition? GetHighlighting(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs"   => HighlightingManager.Instance.GetDefinition("C#"),
            ".axaml"=> HighlightingManager.Instance.GetDefinition("XML"),
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
            ".axaml"=> "AXAML",
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

// ── Tab view-model (Avalonia — IBrush instead of WPF Brush) ──────────────────

public class EditorTab : INotifyPropertyChanged
{
    private static readonly IBrush ActiveBg   = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly IBrush InactiveBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
    private static readonly IBrush ActiveFg   = new SolidColorBrush(Colors.White);
    private static readonly IBrush InactiveFg = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    public string Path        { get; }
    public string DisplayName { get; }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPC(); OnPC(nameof(TabBackground)); OnPC(nameof(TabForeground)); }
    }

    public IBrush TabBackground => _isActive ? ActiveBg   : InactiveBg;
    public IBrush TabForeground => _isActive ? ActiveFg   : InactiveFg;

    public EditorTab(string path)
    {
        Path        = path;
        DisplayName = System.IO.Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPC([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
