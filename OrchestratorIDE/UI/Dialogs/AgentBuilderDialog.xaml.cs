// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Which rules file was written when the dialog closed with DialogResult=true.
/// </summary>
public enum AgentBuilderTarget { None, WorkspaceRules, GlobalAgent }

/// <summary>
/// Unified dialog for creating or editing agent rules files (.agent.md).
/// Supports three modes: AI-Assisted generation, Preset picker, and Manual editing.
/// Can target either the workspace .agent.md or the global_agent.md.
/// </summary>
public partial class AgentBuilderDialog : Window
{
    // ── Injected dependencies ─────────────────────────────────────────────
    private readonly OllamaClient _ollama;
    private readonly string       _activeModel;
    private readonly string?      _workspaceRoot;

    // ── State ─────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private readonly List<AgentMessage> _history = [];
    private AgentPresets.Preset? _selectedPreset;

    /// <summary>Which target was written — valid when DialogResult == true.</summary>
    public AgentBuilderTarget AppliedTarget { get; private set; } = AgentBuilderTarget.None;

    // ── System prompt for AI generation ──────────────────────────────────
    private const string SystemPrompt = """
        You are an expert at writing .agent.md rules files for AI coding agents.

        A .agent.md file is a Markdown document that acts as persistent instructions for an AI agent. It should include:
        1. A title heading (# Agent Name or Project Name)
        2. Role description — what kind of work this agent does
        3. Project context — tech stack, key dependencies, build commands if relevant
        4. Coding standards and conventions specific to the project
        5. Workflow rules — how the agent should approach tasks
        6. Any domain-specific knowledge, constraints, or warnings

        Guidelines:
        - Be specific and actionable. Vague rules waste context.
        - 40–80 lines is ideal. Longer is only justified for complex projects.
        - Use Markdown headings (##) and bullet points.
        - Write in second person ("You are…", "Always…", "Never…").
        - Include concrete examples where useful (e.g. naming conventions, file patterns).
        - Do NOT include a preamble, explanation, or "Here is the file:".

        Output ONLY the Markdown file content, starting directly with # followed by the title.
        """;

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the agent builder dialog.
    /// </summary>
    /// <param name="ollama">OllamaClient to use for AI generation.</param>
    /// <param name="activeModel">Model ID for generation.</param>
    /// <param name="workspaceRoot">Workspace path for workspace-rules target; null disables that button.</param>
    public AgentBuilderDialog(OllamaClient ollama, string activeModel, string? workspaceRoot)
    {
        _ollama        = ollama;
        _activeModel   = activeModel;
        _workspaceRoot = workspaceRoot;

        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Context label in header
        if (!string.IsNullOrEmpty(_workspaceRoot))
            TbContextLabel.Text = $"— {System.IO.Path.GetFileName(_workspaceRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar))}";
        else
        {
            TbContextLabel.Text            = "— Global Agent";
            BtnApplyWorkspace.IsEnabled    = false;
            BtnApplyWorkspace.ToolTip      = "No workspace open";
        }

        // Disable Apply buttons until content is present
        UpdateApplyButtons();

        // Populate presets list
        foreach (var p in AgentPresets.All)
        {
            PresetList.Items.Add(new ListBoxItem
            {
                Tag     = p,
                Content = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Children    =
                    {
                        new TextBlock { Text = p.Icon, Margin = new Thickness(0,0,8,0), FontSize = 13 },
                        new TextBlock { Text = p.Name, FontSize = 12 }
                    }
                },
                ToolTip = p.Description
            });
        }

        // Load existing workspace rules file into Manual tab
        LoadManualContent();

