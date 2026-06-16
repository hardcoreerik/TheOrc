// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Panels;

public partial class ToolEditorPanel : UserControl
{
    public ToolCompiler? Compiler     { get; set; }
    public string?       WorkspaceRoot { get; set; }

    private readonly ObservableCollection<DiagViewModel> _diags = [];
    private CompileResult? _lastCompile;

    public ToolEditorPanel()
    {
        InitializeComponent();
        DiagList.ItemsSource = _diags;
        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        LoadScaffold();
    }

    public void RefreshLoadedBadge(IReadOnlyList<string> toolNames)
    {
        TbLoadedTools.Text = toolNames.Count == 0
            ? ""
            : $"⚡ Live: {string.Join(", ", toolNames)}";
    }

    private void BtnNew_Click(object? sender, RoutedEventArgs e)
    {
        var toolId    = TbToolName.Text?.Trim() ?? "";
        var className = ToClassName(toolId);
        LoadScaffold(className, string.IsNullOrWhiteSpace(toolId) ? "my_tool" : toolId);
        ClearDiags();
        SetStatus("Ready — scaffold loaded. Edit then hit ▶ Compile.", neutral: true);
    }

    private async void BtnCompile_Click(object? sender, RoutedEventArgs e)
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

    private async void BtnLoad_Click(object? sender, RoutedEventArgs e)
    {
        if (Compiler is null) { SetStatus("Compiler not initialised.", error: true); return; }

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

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        if (Compiler is null) { SetStatus("Compiler not initialised.", error: true); return; }
        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            SetStatus("✕ No workspace open — open a folder first.", error: true);
            return;
        }

        var toolId = TbToolName.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(toolId))
        {
            SetStatus("✕ Enter a tool name before saving.", error: true);
            return;
        }

        try
        {
            await Compiler.SaveAsync(CodeEditor.Document.Text, toolId, WorkspaceRoot);
            SetStatus($"💾 Saved → .orc/tools/{toolId}.cs", success: true);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Save failed: {ex.Message}", error: true);
        }
    }

    private void DiagRow_Click(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DiagViewModel vm) return;
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

    private void LoadScaffold(string className = "MyTool", string toolId = "my_tool")
    {
        CodeEditor.Document.Text      = ToolCompiler.Scaffold(className, toolId);
        CodeEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        CodeEditor.ScrollToHome();
        _lastCompile = null;
    }

    private void ShowDiags(List<ToolDiagnostic> diags)
    {
        _diags.Clear();
        foreach (var d in diags)
            _diags.Add(new DiagViewModel(d));

        TbNoDiags.IsVisible = _diags.Count == 0;
    }

    private void ClearDiags()
    {
        _diags.Clear();
        TbNoDiags.IsVisible = true;
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
        return string.Concat(
            toolId.Split('_', '-', ' ')
                  .Where(s => s.Length > 0)
                  .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }
}

// ── DiagViewModel (Avalonia — IBrush instead of WPF Brush) ───────────────────

public class DiagViewModel
{
    private static readonly IBrush ErrorColor    = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly IBrush WarnColor     = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
    private static readonly IBrush InfoColor     = new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
    private static readonly IBrush ErrorRowBg    = new SolidColorBrush(Color.FromArgb(0x20, 0xF4, 0x43, 0x36));
    private static readonly IBrush WarnRowBg     = new SolidColorBrush(Color.FromArgb(0x18, 0xCC, 0xA7, 0x00));
    private static readonly IBrush TransparentBg = new SolidColorBrush(Colors.Transparent);

    public DiagSeverity Severity     => _d.Severity;
    public string       Code         => _d.Code;
    public string       Message      => _d.Message;
    public int          Line         => _d.Line;
    public string       Location     => _d.Location;
    public string       SeverityIcon => _d.SeverityIcon;

    public IBrush SeverityColor => Severity switch
    {
        DiagSeverity.Error   => ErrorColor,
        DiagSeverity.Warning => WarnColor,
        _                    => InfoColor,
    };

    public IBrush RowBackground => Severity switch
    {
        DiagSeverity.Error   => ErrorRowBg,
        DiagSeverity.Warning => WarnRowBg,
        _                    => TransparentBg,
    };

    private readonly ToolDiagnostic _d;
    public DiagViewModel(ToolDiagnostic d) => _d = d;
}
