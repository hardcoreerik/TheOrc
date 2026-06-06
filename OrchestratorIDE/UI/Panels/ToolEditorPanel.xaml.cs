using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvalonEditB;
using AvalonEditB.Highlighting;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Phase 7 Layer 3 — Live tool editor panel.
///
/// Write a C# class implementing ICustomTool, hit Compile → Load,
/// and the agent gains that tool in the current session immediately.
/// Save persists the source to the workspace so it reloads next time.
/// </summary>
public partial class ToolEditorPanel : UserControl
{
    // ── Public state set by MainWindow ────────────────────────────────────
    public ToolCompiler? Compiler    { get; set; }
    public string?       WorkspaceRoot { get; set; }

    // ── Diagnostics list ──────────────────────────────────────────────────
    private readonly ObservableCollection<DiagViewModel> _diags = [];

    // ── Last successful compile bytes (held until Load is clicked) ────────
    private CompileResult? _lastCompile;

    // ── Init ──────────────────────────────────────────────────────────────

    public ToolEditorPanel()
    {
        InitializeComponent();
        DiagList.ItemsSource = _diags;

        // Set C# syntax highlighting
        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");

        // Start with a fresh scaffold
        LoadScaffold();
    }

    // ── Public: update loaded-tools badge (called by MainWindow after load) ─

    public void RefreshLoadedBadge(IReadOnlyList<string> toolNames)
    {
        if (toolNames.Count == 0)
            TbLoadedTools.Text = "";
        else
            TbLoadedTools.Text = $"⚡ Live: {string.Join(", ", toolNames)}";
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var toolId    = TbToolName.Text.Trim();
        var className = ToClassName(toolId);
        LoadScaffold(className, string.IsNullOrWhiteSpace(toolId) ? "my_tool" : toolId);
        ClearDiags();
        SetStatus("Ready — scaffold loaded. Edit then hit ▶ Compile.", neutral: true);
    }