        // Select AI tab by default
        NavList.SelectedIndex = 0;
    }

    private void LoadManualContent()
    {
        // Try workspace rules first, then global agent
        string content = "";

        if (!string.IsNullOrEmpty(_workspaceRoot))
        {
            var wsFile = System.IO.Path.Combine(_workspaceRoot, ".agent.md");
            if (System.IO.File.Exists(wsFile))
                content = System.IO.File.ReadAllText(wsFile);
        }

        if (string.IsNullOrEmpty(content) && System.IO.File.Exists(AgentPresets.GlobalAgentPath))
            content = System.IO.File.ReadAllText(AgentPresets.GlobalAgentPath);

        if (string.IsNullOrEmpty(content))
            content = "# Agent\n\nYou are a capable assistant.\n\n## Rules\n- Use write_file for all file output.\n- Run code after writing to verify it works.\n- Ask before making destructive changes.\n";

        TbManual.Text = content;
    }

    // ── Nav selection ─────────────────────────────────────────────────────

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;

        PnlAI.Visibility      = Visibility.Collapsed;
        PnlPresets.Visibility = Visibility.Collapsed;
        PnlManual.Visibility  = Visibility.Collapsed;

        switch (item.Tag as string)
        {
            case "ai":      PnlAI.Visibility      = Visibility.Visible; break;
            case "presets": PnlPresets.Visibility = Visibility.Visible; break;
            case "manual":  PnlManual.Visibility  = Visibility.Visible; break;
        }
    }

    // ── Preset panel ──────────────────────────────────────────────────────

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is ListBoxItem item && item.Tag is AgentPresets.Preset preset)
        {
            _selectedPreset         = preset;
            TbPreviewPreset.Text    = preset.Content;
            TbPresetDesc.Text       = preset.Description;
            UpdateApplyButtons();
        }
    }

    // ── AI generation ─────────────────────────────────────────────────────

    private void TbDescribe_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter triggers generation
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = RunGenerationAsync(isRefinement: false);
        }
    }

    private void TbRefine_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = RunGenerationAsync(isRefinement: true);
        }
    }

    private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        => _ = RunGenerationAsync(isRefinement: false);

    private void BtnRefine_Click(object sender, RoutedEventArgs e)
        => _ = RunGenerationAsync(isRefinement: true);

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Stopped.", "#CCA700");
    }

    private async Task RunGenerationAsync(bool isRefinement)
    {
        var description = isRefinement ? TbRefine.Text.Trim() : TbDescribe.Text.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            SetStatus(isRefinement
                ? "Enter a refinement instruction first."
                : "Enter a description of your project or agent role first.", "#CCA700");
            return;
        }

        // Cancel any previous generation
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        BtnGenerate.IsEnabled          = false;
        BtnRefine.IsEnabled            = false;
        BtnStop.IsEnabled              = true;
        TbAIHint.Visibility            = Visibility.Collapsed;

        if (!isRefinement)
        {
            // Fresh generation — reset history
            _history.Clear();
            _history.Add(new AgentMessage { Role = MessageRole.System, Content = SystemPrompt });
            _history.Add(new AgentMessage
            {
                Role    = MessageRole.User,
                Content = $"Create a .agent.md rules file for the following project/role:\n\n{description}"
            });
            TbPreviewAI.Text = "";
        }
        else
        {
            // Refinement — append to existing history
            _history.Add(new AgentMessage
            {
                Role    = MessageRole.User,
                Content = $"Refine the above .agent.md based on this feedback:\n\n{description}\n\nOutput ONLY the updated Markdown file content."
            });
            TbPreviewAI.Text = "";
        }

        SetStatus($"Generating with {_activeModel}…", "#CCA700");

        var sb = new StringBuilder();

        try
        {
            await foreach (var token in _ollama.StreamCompletionAsync(
                model:       _activeModel,
                history:     _history,
                temperature: 0.25,
                maxTokens:   2048,
                ct:          ct))
            {
                if (ct.IsCancellationRequested) break;

                sb.Append(token);
                var text = sb.ToString();

                Dispatcher.Invoke(() =>
                {
                    TbPreviewAI.Text = text;
                    TbPreviewAI.ScrollToEnd();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // User stopped — keep what was generated so far
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => SetStatus($"Generation error: {ex.Message}", "#F44747"));
        }
        finally
        {
            var result = sb.ToString().Trim();

            if (!string.IsNullOrEmpty(result))
            {
                // Add assistant response to history for follow-up refinements
                _history.Add(new AgentMessage
                {
                    Role    = MessageRole.Assistant,
                    Content = result
                });
            }

            Dispatcher.Invoke(() =>
            {
                BtnGenerate.IsEnabled  = true;
                BtnRefine.IsEnabled    = true;
                BtnStop.IsEnabled      = false;
                PnlRefine.Visibility   = Visibility.Visible;
                TbRefine.Text          = "";

                if (!string.IsNullOrEmpty(result))
                {
                    SetStatus("Generated — review, refine, or click Apply.", "#76B900");
                    UpdateApplyButtons();
                }
            });
        }
    }

    // ── Preview text changed ──────────────────────────────────────────────

    private void TbPreviewAI_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateApplyButtons();

    // ── Get current content based on active panel ─────────────────────────

    private string? GetActiveContent()
    {
        if (PnlAI.Visibility == Visibility.Visible)
        {
            var text = TbPreviewAI.Text.Trim();
            return text.Length > 0 ? text : null;
        }
        if (PnlPresets.Visibility == Visibility.Visible)
            return _selectedPreset?.Content;
        if (PnlManual.Visibility == Visibility.Visible)
        {
            var text = TbManual.Text.Trim();
            return text.Length > 0 ? text : null;
        }
        return null;
    }

    private void UpdateApplyButtons()
    {
        var hasContent = GetActiveContent() != null;
        BtnApplyGlobal.IsEnabled    = hasContent;
        // Workspace button only enabled if we have a workspace AND content
        BtnApplyWorkspace.IsEnabled = hasContent && !string.IsNullOrEmpty(_workspaceRoot);
    }

    // ── Apply buttons ─────────────────────────────────────────────────────

    private void BtnApplyGlobal_Click(object sender, RoutedEventArgs e)
    {
        var content = GetActiveContent();
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(AgentPresets.GlobalAgentPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(AgentPresets.GlobalAgentPath, content, Encoding.UTF8);
            AppliedTarget = AgentBuilderTarget.GlobalAgent;
            DialogResult  = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to write global agent file:\n{ex.Message}",
                "Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnApplyWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_workspaceRoot)) return;

        var content = GetActiveContent();
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            var path = System.IO.Path.Combine(_workspaceRoot, ".agent.md");
            System.IO.File.WriteAllText(path, content, Encoding.UTF8);
            AppliedTarget = AgentBuilderTarget.WorkspaceRules;
            DialogResult  = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to write workspace rules file:\n{ex.Message}",
                "Write Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetStatus(string msg, string hex = "#5A6A4A")
    {
        TbStatus.Text       = msg;
        TbStatus.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