    private async void BtnCompile_Click(object sender, RoutedEventArgs e)
    {
        if (Compiler is null) { SetStatus("Compiler not initialised.", error: true); return; }

        SetStatus("Compiling…", neutral: true);
        BtnCompile.IsEnabled = false;
        BtnLoad.IsEnabled    = false;
        _lastCompile         = null;

        try
        {
            var source = CodeEditor.Document.Text;
            var result = await Compiler.CompileAsync(source);
            _lastCompile = result;
            ShowDiags(result.Diagnostics);

            if (result.Success)
            {
                var warns = result.Diagnostics.Count(d => d.Severity == DiagSeverity.Warning);
                SetStatus($"✓ Compiled OK — {(warns == 0 ? "0 warnings" : $"{warns} warning(s)")}. " +
                          "Hit ⚡ Load to activate.", success: true);
                BtnLoad.IsEnabled = true;
            }
            else
            {
                var errs = result.Diagnostics.Count(d => d.Severity == DiagSeverity.Error);
                SetStatus($"✕ {errs} error(s) — fix and recompile.", error: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Compile exception: {ex.Message}", error: true);
        }
        finally
        {
            BtnCompile.IsEnabled = true;
        }
    }

    private async void BtnLoad_Click(object sender, RoutedEventArgs e)
    {
        if (Compiler is null) { SetStatus("Compiler not initialised.", error: true); return; }

        // If no successful compile yet, compile first then load in one shot
        if (_lastCompile is null || !_lastCompile.Success)
        {
            SetStatus("Compiling…", neutral: true);
            BtnLoad.IsEnabled    = false;
            BtnCompile.IsEnabled = false;

            try
            {
                var result = await Compiler.CompileAsync(CodeEditor.Document.Text);
                _lastCompile = result;
                ShowDiags(result.Diagnostics);

                if (!result.Success)
                {
                    var errs = result.Diagnostics.Count(d => d.Severity == DiagSeverity.Error);
                    SetStatus($"✕ {errs} error(s) — fix before loading.", error: true);
                    BtnCompile.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"✕ Compile error: {ex.Message}", error: true);
                BtnCompile.IsEnabled = true;
                return;
            }
            finally
            {
                BtnCompile.IsEnabled = true;
            }
        }

        // Load the compiled assembly
        BtnLoad.IsEnabled = false;
        try
        {
            var loaded = await Compiler.LoadAsync(_lastCompile!);
            if (loaded.Success)
            {
                RefreshLoadedBadge(Compiler.LoadedToolNames);
                SetStatus($"⚡ Loaded as \"{loaded.ToolName}\" — agent can use it now.", success: true);
                TbToolName.Text = loaded.ToolName ?? TbToolName.Text;
            }
            else
            {
                SetStatus($"✕ Load failed: {loaded.Error}", error: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Load exception: {ex.Message}", error: true);
        }
        finally
        {
            BtnLoad.IsEnabled = _lastCompile?.Success ?? false;
        }
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (Compiler is null)    { SetStatus("Compiler not initialised.", error: true); return; }
        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            SetStatus("✕ No workspace open — open a folder first.", error: true);
            return;
        }

        var toolId = TbToolName.Text.Trim();
        if (string.IsNullOrWhiteSpace(toolId))
        {
            SetStatus("✕ Enter a tool name before saving.", error: true);
            return;
        }

        try
        {
            await Compiler.SaveAsync(CodeEditor.Document.Text, toolId, WorkspaceRoot);
            var savePath = Path.Combine(WorkspaceRoot, ".orc", "tools", $"{toolId}.cs");
            SetStatus($"💾 Saved → .orc/tools/{toolId}.cs", success: true);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Save failed: {ex.Message}", error: true);
        }
    }

    // ── Diagnostic row click → jump to line ──────────────────────────────

    private void DiagRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DiagViewModel vm) return;
        if (vm.Line < 1) return;

        try
        {
            var line = CodeEditor.Document.GetLineByNumber(
                Math.Min(vm.Line, CodeEditor.Document.LineCount));
            CodeEditor.ScrollToLine(vm.Line);
            CodeEditor.Select(line.Offset, line.Length);
            CodeEditor.Focus();
        }
        catch { /* line out of range — ignore */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void LoadScaffold(string className = "MyTool", string toolId = "my_tool")
    {
        CodeEditor.Document.Text = ToolCompiler.Scaffold(className, toolId);
        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        CodeEditor.ScrollToHome();
        _lastCompile = null;
    }

    private void ShowDiags(List<ToolDiagnostic> diags)
    {
        _diags.Clear();
        foreach (var d in diags)
            _diags.Add(new DiagViewModel(d));

        TbNoDiags.Visibility = _diags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearDiags()
    {
        _diags.Clear();
        TbNoDiags.Visibility = Visibility.Visible;
    }

    private void SetStatus(string message, bool success = false, bool error = false, bool neutral = false)
    {
        TbStatus.Text = message;
        TbStatus.Foreground = error   ? new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36))
                            : success ? new SolidColorBrush(Color.FromRgb(0x6D, 0xB3, 0x6D))
                            :           new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    }

    private static string ToClassName(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return "MyTool";
        // "my_tool_name" → "MyToolName"
        return string.Concat(
            toolId.Split('_', '-', ' ')
                  .Where(s => s.Length > 0)
                  .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }
}

// ── DiagViewModel ─────────────────────────────────────────────────────────────

/// <summary>View-model wrapper around ToolDiagnostic — adds WPF-bindable color properties.</summary>
public class DiagViewModel
{
    private static readonly SolidColorBrush ErrorColor   = new(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly SolidColorBrush WarnColor    = new(Color.FromRgb(0xCC, 0xA7, 0x00));
    private static readonly SolidColorBrush InfoColor    = new(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly SolidColorBrush ErrorRowBg   = new(Color.FromArgb(0x20, 0xF4, 0x43, 0x36));
    private static readonly SolidColorBrush WarnRowBg    = new(Color.FromArgb(0x18, 0xCC, 0xA7, 0x00));
    private static readonly SolidColorBrush TransparentBg = new(Colors.Transparent);

    public DiagSeverity Severity     => _d.Severity;
    public string       Code         => _d.Code;
    public string       Message      => _d.Message;
    public int          Line         => _d.Line;
    public string       Location     => _d.Location;
    public string       SeverityIcon => _d.SeverityIcon;

    public Brush SeverityColor => Severity switch
    {
        DiagSeverity.Error   => ErrorColor,
        DiagSeverity.Warning => WarnColor,
        _                    => InfoColor,
    };

    public Brush RowBackground => Severity switch
    {
        DiagSeverity.Error   => ErrorRowBg,
        DiagSeverity.Warning => WarnRowBg,
        _                    => TransparentBg,
    };

    private readonly ToolDiagnostic _d;
    public DiagViewModel(ToolDiagnostic d) => _d = d;
}
